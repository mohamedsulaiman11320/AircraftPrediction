using System.Text.Json;
using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Application.Contracts;
using AFP.Application.Mapping;
using AFP.Domain;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Domain.Rules;
using Microsoft.Extensions.Options;

namespace AFP.Application.Services;

public interface IPipelineService
{
    Task<PagedResult<PipelineRunDto>> ListAsync(PipelineJobType? jobType, PipelineRunStatus? status, PageRequest page, CancellationToken ct);
    Task<PipelineRunDto> GetAsync(Guid id, CancellationToken ct);
    Task<JobAcceptedDto> StartJobAsync(StartJobRequest request, CancellationToken ct);
    Task<PipelineRunDto> CancelAsync(Guid id, CancellationToken ct);
    Task<PipelineRunDto> GetLatestDatasetAuditAsync(CancellationToken ct);
}

/// <summary>
/// Queues ML jobs. The API never runs Python: the separate ML worker process claims queued
/// <c>pipeline_run</c> rows and records their outcome.
/// </summary>
public sealed class PipelineService(
    IPipelineRunRepository runs, IActionAuditor auditor, IUnitOfWork unitOfWork, ICurrentUser currentUser, IClock clock) : IPipelineService
{
    public static readonly PipelineJobType[] QueueableJobs =
        [PipelineJobType.DatasetAudit, PipelineJobType.TrainAnomaly, PipelineJobType.Score, PipelineJobType.TrainFailure,
         PipelineJobType.TrainSensorForecast, PipelineJobType.ForecastSensors];

    /// <summary>Only parameters the Python worker actually honours are accepted (see docs/ML_PIPELINE.md).</summary>
    private static readonly HashSet<string> AllowedParameters = ["include_suspect", "full"];

    public async Task<PagedResult<PipelineRunDto>> ListAsync(PipelineJobType? jobType, PipelineRunStatus? status, PageRequest page, CancellationToken ct) =>
        (await runs.ListAsync(jobType, status, page.Normalize(), ct)).Map(r => r.ToDto());

    public async Task<PipelineRunDto> GetAsync(Guid id, CancellationToken ct) =>
        (await runs.GetAsync(id, ct) ?? throw new NotFoundException("Pipeline run", id)).ToDto();

    public async Task<JobAcceptedDto> StartJobAsync(StartJobRequest request, CancellationToken ct)
    {
        new Validator()
            .Must(request.JobType.HasValue && QueueableJobs.Contains(request.JobType.Value), "jobType",
                $"Job type must be one of: {string.Join(", ", QueueableJobs)}.")
            .ThrowIfInvalid();
        var parameters = ValidateParameters(request.Parameters);
        var jobType = request.JobType!.Value;

        if (await runs.HasPendingAsync(jobType, ct))
            throw new ConflictException($"A {jobType} job is already queued or running.", "job_already_pending");

        var run = new PipelineRun
        {
            JobType = jobType,
            Status = PipelineRunStatus.Queued,
            Trigger = PipelineTrigger.Api,
            RequestedByUserId = currentUser.RequireUserId(),
            RequestedAtUtc = clock.UtcNow,
            Parameters = Json.Serialize(parameters),
        };
        await runs.AddAsync(run, ct);
        await auditor.RecordAsync("PipelineJobQueued", nameof(PipelineRun), run.Id.ToString(), new { jobType = jobType.ToString(), parameters }, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return new JobAcceptedDto(run.Id, run.Status.ToString(), $"/api/v1/pipelines/runs/{run.Id}");
    }

    public async Task<PipelineRunDto> CancelAsync(Guid id, CancellationToken ct)
    {
        var run = await runs.GetAsync(id, ct) ?? throw new NotFoundException("Pipeline run", id);
        if (run.Status != PipelineRunStatus.Queued)
            throw new BusinessRuleException("Only queued runs can be cancelled.", "invalid_transition");
        run.Status = PipelineRunStatus.Cancelled;
        run.CompletedAtUtc = clock.UtcNow;
        await auditor.RecordAsync("PipelineJobCancelled", nameof(PipelineRun), run.Id.ToString(), null, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return run.ToDto();
    }

    public async Task<PipelineRunDto> GetLatestDatasetAuditAsync(CancellationToken ct) =>
        (await runs.GetLatestAsync(PipelineJobType.DatasetAudit, PipelineRunStatus.Succeeded, ct)
            ?? throw new NotFoundException("Dataset audit", "latest")).ToDto();

    private static Dictionary<string, object?> ValidateParameters(JsonElement? parameters)
    {
        var result = new Dictionary<string, object?>();
        if (parameters is not { ValueKind: JsonValueKind.Object } obj) return result;
        var v = new Validator();
        foreach (var prop in obj.EnumerateObject())
        {
            if (!AllowedParameters.Contains(prop.Name))
            {
                v.Add("parameters", $"Unsupported parameter '{prop.Name}'. Allowed: {string.Join(", ", AllowedParameters)}.");
                continue;
            }
            switch (prop.Name)
            {
                case "include_suspect" or "full" when prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    result[prop.Name] = prop.Value.GetBoolean(); break;
                default:
                    v.Add("parameters", $"Invalid value for '{prop.Name}'."); break;
            }
        }
        v.ThrowIfInvalid();
        return result;
    }
}

public interface IModelService
{
    Task<IReadOnlyList<ModelVersionDto>> ListAsync(ModelType? modelType, ModelStatus? status, CancellationToken ct);
    Task<ModelVersionDto> GetAsync(Guid id, CancellationToken ct);
    Task<ModelEvaluationDto> GetEvaluationAsync(Guid id, CancellationToken ct);
    Task<ModelVersionDto> RegisterAsync(Guid id, CancellationToken ct);
    Task<ModelVersionDto> ApproveAsync(Guid id, NotesRequest request, CancellationToken ct);
    Task<ModelVersionDto> RejectAsync(Guid id, NotesRequest request, CancellationToken ct);
    Task<ModelVersionDto> ActivateAsync(Guid id, CancellationToken ct);
    Task<ModelVersionDto> RetireAsync(Guid id, CancellationToken ct);
}

public sealed class ModelService(
    IModelVersionRepository models, IFailurePredictionStatusProvider readiness, IActionAuditor auditor, IUnitOfWork unitOfWork,
    ICurrentUser currentUser, IClock clock) : IModelService
{
    /// <summary>Evaluation metrics a failure-prediction model must carry before it can be approved.</summary>
    public static readonly string[] RequiredFailureMetrics = ["precision", "recall", "pr_auc", "false_alert_rate"];

    public async Task<IReadOnlyList<ModelVersionDto>> ListAsync(ModelType? modelType, ModelStatus? status, CancellationToken ct) =>
        (await models.ListAsync(modelType, status, ct)).Select(m => m.ToDto()).ToList();

    public async Task<ModelVersionDto> GetAsync(Guid id, CancellationToken ct) => (await Load(id, ct)).ToDto();

    public async Task<ModelEvaluationDto> GetEvaluationAsync(Guid id, CancellationToken ct)
    {
        var m = await Load(id, ct);
        var metrics = Json.Parse(m.Metrics);
        var available = metrics.ValueKind == JsonValueKind.Object && metrics.EnumerateObject().Any();
        return new ModelEvaluationDto(m.Id, m.ModelName, m.Version, m.ModelType, metrics, m.Limitations, available);
    }

    public Task<ModelVersionDto> RegisterAsync(Guid id, CancellationToken ct) =>
        Mutate(id, "ModelRegistered", m => ModelLifecycle.Register(m, clock.UtcNow), null, ct);

    public async Task<ModelVersionDto> ApproveAsync(Guid id, NotesRequest request, CancellationToken ct)
    {
        new Validator().Required("notes", request.Notes, "Approval notes").MaxLength("notes", request.Notes, 2000, "Notes").ThrowIfInvalid();
        var model = await Load(id, ct);
        if (model.ModelType == ModelType.FailurePrediction)
        {
            var status = await readiness.GetAsync(ct);
            if (status.Status == "Blocked")
                throw new BusinessRuleException("Failure prediction is BLOCKED: " + string.Join(" ", status.Reasons), "prediction_blocked");
            var metrics = Json.Parse(model.Metrics);
            var missing = RequiredFailureMetrics.Where(k => FindMetric(metrics, k) is null).ToList();
            if (missing.Count > 0)
                throw new BusinessRuleException($"Model is missing required evaluation metrics: {string.Join(", ", missing)}.", "missing_metrics");
            EnsureTestSplitSupportsItsMetrics(metrics);
        }
        if (model.ModelType == ModelType.SensorForecast)
        {
            // Only a forecaster whose backtest supported at least one horizon may be approved.
            var supported = Json.Find(Json.Parse(model.Metrics), "supported_horizons");
            if (supported is not { ValueKind: JsonValueKind.Object } s || !s.EnumerateObject().Any())
                throw new BusinessRuleException("No forecast horizon passed validation; this forecaster cannot be approved.", "no_supported_horizon");
        }
        return await Mutate(id, "ModelApproved", m =>
        {
            ModelLifecycle.Approve(m, currentUser.RequireUserId(), clock.UtcNow);
            m.Notes = AppendNote(m.Notes, $"Approved: {request.Notes!.Trim()}");
        }, new { request.Notes }, ct);
    }

    public Task<ModelVersionDto> RejectAsync(Guid id, NotesRequest request, CancellationToken ct)
    {
        new Validator().Required("notes", request.Notes, "Rejection notes").MaxLength("notes", request.Notes, 2000, "Notes").ThrowIfInvalid();
        return Mutate(id, "ModelRejected", m =>
        {
            ModelLifecycle.Reject(m);
            m.Notes = AppendNote(m.Notes, $"Rejected: {request.Notes!.Trim()}");
        }, new { request.Notes }, ct);
    }

    public async Task<ModelVersionDto> ActivateAsync(Guid id, CancellationToken ct)
    {
        var model = await Load(id, ct);
        if (model.Status != ModelStatus.Approved)
            throw new BusinessRuleException($"Only approved models can be activated (current status: {model.Status}).", "invalid_transition");
        var current = await models.GetActiveAsync(model.ModelType, ct);

        return await unitOfWork.ExecuteInTransactionAsync(async innerCt =>
        {
            // Retire first and save: the partial unique index allows only one Active model per type.
            if (current is not null && current.Id != model.Id)
            {
                ModelLifecycle.Retire(current, clock.UtcNow);
                await unitOfWork.SaveChangesAsync(innerCt);
            }
            ModelLifecycle.Activate(model, null, clock.UtcNow);
            await auditor.RecordAsync("ModelActivated", nameof(ModelVersion), model.Id.ToString(),
                new { model.ModelName, model.Version, retired = current?.Id }, innerCt);
            await unitOfWork.SaveChangesAsync(innerCt);
            return model.ToDto();
        }, ct);
    }

    public Task<ModelVersionDto> RetireAsync(Guid id, CancellationToken ct) =>
        Mutate(id, "ModelRetired", m => ModelLifecycle.Retire(m, clock.UtcNow), null, ct);

    private async Task<ModelVersionDto> Mutate(Guid id, string action, Action<ModelVersion> change, object? details, CancellationToken ct)
    {
        var model = await Load(id, ct);
        try
        {
            change(model);
        }
        catch (DomainException ex)
        {
            throw new BusinessRuleException(ex.Message, ex.Code);
        }
        await auditor.RecordAsync(action, nameof(ModelVersion), model.Id.ToString(),
            details ?? new { model.ModelName, model.Version, status = model.Status.ToString() }, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return model.ToDto();
    }

    /// <summary>Metrics may be flat or nested under a "test"/"evaluation" section.</summary>
    /// <summary>
    /// Smallest test split that can support the metrics a failure model is approved on. Precision and the
    /// false-alert rate are estimated entirely from negatives, so a handful of them cannot establish either,
    /// however high PR-AUC looks.
    /// </summary>
    public const int MinimumTestNegatives = 30;
    public const int MinimumTestPositives = 30;

    /// <summary>
    /// Refuses approval when the test split is too small or too one-sided to support its own metrics.
    /// <para>
    /// Added 2026-10-23 after a candidate reported PR-AUC 0.994 on a test split of 103 positives and
    /// <b>5</b> negatives: the "always predict failure" baseline scored 0.954 there, so the real lift was 1.04
    /// and precision/false-alert rate were unmeasurable. Readiness and metric-presence checks both passed, so
    /// nothing but human judgement stood in the way. This makes that judgement enforceable.
    /// </para>
    /// </summary>
    internal static void EnsureTestSplitSupportsItsMetrics(JsonElement metrics)
    {
        var negatives = ReadCount(metrics, "test.negatives");
        var positives = ReadCount(metrics, "test.positives");
        if (negatives is null || positives is null)
            throw new BusinessRuleException(
                "Model cannot be approved: its evaluation does not record the test-split class counts.",
                "evaluation_not_assessable");

        if (negatives < MinimumTestNegatives || positives < MinimumTestPositives)
            throw new BusinessRuleException(
                $"Model cannot be approved: the test split has {positives} positive and {negatives} negative example(s); " +
                $"at least {MinimumTestPositives} positives and {MinimumTestNegatives} negatives are required before " +
                "precision and false-alert rate are measurable.",
                "insufficient_test_examples");
    }

    private static int? ReadCount(JsonElement metrics, string path) =>
        Json.Find(metrics, path) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var count) ? count : null;

    internal static JsonElement? FindMetric(JsonElement metrics, string key)
    {
        foreach (var path in new[] { key, $"test.{key}", $"evaluation.{key}", $"selected.{key}" })
            if (Json.Find(metrics, path) is { ValueKind: JsonValueKind.Number } value) return value;
        return null;
    }

    private static string AppendNote(string? existing, string note) =>
        string.IsNullOrWhiteSpace(existing) ? note : $"{existing}\n{note}";

    private async Task<ModelVersion> Load(Guid id, CancellationToken ct) =>
        await models.GetAsync(id, ct) ?? throw new NotFoundException("Model version", id);
}

public interface IAuditQueryService
{
    Task<PagedResult<ActionAuditDto>> ListActionsAsync(ActionAuditFilter filter, PageRequest page, CancellationToken ct);
    Task<PagedResult<AuthAuditDto>> ListAuthAsync(AuthAuditFilter filter, PageRequest page, CancellationToken ct);
}

public sealed class AuditQueryService(IAuditRepository audit) : IAuditQueryService
{
    public async Task<PagedResult<ActionAuditDto>> ListActionsAsync(ActionAuditFilter filter, PageRequest page, CancellationToken ct) =>
        (await audit.ListActionsAsync(filter, page.Normalize(), ct)).Map(a => a.ToDto());

    public async Task<PagedResult<AuthAuditDto>> ListAuthAsync(AuthAuditFilter filter, PageRequest page, CancellationToken ct) =>
        (await audit.ListAuthAsync(filter, page.Normalize(), ct)).Map(a => a.ToDto());
}
