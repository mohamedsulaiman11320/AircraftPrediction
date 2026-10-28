using System.Text.Json;
using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Application.Contracts;
using AFP.Application.Mapping;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using Microsoft.Extensions.Options;

namespace AFP.Application.Services;

public interface IAnomalyService
{
    Task<PagedResult<AnomalyDto>> ListAsync(AnomalyFilter filter, PageRequest page, CancellationToken ct);
    Task<AnomalyDetailDto> GetAsync(long id, CancellationToken ct);
    Task<AnomalySummaryDto> GetSummaryAsync(int days, CancellationToken ct);
    Task<AnomalyDto> AcknowledgeAsync(long id, AcknowledgeAnomalyRequest request, CancellationToken ct);
    Task<AnomalyDto> InvestigateAsync(long id, InvestigateAnomalyRequest request, CancellationToken ct);
}

public sealed class AnomalyService(
    IAnomalyRepository anomalies, ITelemetryRepository telemetry, IActionAuditor auditor, IUnitOfWork unitOfWork,
    ICurrentUser currentUser, IClock clock) : IAnomalyService
{
    public async Task<PagedResult<AnomalyDto>> ListAsync(AnomalyFilter filter, PageRequest page, CancellationToken ct) =>
        (await anomalies.ListAsync(filter, page.Normalize(), ct)).Map(a => a.ToDto());

    public async Task<AnomalyDetailDto> GetAsync(long id, CancellationToken ct)
    {
        var anomaly = await anomalies.GetAsync(id, ct) ?? throw new NotFoundException("Anomaly", id);
        var record = anomaly.TelemetryRecordId is { } tid ? await telemetry.GetByIdAsync(tid, ct) : null;
        return new AnomalyDetailDto(anomaly.ToDto(), record?.ToDto());
    }

    public async Task<AnomalySummaryDto> GetSummaryAsync(int days, CancellationToken ct)
    {
        days = Math.Clamp(days <= 0 ? 30 : days, 1, 365);
        var from = clock.UtcNow.Date.AddDays(-days + 1);
        var rows = await anomalies.GetSinceAsync(from, ct);
        return BuildSummary(rows, from, days);
    }

    internal static AnomalySummaryDto BuildSummary(
        IReadOnlyList<(DateTime ObservedAtUtc, AnomalySeverity Severity, InvestigationStatus Status)> rows, DateTime from, int days)
    {
        var byDay = Enumerable.Range(0, days)
            .Select(i => from.AddDays(i))
            .Select(d => new DailyCountDto(d.ToString("yyyy-MM-dd"), rows.Count(r => r.ObservedAtUtc.Date == d)))
            .ToList();
        var bySeverity = Enum.GetValues<AnomalySeverity>().ToDictionary(s => s.ToString(), s => rows.Count(r => r.Severity == s));
        var open = rows.Count(r => r.Status is InvestigationStatus.New or InvestigationStatus.Acknowledged or InvestigationStatus.Investigating);
        return new AnomalySummaryDto(rows.Count, open, bySeverity, byDay);
    }

    public async Task<AnomalyDto> AcknowledgeAsync(long id, AcknowledgeAnomalyRequest request, CancellationToken ct)
    {
        new Validator().MaxLength("notes", request.Notes, 2000, "Notes").ThrowIfInvalid();
        var anomaly = await anomalies.GetAsync(id, ct) ?? throw new NotFoundException("Anomaly", id);
        if (anomaly.InvestigationStatus != InvestigationStatus.New)
            throw new BusinessRuleException($"Only new anomalies can be acknowledged (current: {anomaly.InvestigationStatus}).", "invalid_transition");

        anomaly.InvestigationStatus = InvestigationStatus.Acknowledged;
        anomaly.AcknowledgedByUserId = currentUser.RequireUserId();
        anomaly.AcknowledgedAtUtc = clock.UtcNow;
        anomaly.UpdatedAtUtc = clock.UtcNow;
        if (!string.IsNullOrWhiteSpace(request.Notes)) anomaly.InvestigationNotes = request.Notes.Trim();
        await auditor.RecordAsync("AnomalyAcknowledged", nameof(AircraftAnomalyResult), id.ToString(), new { request.Notes }, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return (await anomalies.GetAsync(id, ct))!.ToDto();
    }

    public async Task<AnomalyDto> InvestigateAsync(long id, InvestigateAnomalyRequest request, CancellationToken ct)
    {
        new Validator()
            .Must(request.Status is InvestigationStatus.Investigating or InvestigationStatus.Dismissed or InvestigationStatus.ConfirmedIssue,
                "status", "Status must be Investigating, Dismissed or ConfirmedIssue.")
            .Must(request.Status == InvestigationStatus.Investigating || !string.IsNullOrWhiteSpace(request.Notes),
                "notes", "Notes are required to dismiss or confirm an anomaly.")
            .MaxLength("notes", request.Notes, 2000, "Notes")
            .ThrowIfInvalid();

        var anomaly = await anomalies.GetAsync(id, ct) ?? throw new NotFoundException("Anomaly", id);
        var from = anomaly.InvestigationStatus;
        var allowed = request.Status == InvestigationStatus.Investigating
            ? from is InvestigationStatus.New or InvestigationStatus.Acknowledged
            : from is InvestigationStatus.New or InvestigationStatus.Acknowledged or InvestigationStatus.Investigating;
        if (!allowed)
            throw new BusinessRuleException($"Cannot move an anomaly from {from} to {request.Status}.", "invalid_transition");

        var now = clock.UtcNow;
        anomaly.InvestigationStatus = request.Status!.Value;
        if (anomaly.AcknowledgedByUserId is null)
        {
            anomaly.AcknowledgedByUserId = currentUser.RequireUserId();
            anomaly.AcknowledgedAtUtc = now;
        }
        if (!string.IsNullOrWhiteSpace(request.Notes)) anomaly.InvestigationNotes = request.Notes.Trim();
        anomaly.UpdatedAtUtc = now;
        await auditor.RecordAsync("AnomalyInvestigationUpdated", nameof(AircraftAnomalyResult), id.ToString(),
            new { from = from.ToString(), to = anomaly.InvestigationStatus.ToString(), request.Notes }, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return (await anomalies.GetAsync(id, ct))!.ToDto();
    }
}

/// <summary>
/// Determines whether failure prediction may be used. It is Blocked unless the latest dataset audit
/// found sufficient verified labels (or a reviewed model is already active).
/// </summary>
public interface IFailurePredictionStatusProvider
{
    Task<FailurePredictionStatusDto> GetAsync(CancellationToken ct);
}

public sealed class FailurePredictionStatusProvider(
    IPipelineRunRepository runs, IModelVersionRepository models, IOptions<PredictionOptions> options) : IFailurePredictionStatusProvider
{
    public const string ReadinessPath = "failure_prediction_readiness";

    public async Task<FailurePredictionStatusDto> GetAsync(CancellationToken ct)
    {
        var horizon = options.Value.HorizonDays;
        if (await models.GetActiveAsync(ModelType.FailurePrediction, ct) is { } active)
            return new FailurePredictionStatusDto("Active", [$"Model {active.ModelName} {active.Version} is active."], active.PredictionHorizonDays ?? horizon);

        var audit = await runs.GetLatestAsync(PipelineJobType.DatasetAudit, PipelineRunStatus.Succeeded, ct);
        if (audit?.ResultSummary is null)
            return new FailurePredictionStatusDto("Blocked",
                ["No dataset audit has been run. Run the DatasetAudit job to assess failure-label availability."], horizon);

        var root = Json.Parse(audit.ResultSummary);
        var readiness = Json.Find(root, ReadinessPath);
        var status = readiness is { } r ? Json.Find(r, "status")?.GetString() : null;
        var reasons = readiness is { } rr && Json.Find(rr, "reasons") is { ValueKind: JsonValueKind.Array } arr
            ? arr.EnumerateArray().Select(e => e.ToString()).ToList()
            : [];
        if (status == "Ready")
            return new FailurePredictionStatusDto("Ready",
                reasons.Count > 0 ? reasons : ["Dataset audit found sufficient verified labels. A trained model still requires approval."], horizon);
        if (reasons.Count == 0) reasons.Add("The latest dataset audit did not report sufficient verified failure labels.");
        return new FailurePredictionStatusDto("Blocked", reasons, horizon);
    }
}

public interface IPredictionService
{
    Task<PagedResult<PredictionDto>> ListAsync(PredictionFilter filter, PageRequest page, CancellationToken ct);
    Task<IReadOnlyList<PredictionDto>> ListForAircraftAsync(Guid aircraftId, CancellationToken ct);
    Task<PredictionMetadataDto> GetMetadataAsync(CancellationToken ct);
    Task<PredictionDto> ReviewAsync(Guid id, ReviewPredictionRequest request, CancellationToken ct);
    Task<PredictionDto> RecordOutcomeAsync(Guid id, RecordOutcomeRequest request, CancellationToken ct);
}

public sealed class PredictionService(
    IPredictionRepository predictions, IAircraftRepository aircraft, IModelVersionRepository models, IMaintenanceRepository maintenance,
    IFailurePredictionStatusProvider status, IActionAuditor auditor, IUnitOfWork unitOfWork, ICurrentUser currentUser, IClock clock,
    IOptions<PredictionOptions> options) : IPredictionService
{
    public async Task<PagedResult<PredictionDto>> ListAsync(PredictionFilter filter, PageRequest page, CancellationToken ct) =>
        (await predictions.ListAsync(filter, page.Normalize(), ct)).Map(p => p.ToDto());

    public async Task<IReadOnlyList<PredictionDto>> ListForAircraftAsync(Guid aircraftId, CancellationToken ct)
    {
        _ = await aircraft.GetByIdAsync(aircraftId, ct) ?? throw new NotFoundException("Aircraft", aircraftId);
        return (await predictions.ListForAircraftAsync(aircraftId, 20, ct)).Select(p => p.ToDto()).ToList();
    }

    public async Task<PredictionMetadataDto> GetMetadataAsync(CancellationToken ct)
    {
        var anomalyModel = await models.GetActiveAsync(ModelType.AnomalyDetection, ct);
        var failureModel = await models.GetActiveAsync(ModelType.FailurePrediction, ct);
        var categories = await predictions.ListRiskStatusesAsync(ct);
        return new PredictionMetadataDto(
            options.Value.HorizonDays, await status.GetAsync(ct), anomalyModel?.ToDto(), failureModel?.ToDto(),
            categories.Select(c => new RiskCategoryDto(c.Code, c.Description, c.DisplayOrder)).ToList());
    }

    public async Task<PredictionDto> ReviewAsync(Guid id, ReviewPredictionRequest request, CancellationToken ct)
    {
        new Validator()
            .Must(request.ApprovalStatus is ApprovalStatus.Approved or ApprovalStatus.Rejected, "approvalStatus", "Choose Approved or Rejected.")
            .Required("notes", request.Notes, "Review notes")
            .MaxLength("notes", request.Notes, 2000, "Review notes")
            .ThrowIfInvalid();
        var prediction = await predictions.GetAsync(id, ct) ?? throw new NotFoundException("Prediction", id);
        if (prediction.ApprovalStatus != ApprovalStatus.PendingReview)
            throw new BusinessRuleException($"Prediction has already been reviewed ({prediction.ApprovalStatus}).", "invalid_transition");

        prediction.ApprovalStatus = request.ApprovalStatus!.Value;
        prediction.ReviewedByUserId = currentUser.RequireUserId();
        prediction.ReviewedAtUtc = clock.UtcNow;
        prediction.ReviewNotes = request.Notes!.Trim();
        await auditor.RecordAsync("PredictionReviewed", nameof(AircraftRiskPrediction), id.ToString(),
            new { status = prediction.ApprovalStatus.ToString(), prediction.ReviewNotes }, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return (await predictions.GetAsync(id, ct))!.ToDto();
    }

    public async Task<PredictionDto> RecordOutcomeAsync(Guid id, RecordOutcomeRequest request, CancellationToken ct)
    {
        new Validator()
            .Must(request.ActualOutcome is MaintenanceOutcome.UnscheduledMaintenance or MaintenanceOutcome.NoUnscheduledMaintenance,
                "actualOutcome", "Outcome must be UnscheduledMaintenance or NoUnscheduledMaintenance.")
            .ThrowIfInvalid();
        var prediction = await predictions.GetAsync(id, ct) ?? throw new NotFoundException("Prediction", id);
        if (request.ActualOutcome == MaintenanceOutcome.NoUnscheduledMaintenance &&
            clock.UtcNow < prediction.PredictionTimestampUtc.AddDays(prediction.HorizonDays))
            throw new BusinessRuleException("'No unscheduled maintenance' can only be recorded after the prediction horizon has elapsed.");
        if (request.WorkOrderId is { } woId)
        {
            var wo = await maintenance.GetWorkOrderAsync(woId, ct) ?? throw new NotFoundException("Work order", woId);
            if (wo.AircraftId != prediction.AircraftId)
                throw new ValidationException("workOrderId", "Work order belongs to a different aircraft.");
        }

        prediction.ActualOutcome = request.ActualOutcome!.Value;
        prediction.OutcomeRecordedAtUtc = clock.UtcNow;
        prediction.OutcomeWorkOrderId = request.WorkOrderId;
        await auditor.RecordAsync("PredictionOutcomeRecorded", nameof(AircraftRiskPrediction), id.ToString(),
            new { outcome = prediction.ActualOutcome.ToString(), request.WorkOrderId }, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return (await predictions.GetAsync(id, ct))!.ToDto();
    }
}
