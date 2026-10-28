using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Application.Contracts;
using AFP.Application.Mapping;
using AFP.Domain.Entities;
using AFP.Domain.Enums;

namespace AFP.Application.Services;

public interface IDevelopmentLabService
{
    DevelopmentStatusDto GetStatus();
    Task<IReadOnlyList<ModelVersionDto>> ListModelsAsync(CancellationToken ct);
    Task<ModelEvaluationDto> GetEvaluationAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<PipelineRunDto>> ListRunsAsync(CancellationToken ct);
    Task<PipelineRunDto> GetRunAsync(Guid id, CancellationToken ct);
    Task<JobAcceptedDto> QueueJobAsync(DevelopmentJobRequest request, CancellationToken ct);
    Task<IReadOnlyList<PredictionDto>> ListPredictionsAsync(CancellationToken ct);
}

/// <summary>
/// The Data Engineer's window onto the isolated development (synthetic) environment: see candidates, their
/// evaluation and development predictions, and queue development training/evaluation/scoring for an ML worker
/// started with the development configuration.
/// <para>
/// It can never promote anything: there is no register/approve/activate here, nothing is written to the
/// operational registry, and the only operational write is the audit entry recording who queued what.
/// </para>
/// </summary>
public sealed class DevelopmentLabService(
    IDevelopmentDataSource development, IActionAuditor auditor, IUnitOfWork unitOfWork, ICurrentUser currentUser, IClock clock)
    : IDevelopmentLabService
{
    public static readonly PipelineJobType[] QueueableJobs =
        [PipelineJobType.DatasetAudit, PipelineJobType.TrainFailure, PipelineJobType.ScoreCandidate];

    public const string Notice =
        "Development environment on SYNTHETIC data. Results exercise the training and scoring pipeline only; they are " +
        "not evidence of real-world predictive performance and are never operational predictions.";

    private const int RunHistoryLimit = 50;

    public DevelopmentStatusDto GetStatus() =>
        new(development.IsConfigured, development.DatabaseName, Notice, QueueableJobs);

    public async Task<IReadOnlyList<ModelVersionDto>> ListModelsAsync(CancellationToken ct)
    {
        EnsureConfigured();
        return (await development.ListModelsAsync(ct)).Select(m => m.ToDto()).ToList();
    }

    public async Task<ModelEvaluationDto> GetEvaluationAsync(Guid id, CancellationToken ct)
    {
        EnsureConfigured();
        var m = await development.GetModelAsync(id, ct) ?? throw new NotFoundException("Development model", id);
        var metrics = Json.Parse(m.Metrics);
        var available = metrics.ValueKind == System.Text.Json.JsonValueKind.Object && metrics.EnumerateObject().Any();
        return new ModelEvaluationDto(m.Id, m.ModelName, m.Version, m.ModelType, metrics, m.Limitations, available);
    }

    public async Task<IReadOnlyList<PipelineRunDto>> ListRunsAsync(CancellationToken ct)
    {
        EnsureConfigured();
        return (await development.ListRunsAsync(RunHistoryLimit, ct)).Select(r => r.ToDto()).ToList();
    }

    public async Task<PipelineRunDto> GetRunAsync(Guid id, CancellationToken ct)
    {
        EnsureConfigured();
        return (await development.GetRunAsync(id, ct) ?? throw new NotFoundException("Development pipeline run", id)).ToDto();
    }

    public async Task<JobAcceptedDto> QueueJobAsync(DevelopmentJobRequest request, CancellationToken ct)
    {
        EnsureConfigured();
        new Validator()
            .Must(request.JobType.HasValue && QueueableJobs.Contains(request.JobType.Value), "jobType",
                $"Job type must be one of: {string.Join(", ", QueueableJobs)}.")
            .Must(request.JobType != PipelineJobType.ScoreCandidate || request.ModelVersionId.HasValue, "modelVersionId",
                "Choose the candidate model to score.")
            .ThrowIfInvalid();
        var jobType = request.JobType!.Value;
        var parameters = new Dictionary<string, object?>
        {
            // The development database has no users, so the requester is recorded here rather than as a foreign key.
            ["requested_by_user_id"] = currentUser.RequireUserId(),
        };
        if (jobType == PipelineJobType.ScoreCandidate)
        {
            var model = await development.GetModelAsync(request.ModelVersionId!.Value, ct)
                ?? throw new NotFoundException("Development model", request.ModelVersionId!.Value);
            if (model.ModelType != ModelType.FailurePrediction)
                throw new BusinessRuleException("Only failure-prediction candidates can be scored.", "not_a_failure_model");
            parameters["model_version_id"] = model.Id;
        }
        if (jobType == PipelineJobType.TrainFailure) parameters["target"] = "failure";

        if (await development.HasPendingAsync(jobType, ct))
            throw new ConflictException($"A development {jobType} job is already queued or running.", "job_already_pending");

        var run = new PipelineRun
        {
            JobType = jobType,
            Status = PipelineRunStatus.Queued,
            Trigger = PipelineTrigger.Api,
            RequestedByUserId = null,
            RequestedAtUtc = clock.UtcNow,
            Parameters = Json.Serialize(parameters),
        };
        // Two databases cannot share a transaction: the development run is committed first (AddRunAsync saves it),
        // so an audit entry is only ever written for a run that really exists.
        await development.AddRunAsync(run, ct);
        // Who queued what is recorded in the OPERATIONAL audit log, where the user exists.
        await auditor.RecordAsync("DevelopmentJobQueued", nameof(PipelineRun), run.Id.ToString(),
            new { environment = "development", database = development.DatabaseName, jobType = jobType.ToString(),
                  modelVersionId = request.ModelVersionId }, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return new JobAcceptedDto(run.Id, run.Status.ToString(), $"/api/v1/development/runs/{run.Id}");
    }

    public async Task<IReadOnlyList<PredictionDto>> ListPredictionsAsync(CancellationToken ct)
    {
        EnsureConfigured();
        return (await development.ListLatestModelPredictionsAsync(ct)).Select(p => p.ToDto()).ToList();
    }

    private void EnsureConfigured()
    {
        if (!development.IsConfigured)
            throw new BusinessRuleException(
                "The development environment is not configured (set DEV_DB_NAME in the root .env).", "development_not_configured");
    }
}
