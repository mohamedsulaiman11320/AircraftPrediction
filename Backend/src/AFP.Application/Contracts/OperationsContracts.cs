using System.Text.Json;
using AFP.Domain.Enums;

namespace AFP.Application.Contracts;

// ---- Telemetry -------------------------------------------------------------------------------

public sealed record TelemetryRecordDto(
    long Id, Guid AircraftId, string? SensorId, DateTime RecordedAtUtc,
    double? TemperatureValue, string? TemperatureUnit, double? TemperatureC,
    double? PressureValue, string? PressureUnit, double? PressureKpa,
    string DataSource, QualityStatus QualityStatus, IReadOnlyList<string> QualityFlags, DateTime IngestedAtUtc, Guid? ImportId,
    string? SourceRecordId, double? VibrationIndex, string? SourceStatusFlag);

/// <summary>
/// One bucketed observation. `PressurePsi` is the same reading as `PressureKpa`, converted once here at the
/// API boundary (TelemetryRowValidator.KpaToPsi) so clients never re-derive it; both are returned because
/// kPa is the stored canonical unit and PSI is what the engine-health business rules are written in.
/// A null means "no reading in this bucket" and must never be plotted as zero.
/// </summary>
public sealed record TelemetrySeriesPointDto(
    DateTime TimestampUtc, double? TemperatureC, double? PressureKpa, int Count,
    double? PressurePsi = null, double? VibrationIndex = null);
public sealed record TelemetrySeriesDto(Guid AircraftId, DateTime FromUtc, DateTime ToUtc, int BucketMinutes, IReadOnlyList<TelemetrySeriesPointDto> Points);

public sealed record TelemetryImportDto(
    Guid Id, string FileName, string FileSha256, TelemetryImportStatus Status, int TotalRows, int AcceptedRows,
    int RejectedRows, int DuplicateRows, int WarningCount, IReadOnlyList<string> Warnings, string? ErrorMessage,
    string RequestedBy, DateTime RequestedAtUtc, DateTime? StartedAtUtc, DateTime? CompletedAtUtc, TelemetryImportSource Source,
    // Lineage for the Data Engineer: where the original payload landed (MinIO Bronze key for raw JSON) and its size.
    string? StoredPath = null, long FileSizeBytes = 0);

public sealed record RejectedRowDto(int RowNumber, string RawLine, string Reasons);

/// <summary>Read-only preview of an automatic work-order pass. Produced without any write.</summary>
public sealed record AnomalyWorkOrderDryRunDto(
    bool Enabled, string MinimumSeverity, int MaxPerRun,
    AnomalyDryRunCountsDto Counts, IReadOnlyList<AnomalyDryRunRowDto> Anomalies);

public sealed record AnomalyDryRunCountsDto(
    int Total, int WouldCreate, int AlreadyTicketed, int BelowThreshold,
    int Dismissed, int NotAnomaly, int DeferredByCap, int Disabled,
    int TooOld = 0);   // added 2026-10-27: observed before the automatic-ticket age window

public sealed record AnomalyDryRunRowDto(
    long AnomalyId, string RegistrationNumber, string Severity, DateTime ObservedAtUtc,
    string InvestigationStatus, bool WouldCreateWorkOrder, string? ExclusionReason, string? Priority);

/// <summary>Outcome of one automatic work-order generation pass.</summary>
public sealed record AnomalyWorkOrderRunDto(
    int Considered, int Created, IReadOnlyList<string> WorkOrderNumbers, string? Note, int DiagnosisFailures = 0);

/// <summary>What deleting an import would remove, and what it deliberately keeps. Read-only.</summary>
public sealed record ImportDeletionPlanDto(
    Guid ImportId, string FileName, TelemetryImportStatus Status,
    int BronzeObjects, int TelemetryRecords, int RejectedRows, int PipelineRuns,
    int SilverPartitionsRemoved, int GoldPartitionsRemoved,
    IReadOnlyList<string> Preserved);

/// <summary>
/// Outcome of a deletion. <paramref name="Completed"/> is false while <paramref name="RemainingWork"/>
/// is non-empty — the database half may have succeeded while object storage still holds data, and the
/// operation must not be reported as successful until everything required is gone. Re-running is safe.
/// </summary>
public sealed record ImportDeletionResultDto(
    Guid ImportId, bool Completed, int DatabaseRecordsRemoved, int ObjectsRemoved,
    IReadOnlyList<string> Preserved, IReadOnlyList<string> RemainingWork);
public sealed record JobAcceptedDto(Guid Id, string Status, string StatusUrl);

public sealed record AircraftDataQualityDto(Guid AircraftId, string RegistrationNumber, int Records, int SuspectRecords, DateTime FirstRecordUtc, DateTime LastRecordUtc);
public sealed record ImportTotalsDto(int Count, int AcceptedRows, int RejectedRows, int DuplicateRows);
public sealed record DataQualityReportDto(
    int TotalRecords, int ValidRecords, int SuspectRecords, int MissingTemperature, int MissingPressure,
    DateTime? FirstRecordUtc, DateTime? LastRecordUtc, IReadOnlyList<AircraftDataQualityDto> ByAircraft, ImportTotalsDto Imports);

// ---- Anomalies -------------------------------------------------------------------------------

public record AnomalyDto(
    long Id, Guid AircraftId, string RegistrationNumber, string? SensorId, long? TelemetryRecordId, DateTime ObservedAtUtc,
    double? AnomalyScore, AnomalySeverity Severity, string ModelName, string ModelVersion, Guid? ModelVersionId,
    DateTime ScoredAtUtc, InvestigationStatus InvestigationStatus, string? AcknowledgedBy, DateTime? AcknowledgedAtUtc,
    string? InvestigationNotes, JsonElement FeatureValues, string ScoreNote,
    // Added 2026-10-27 (additive). AnomalyScore/ModelVersionId are null for a ThresholdRule anomaly.
    AnomalySource Source = AnomalySource.IsolationForest, string? RuleCodes = null, JsonElement? Evidence = null,
    DateTime? LastObservedAtUtc = null);

public sealed record AnomalyDetailDto : AnomalyDto
{
    public AnomalyDetailDto(AnomalyDto anomaly, TelemetryRecordDto? telemetry) : base(anomaly) => Telemetry = telemetry;

    public TelemetryRecordDto? Telemetry { get; init; }
}

public sealed record AcknowledgeAnomalyRequest(string? Notes);
public sealed record InvestigateAnomalyRequest(InvestigationStatus? Status, string? Notes);
public sealed record DailyCountDto(string Date, int Count);
public sealed record AnomalySummaryDto(int Total, int Open, IReadOnlyDictionary<string, int> BySeverity, IReadOnlyList<DailyCountDto> ByDay);

// ---- Predictions -----------------------------------------------------------------------------

public sealed record PredictionDto(
    Guid Id, Guid AircraftId, string RegistrationNumber, string? ComponentId, DateTime PredictionTimestampUtc, int HorizonDays,
    string RiskCategory, PredictionBasis Basis, double? Probability, bool ProbabilityValidated, JsonElement FeatureSnapshot,
    string? ModelName, string? ModelVersion, ApprovalStatus ApprovalStatus, string? ReviewedBy, DateTime? ReviewedAtUtc,
    string? ReviewNotes, MaintenanceOutcome ActualOutcome, DateTime? OutcomeRecordedAtUtc, string Disclaimer,
    PredictionState PredictionState, DateTime FeatureCutoffUtc);

public sealed record ReviewPredictionRequest(ApprovalStatus? ApprovalStatus, string? Notes);
public sealed record RecordOutcomeRequest(MaintenanceOutcome? ActualOutcome, Guid? WorkOrderId);
public sealed record RiskCategoryDto(string Code, string Description, int DisplayOrder);
public sealed record FailurePredictionStatusDto(string Status, IReadOnlyList<string> Reasons, int HorizonDays);
public sealed record PredictionMetadataDto(
    int ConfiguredHorizonDays, FailurePredictionStatusDto FailurePredictionStatus,
    ModelVersionDto? ActiveAnomalyModel, ModelVersionDto? ActiveFailureModel, IReadOnlyList<RiskCategoryDto> RiskCategories);

// ---- Maintenance -----------------------------------------------------------------------------

public record WorkOrderDto(
    Guid Id, string WorkOrderNumber, Guid AircraftId, string RegistrationNumber, string? ComponentId, long? AnomalyId,
    Guid? PredictionId, WorkOrderPriority Priority, string Title, string Description, WorkOrderStatus Status,
    long? AssignedTechnicianId, string? AssignedTechnicianName, string CreatedBy, DateTime CreatedAtUtc, DateTime? DueAtUtc,
    bool IsOverdue, string? InspectionFindings, string? CorrectiveAction, bool? ActualFailureConfirmed, string? ResolutionNotes,
    DateTime? ResolvedAtUtc, DateTime? ClosedAtUtc, DateTime UpdatedAtUtc, IReadOnlyList<string> AllowedTransitions, Guid ConcurrencyStamp,
    // Added 2026-10-27 (additive): where the ticket came from and the diagnosis snapshot stored when it was created.
    WorkOrderSource Source = WorkOrderSource.Manual, DiagnosisStatus DiagnosisStatus = DiagnosisStatus.NotGenerated,
    string? DiagnosisText = null, string? DiagnosisMethod = null, string? DiagnosisRuleVersion = null,
    DateTime? DiagnosisGeneratedAtUtc = null, string? DiagnosisError = null, string? DispatcherReviewNotes = null);

/// <summary>One immutable technician report (append-only; a re-report after "send back" is the next sequence).</summary>
public sealed record WorkOrderReportDto(
    int Sequence, long TechnicianUserId, string? TechnicianName, DateTime SubmittedAtUtc, string? ActionCode,
    string? InspectionFindings, string CorrectiveAction, string ResolutionNotes, bool ActualFailureConfirmed);

/// <summary>An approved maintenance action code a technician may report.</summary>
public sealed record MaintenanceActionCodeDto(string Code, string Description);

public sealed record WorkOrderDetailDto : WorkOrderDto
{
    public WorkOrderDetailDto(
        WorkOrderDto workOrder, IReadOnlyList<ActionAuditDto> history, AircraftSummaryDto aircraft, AnomalyDto? anomaly, PredictionDto? prediction,
        IReadOnlyList<WorkOrderReportDto>? reports = null, JsonElement? diagnosisEvidence = null)
        : base(workOrder)
    {
        History = history;
        Aircraft = aircraft;
        Anomaly = anomaly;
        Prediction = prediction;
        Reports = reports ?? [];
        DiagnosisEvidence = diagnosisEvidence;
    }

    /// <summary>Every technician report, oldest first (added 2026-10-27).</summary>
    public IReadOnlyList<WorkOrderReportDto> Reports { get; init; }

    /// <summary>The evidence the stored diagnosis was generated from (added 2026-10-27).</summary>
    public JsonElement? DiagnosisEvidence { get; init; }

    public IReadOnlyList<ActionAuditDto> History { get; init; }

    /// <summary>Aircraft the work order is for (so technicians need no fleet access).</summary>
    public AircraftSummaryDto Aircraft { get; init; }

    /// <summary>AI diagnosis: the linked anomaly result (score is not a probability).</summary>
    public AnomalyDto? Anomaly { get; init; }

    /// <summary>AI diagnosis: the linked risk prediction with its disclaimer.</summary>
    public PredictionDto? Prediction { get; init; }
}

public sealed record CreateWorkOrderRequest(
    Guid? AircraftId, string? ComponentId, long? AnomalyId, Guid? PredictionId, WorkOrderPriority? Priority,
    string? Title, string? Description, DateTime? DueAtUtc, long? AssignedTechnicianId);

public sealed record UpdateWorkOrderRequest(
    Guid? ConcurrencyStamp, string? Title, string? Description, WorkOrderPriority? Priority, DateTime? DueAtUtc, string? ComponentId);

public sealed record AssignWorkOrderRequest(long? TechnicianId);
public sealed record RecordInspectionRequest(string? InspectionFindings);
public sealed record ResolveWorkOrderRequest(
    string? CorrectiveAction, bool? ActualFailureConfirmed, string? ResolutionNotes,
    string? ActionCode = null);   // required since 2026-10-27: an active code from maintenance_action_code
public sealed record CloseWorkOrderRequest(string? ResolutionNotes);
public sealed record ReasonRequest(string? Reason);

public sealed record MaintenanceLogDto(
    Guid Id, Guid AircraftId, string RegistrationNumber, string? ComponentId, MaintenanceEventType EventType, DateTime OccurredAtUtc,
    string Description, bool? ActualFailureConfirmed, bool IsVerified, MaintenanceLogSource Source, Guid? WorkOrderId);

public sealed record CreateMaintenanceLogRequest(
    Guid? AircraftId, string? ComponentId, MaintenanceEventType? EventType, DateTime? OccurredAtUtc, string? Description,
    bool? ActualFailureConfirmed, bool IsVerified);

public sealed record TechnicianDto(long Id, string DisplayName, string Username);

// ---- Pipelines, models, audit ----------------------------------------------------------------

public sealed record PipelineRunDto(
    Guid Id, PipelineJobType JobType, PipelineRunStatus Status, PipelineTrigger Trigger, string? RequestedBy, DateTime RequestedAtUtc,
    DateTime? StartedAtUtc, DateTime? CompletedAtUtc, JsonElement Parameters, JsonElement? ResultSummary, string? ErrorMessage,
    Guid? ModelVersionId, int? RecordsProcessed);

public sealed record StartJobRequest(PipelineJobType? JobType, JsonElement? Parameters);

public sealed record ModelVersionDto(
    Guid Id, string ModelName, string Version, ModelType ModelType, string Algorithm, ModelStatus Status, string ArtifactSha256,
    JsonElement FeatureNames, JsonElement Hyperparameters, JsonElement Metrics, DateTime? TrainingDataFromUtc, DateTime? TrainingDataToUtc,
    int TrainingRowCount, int? PredictionHorizonDays, string? Limitations, string? Notes, DateTime CreatedAtUtc, DateTime? RegisteredAtUtc,
    string? ApprovedBy, DateTime? ApprovedAtUtc, DateTime? ActivatedAtUtc, DateTime? RetiredAtUtc, string? ArtifactUri = null);

public sealed record ModelEvaluationDto(
    Guid ModelVersionId, string ModelName, string Version, ModelType ModelType, JsonElement Metrics, string? Limitations, bool EvaluationAvailable);

public sealed record NotesRequest(string? Notes);

public sealed record ActionAuditDto(
    long Id, long? UserId, string? UserName, string Action, string EntityType, string EntityId, JsonElement? Details,
    DateTime OccurredAtUtc, string? CorrelationId);

public sealed record AuthAuditDto(
    long Id, long? UserId, string? Email, string EventType, bool Succeeded, string? IpAddress, string? UserAgent,
    string? Detail, DateTime OccurredAtUtc);

/// <summary>The isolated development (synthetic) environment as the Data Engineer sees it.</summary>
public sealed record DevelopmentStatusDto(
    bool Configured, string? Database, string Notice, IReadOnlyList<PipelineJobType> QueueableJobs);

/// <summary>Queue a development job. <c>ModelVersionId</c> is required for <c>ScoreCandidate</c> only.</summary>
public sealed record DevelopmentJobRequest(PipelineJobType? JobType, Guid? ModelVersionId);
