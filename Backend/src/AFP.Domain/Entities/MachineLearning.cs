using AFP.Domain.Enums;

namespace AFP.Domain.Entities;

public class RiskStatus : ILifecycleEntity
{
    public LifecycleStatus StatusId { get; set; } = LifecycleStatus.Active;
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
}

public class ModelVersion : ILifecycleEntity
{
    public LifecycleStatus StatusId { get; set; } = LifecycleStatus.Active;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ModelName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public ModelType ModelType { get; set; }
    public string Algorithm { get; set; } = string.Empty;
    public ModelStatus Status { get; set; } = ModelStatus.Trained;
    public string ArtifactPath { get; set; } = string.Empty;
    public string ArtifactSha256 { get; set; } = string.Empty;
    /// <summary>Durable copy in the environment's MinIO bucket (s3://bucket/key); null for artifacts held locally only.</summary>
    public string? ArtifactUri { get; set; }
    public string FeatureNames { get; set; } = "[]";
    public string Hyperparameters { get; set; } = "{}";
    public string Metrics { get; set; } = "{}";
    public DateTime? TrainingDataFromUtc { get; set; }
    public DateTime? TrainingDataToUtc { get; set; }
    public int TrainingRowCount { get; set; }
    public int? PredictionHorizonDays { get; set; }
    public string? Limitations { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? RegisteredAtUtc { get; set; }
    public long? ApprovedByUserId { get; set; }
    public AppUser? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public DateTime? ActivatedAtUtc { get; set; }
    public DateTime? RetiredAtUtc { get; set; }
    public Guid? TrainingRunId { get; set; }
}

public class AircraftAnomalyResult : ILifecycleEntity
{
    public LifecycleStatus StatusId { get; set; } = LifecycleStatus.Active;
    public long Id { get; set; }
    public Guid AircraftId { get; set; }
    public Aircraft? Aircraft { get; set; }
    public long? TelemetryRecordId { get; set; }
    public TelemetryRecord? TelemetryRecord { get; set; }
    public string? SensorId { get; set; }
    public DateTime ObservedAtUtc { get; set; }
    /// <summary>Isolation Forest score; null for rule-driven anomalies (they have no score).</summary>
    public double? AnomalyScore { get; set; }
    public bool IsAnomaly { get; set; }
    public AnomalySeverity Severity { get; set; }
    /// <summary>The model that produced it; null for a ThresholdRule anomaly.</summary>
    public Guid? ModelVersionId { get; set; }
    public ModelVersion? ModelVersion { get; set; }
    public Guid? PipelineRunId { get; set; }
    public DateTime ScoredAtUtc { get; set; }
    public string FeatureValues { get; set; } = "{}";
    public InvestigationStatus InvestigationStatus { get; set; } = InvestigationStatus.New;
    public long? AcknowledgedByUserId { get; set; }
    public AppUser? AcknowledgedBy { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
    public string? InvestigationNotes { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    // ---- added 2026-10-27 (Phase 5) -------------------------------------------------------------
    public AnomalySource Source { get; set; } = AnomalySource.IsolationForest;
    /// <summary>
    /// Stable identity of the logical event (e.g. ThresholdRule:{aircraft}:{latest reading}); unique when set, so a
    /// rerun over the same evidence can never create a second anomaly. Null for ML rows (deduplicated by their own
    /// (telemetry_record_id, model_version_id) key).
    /// </summary>
    public string? DedupKey { get; set; }
    /// <summary>Triggering rule codes (comma-separated health_threshold codes) for rule-driven anomalies.</summary>
    public string? RuleCodes { get; set; }
    /// <summary>Immutable evidence captured at detection (readings, limits, window, model version and score status).</summary>
    public string? Evidence { get; set; }
    /// <summary>Last time the same unresolved incident was observed again (continuing episode, no new ticket).</summary>
    public DateTime? LastObservedAtUtc { get; set; }
    /// <summary>
    /// The work order this anomaly is handled under (added 2026-10-28). Forecast anomalies projected for the same UTC
    /// day share one daily work order; that order's <c>AnomalyId</c> is its first anomaly.
    /// </summary>
    public Guid? WorkOrderId { get; set; }
}

/// <summary>
/// One stored sensor forecast: future hourly engine temperature / oil pressure / vibration for one aircraft and horizon,
/// written by the ML ForecastSensors job with the Active SensorForecast model. Points (jsonb array) are strictly after
/// <see cref="DataAsOfUtc"/> (the aircraft's last recorded reading); a metric the model is not validated for is null.
/// </summary>
public class SensorForecast : ILifecycleEntity
{
    public LifecycleStatus StatusId { get; set; } = LifecycleStatus.Active;
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AircraftId { get; set; }
    public Aircraft? Aircraft { get; set; }
    public Guid ModelVersionId { get; set; }
    public ModelVersion? ModelVersion { get; set; }
    public SensorForecastHorizon Horizon { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
    public DateTime DataAsOfUtc { get; set; }
    public Guid? PipelineRunId { get; set; }
    public string Points { get; set; } = "[]";
}

public class AircraftRiskPrediction : ILifecycleEntity
{
    public LifecycleStatus StatusId { get; set; } = LifecycleStatus.Active;
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AircraftId { get; set; }
    public Aircraft? Aircraft { get; set; }
    public string? ComponentId { get; set; }
    public DateTime PredictionTimestampUtc { get; set; }
    public int HorizonDays { get; set; }
    public string RiskCategory { get; set; } = string.Empty;
    public PredictionBasis Basis { get; set; }
    public PredictionState PredictionState { get; set; }

    /// <summary>Latest telemetry time the features were computed from (scoring idempotency key).</summary>
    public DateTime FeatureCutoffUtc { get; set; }
    public double? Probability { get; set; }
    public bool ProbabilityValidated { get; set; }
    public string FeatureSnapshot { get; set; } = "{}";
    public Guid? ModelVersionId { get; set; }
    public ModelVersion? ModelVersion { get; set; }
    public Guid? PipelineRunId { get; set; }
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.PendingReview;
    public long? ReviewedByUserId { get; set; }
    public AppUser? ReviewedBy { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
    public string? ReviewNotes { get; set; }
    public MaintenanceOutcome ActualOutcome { get; set; } = MaintenanceOutcome.Unknown;
    public DateTime? OutcomeRecordedAtUtc { get; set; }
    public Guid? OutcomeWorkOrderId { get; set; }
}

public class PipelineRun : ILifecycleEntity
{
    public LifecycleStatus StatusId { get; set; } = LifecycleStatus.Active;
    public Guid Id { get; set; } = Guid.NewGuid();
    public PipelineJobType JobType { get; set; }
    public PipelineRunStatus Status { get; set; } = PipelineRunStatus.Queued;
    public PipelineTrigger Trigger { get; set; } = PipelineTrigger.Api;
    public long? RequestedByUserId { get; set; }
    public AppUser? RequestedBy { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string Parameters { get; set; } = "{}";
    public string? ResultSummary { get; set; }
    public string? ErrorMessage { get; set; }
    public Guid? ModelVersionId { get; set; }
    public int? RecordsProcessed { get; set; }
    public string? WorkerHost { get; set; }
}
