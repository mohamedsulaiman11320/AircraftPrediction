using AFP.Application.Common;
using AFP.Application.Contracts;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Domain.Rules;

namespace AFP.Application.Mapping;

/// <summary>Entity â†’ DTO mapping. Entities are never exposed through the API.</summary>
public static class Mappers
{
    public const string AnomalyScoreNote =
        "Isolation Forest anomaly score: higher means more unusual relative to the training data. " +
        "It is not a probability of failure.";

    public const string ThresholdRuleNote =
        "Raised by an approved operating limit from measured telemetry (see Evidence). It has no score and is " +
        "not a probability of failure.";

    public const string DescriptiveDisclaimer =
        "Descriptive indicator derived from recent anomaly activity. It is not a calibrated failure probability " +
        "and must be reviewed by maintenance engineering.";

    public const string ModelDisclaimer =
        "Model-based risk estimate for the stated horizon. Requires engineering review; it does not determine airworthiness.";

    private const string Unknown = "Unknown";
    private const string EtlDiscovered = "ETL (placed directly in object storage)";

    public static UserDto ToDto(this AppUser u) =>
        new(u.Id, u.Username, u.Email, u.DisplayName, u.ActiveRoles, u.Status, u.CreatedAtUtc, u.LastLoginAtUtc);

    public static AdminUserDto ToAdminDto(this AppUser u) =>
        new(u.Id, u.Username, u.Email, u.DisplayName, u.ActiveRoles, u.Status, u.CreatedAtUtc, u.LastLoginAtUtc,
            u.ApprovedAtUtc, u.FailedLoginCount, u.LockoutEndUtc, u.DisabledReason);

    public static LatestRiskDto? ToLatestRisk(this AircraftRiskPrediction? p) =>
        p is null ? null : new(p.RiskCategory, p.Basis, p.PredictionTimestampUtc, p.HorizonDays);

    public static AircraftSummaryDto ToSummaryDto(
        this Aircraft a, AircraftCountersView counters, AircraftRiskPrediction? latest, AircraftHealthDto? health = null) =>
        new(a.Id, a.RegistrationNumber, a.Model, a.Manufacturer, a.FleetGroup, a.OperationalStatus, a.StatusReason,
            a.LastTelemetryAtUtc, a.LastInspectionAtUtc, counters.OpenAnomalies, counters.ActiveFlags, counters.OpenWorkOrders,
            latest.ToLatestRisk(), health,
            FleetStatusRules.Derive(a, counters.WorkOrdersInProgress, counters.ActiveFlags));

    public static InspectionFlagDto ToDto(this InspectionFlag f) =>
        new(f.Id, f.AircraftId, f.Aircraft?.RegistrationNumber ?? Unknown, f.Severity, f.Reason, f.RaisedBy?.DisplayName ?? Unknown,
            f.RaisedAtUtc, f.IsActive, f.ClearedBy?.DisplayName, f.ClearedAtUtc, f.ClearanceNotes, f.AnomalyId, f.PredictionId, f.WorkOrderId);

    public static MaintenanceLogDto ToDto(this MaintenanceLog m) =>
        new(m.Id, m.AircraftId, m.Aircraft?.RegistrationNumber ?? Unknown, m.ComponentId, m.EventType, m.OccurredAtUtc,
            m.Description, m.ActualFailureConfirmed, m.IsVerified, m.Source, m.WorkOrderId);

    public static TelemetryRecordDto ToDto(this TelemetryRecord t) =>
        new(t.Id, t.AircraftId, t.SensorId, t.RecordedAtUtc, t.TemperatureValue, t.TemperatureUnit, t.TemperatureC,
            t.PressureValue, t.PressureUnit, t.PressureKpa, t.DataSource, t.QualityStatus,
            string.IsNullOrEmpty(t.QualityFlags) ? [] : t.QualityFlags.Split(',', StringSplitOptions.RemoveEmptyEntries),
            t.IngestedAtUtc, t.ImportId, t.SourceRecordId, t.VibrationIndex, t.SourceStatusFlag);

    public static TelemetryImportDto ToDto(this TelemetryImport i) =>
        new(i.Id, i.FileName, i.FileSha256, i.Status, i.TotalRows, i.AcceptedRows, i.RejectedRows, i.DuplicateRows,
            i.WarningCount, i.Warnings, i.ErrorMessage, i.RequestedBy?.DisplayName ?? (i.RequestedByUserId is null ? EtlDiscovered : Unknown),
            i.RequestedAtUtc, i.StartedAtUtc, i.CompletedAtUtc, i.Source,
            string.IsNullOrWhiteSpace(i.StoredPath) ? null : i.StoredPath, i.FileSizeBytes);

    public static AnomalyDto ToDto(this AircraftAnomalyResult a) =>
        new(a.Id, a.AircraftId, a.Aircraft?.RegistrationNumber ?? Unknown, a.SensorId, a.TelemetryRecordId, a.ObservedAtUtc,
            a.AnomalyScore, a.Severity, a.ModelVersion?.ModelName ?? Unknown, a.ModelVersion?.Version ?? Unknown, a.ModelVersionId,
            a.ScoredAtUtc, a.InvestigationStatus, a.AcknowledgedBy?.DisplayName, a.AcknowledgedAtUtc, a.InvestigationNotes,
            Json.Parse(a.FeatureValues), a.Source == AnomalySource.ThresholdRule ? ThresholdRuleNote : AnomalyScoreNote,
            a.Source, a.RuleCodes, Json.ParseOptional(a.Evidence), a.LastObservedAtUtc);

    public static PredictionDto ToDto(this AircraftRiskPrediction p) =>
        new(p.Id, p.AircraftId, p.Aircraft?.RegistrationNumber ?? Unknown, p.ComponentId, p.PredictionTimestampUtc, p.HorizonDays,
            p.RiskCategory, p.Basis,
            // Defence in depth: an unvalidated probability is never returned even if one was stored.
            p.ProbabilityValidated ? p.Probability : null,
            p.ProbabilityValidated, Json.Parse(p.FeatureSnapshot), p.ModelVersion?.ModelName, p.ModelVersion?.Version,
            p.ApprovalStatus, p.ReviewedBy?.DisplayName, p.ReviewedAtUtc, p.ReviewNotes, p.ActualOutcome, p.OutcomeRecordedAtUtc,
            p.Basis == Domain.Enums.PredictionBasis.Model ? ModelDisclaimer : DescriptiveDisclaimer,
            p.PredictionState, p.FeatureCutoffUtc);

    /// <summary><c>allowedTransitions</c> is filtered for the caller: dispatchers plan/close, only the assigned technician works the ticket.</summary>
    public static WorkOrderDto ToDto(this MaintenanceWorkOrder w, DateTime nowUtc, Abstractions.ICurrentUser caller) =>
        new(w.Id, w.WorkOrderNumber, w.AircraftId, w.Aircraft?.RegistrationNumber ?? Unknown, w.ComponentId, w.AnomalyId,
            w.PredictionId, w.Priority, w.Title, w.Description, w.Status, w.AssignedTechnicianId, w.AssignedTechnician?.DisplayName,
            w.CreatedBy?.DisplayName ?? Unknown, w.CreatedAtUtc, w.DueAtUtc, w.IsOverdue(nowUtc), w.InspectionFindings,
            w.CorrectiveAction, w.ActualFailureConfirmed, w.ResolutionNotes, w.ResolvedAtUtc, w.ClosedAtUtc, w.UpdatedAtUtc,
            WorkOrderWorkflow.AllowedTransitions(w.Status, caller.IsDispatcher, IsAssignedTechnician(w, caller)), w.ConcurrencyStamp,
            w.Source, w.DiagnosisStatus, w.DiagnosisText, w.DiagnosisMethod, w.DiagnosisRuleVersion, w.DiagnosisGeneratedAtUtc,
            w.DiagnosisError, w.DispatcherReviewNotes);

    public static WorkOrderReportDto ToDto(this WorkOrderReport r) =>
        new(r.Sequence, r.TechnicianUserId, r.Technician?.DisplayName, r.SubmittedAtUtc, r.ActionCode, r.InspectionFindings,
            r.CorrectiveAction, r.ResolutionNotes, r.ActualFailureConfirmed);

    public static bool IsAssignedTechnician(MaintenanceWorkOrder w, Abstractions.ICurrentUser caller) =>
        caller.UserId is { } id && w.AssignedTechnicianId == id && caller.Roles.Contains(Domain.Enums.UserRole.MaintenanceTechnician);

    public static PipelineRunDto ToDto(this PipelineRun r) =>
        new(r.Id, r.JobType, r.Status, r.Trigger, r.RequestedBy?.DisplayName, r.RequestedAtUtc, r.StartedAtUtc, r.CompletedAtUtc,
            Json.Parse(r.Parameters), Json.ParseOptional(r.ResultSummary), r.ErrorMessage, r.ModelVersionId, r.RecordsProcessed);

    public static ModelVersionDto ToDto(this ModelVersion m) =>
        new(m.Id, m.ModelName, m.Version, m.ModelType, m.Algorithm, m.Status, m.ArtifactSha256,
            Json.Parse(m.FeatureNames, Json.Parse("[]")), Json.Parse(m.Hyperparameters), Json.Parse(m.Metrics),
            m.TrainingDataFromUtc, m.TrainingDataToUtc, m.TrainingRowCount, m.PredictionHorizonDays, m.Limitations, m.Notes,
            m.CreatedAtUtc, m.RegisteredAtUtc, m.ApprovedBy?.DisplayName, m.ApprovedAtUtc, m.ActivatedAtUtc, m.RetiredAtUtc,
            m.ArtifactUri);

    public static ActionAuditDto ToDto(this ActionAudit a) =>
        new(a.Id, a.UserId, a.User?.DisplayName, a.Action, a.EntityType, a.EntityId, Json.ParseOptional(a.Details),
            a.OccurredAtUtc, a.CorrelationId);

    public static AuthAuditDto ToDto(this AuthAudit a) =>
        new(a.Id, a.UserId, a.Email, a.EventType, a.Succeeded, a.IpAddress, a.UserAgent, a.Detail, a.OccurredAtUtc);
}

/// <summary>Counter values for one aircraft, defaulting to zero when no row exists.</summary>
public readonly record struct AircraftCountersView(int OpenAnomalies, int OpenHighAnomalies, int ActiveFlags, int OpenWorkOrders,
    int WorkOrdersInProgress)
{
    public static AircraftCountersView From(Abstractions.AircraftCounters? c) =>
        c is null ? default : new(c.OpenAnomalies, c.OpenHighAnomalies, c.ActiveFlags, c.OpenWorkOrders, c.WorkOrdersInProgress);
}
