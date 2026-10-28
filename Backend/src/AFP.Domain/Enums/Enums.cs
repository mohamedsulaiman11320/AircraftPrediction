namespace AFP.Domain.Enums;

/// <summary>Application roles. Values are the stable primary keys of the seeded <c>role</c> table
/// (tools/db/seed/csv/role.csv); stored as integers (FK), never renumber.</summary>
public enum UserRole { FlightDispatcher = 1, MaintenanceTechnician = 2, DataEngineer = 3 }

public enum UserStatus { Pending, Active, Disabled }

public enum OperationalStatus { Available, UnderMaintenance, Grounded }

public enum MaintenanceEventType { Scheduled, Unscheduled, Inspection }

public enum MaintenanceLogSource { Manual, WorkOrder, Import }

public enum WorkOrderPriority { Low, Medium, High, Critical }

public enum WorkOrderStatus { Open, Assigned, InInspection, Resolved, Closed, Cancelled }

public enum InspectionFlagSeverity { Advisory, Restrictive, Grounding }

public enum TelemetryImportStatus { Queued, Processing, Completed, Failed }

/// <summary>How an import entered the platform: CSV processed by the API, or raw JSON processed by the Spark ETL
/// from MinIO bronze/telemetry (uploaded through the API or placed directly in the bucket).</summary>
public enum TelemetryImportSource { Csv, RawJson }

public enum QualityStatus { Valid, Suspect }

public enum MeasurementKind { Temperature, Pressure }

public enum ModelType { AnomalyDetection, FailurePrediction, SensorForecast }

/// <summary>Horizons a future sensor forecast may be requested for (Health Dashboard filters: 1 day, 7 days, 1 month).</summary>
public enum SensorForecastHorizon { OneDay, SevenDays, ThirtyDays }

/// <summary>Whether genuine forecast points were returned. NotAvailable always comes with the reason - never with substitutes.</summary>
public enum SensorForecastStatus { Available, NotAvailable }

public enum ModelStatus { Trained, Registered, Approved, Active, Retired, Rejected }

public enum AnomalySeverity { Low, Medium, High }

/// <summary>Where an anomaly came from. IsolationForest rows are written by the ML scorer; the others by the
/// backend's anomaly processing (approved BR-01 rules / a VALIDATED failure-model assessment).</summary>
public enum AnomalySource { IsolationForest, ThresholdRule, FailureModel, ForecastModel }

/// <summary>Automatic = raised from an anomaly by the processing run; Manual = raised by a dispatcher.</summary>
public enum WorkOrderSource { Manual, Automatic }

/// <summary>State of the diagnosis snapshot stored with a work order.</summary>
public enum DiagnosisStatus { NotGenerated, Generated, Failed }

public enum InvestigationStatus { New, Acknowledged, Investigating, Dismissed, ConfirmedIssue }

public enum PredictionBasis { Model, DescriptiveIndicator }

public enum ApprovalStatus { PendingReview, Approved, Rejected }

public enum MaintenanceOutcome { Unknown, UnscheduledMaintenance, NoUnscheduledMaintenance }

/// <summary>ScoreCandidate exists only in the isolated development environment (never queued operationally).
/// SimulateTelemetry is the daily telemetry simulator (scheduled/CLI only, never queued from the API).</summary>
public enum PipelineJobType { DatasetAudit, TrainAnomaly, RegisterModel, Score, TrainFailure, TelemetryImport, ScoreCandidate, SimulateTelemetry, AnomalyProcessing, TrainSensorForecast, ForecastSensors }

public enum PipelineRunStatus { Queued, Running, Succeeded, Failed, Blocked, Cancelled }

public enum PipelineTrigger { Api, Cli }

public enum DispatchReadiness { NoKnownRestrictions, Restricted, NotAvailable }

/// <summary>
/// Record lifecycle. Values are the stable primary keys of the seeded <c>status</c> reference table
/// (tools/db/seed/csv/status.csv); they must never be renumbered. Stored as integers (FK), not strings.
/// </summary>
public enum LifecycleStatus { Inactive = 0, Active = 1, Deleted = 2 }

/// <summary>Outcome of a scoring attempt for one aircraft (distinct from the risk category itself).</summary>
public enum PredictionState { Scored, InsufficientTelemetry, MissingFeatures }

/// <summary>
/// Rule-based operational criticality from approved temperature/pressure limits.
/// Unclassified = a measurement is missing/invalid or no approved limit exists (never treated as Normal).
/// </summary>
public enum OperationalCriticality { Normal, Critical, HighCritical, Unclassified }
