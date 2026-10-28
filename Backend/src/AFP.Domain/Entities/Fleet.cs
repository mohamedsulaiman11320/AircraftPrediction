using AFP.Domain.Enums;

namespace AFP.Domain.Entities;

public class Aircraft : ILifecycleEntity
{
    public LifecycleStatus StatusId { get; set; } = LifecycleStatus.Active;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RegistrationNumber { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string FleetGroup { get; set; } = string.Empty;
    public string? SerialNumber { get; set; }
    public OperationalStatus OperationalStatus { get; set; } = OperationalStatus.Available;
    public string? StatusReason { get; set; }
    public DateTime StatusChangedAtUtc { get; set; }
    public DateTime? LastTelemetryAtUtc { get; set; }
    public DateTime? LastInspectionAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// Seeded reference data for the engine-health business rules recorded in the BRD (BR-01 breach thresholds,
/// BR-03 risk tiers, BR-04 recommended inspections). Rows are maintained in
/// tools/db/seed/csv/health_threshold.csv — never embed these values in code, SQL or UI.
/// <para>
/// These are client-defined operational thresholds, not manufacturer limits or regulatory guidance:
/// <see cref="IsValidated"/> records whether an accountable stakeholder has approved the value for operational use.
/// Distinct from <see cref="EngineeringThreshold"/>, which carries an approved specification reference and approver.
/// </para>
/// </summary>
public class HealthThreshold : ILifecycleEntity
{
    public LifecycleStatus StatusId { get; set; } = LifecycleStatus.Active;
    /// <summary>Stable business key, e.g. <c>OilPressureLowPsi</c>.</summary>
    public string Code { get; set; } = string.Empty;
    /// <summary>Identifier of the business rule in the source BRD, e.g. <c>BR-01</c>.</summary>
    public string RuleReference { get; set; } = string.Empty;
    /// <summary>What is measured: <c>OilPressure</c>, <c>EngineTemperature</c>, <c>FailureRiskScore</c> or <c>None</c>.</summary>
    public string Measurement { get; set; } = string.Empty;
    /// <summary>How the value is compared: <c>LessThan</c>, <c>GreaterThan</c> or <c>None</c>.</summary>
    public string Comparison { get; set; } = string.Empty;
    /// <summary>The threshold itself. Null for rows that only carry text (such as the BR-04 default).</summary>
    public double? ThresholdValue { get; set; }
    /// <summary>Unit the threshold is expressed in: <c>psi</c>, <c>C</c>, <c>score</c>, <c>hours</c>.</summary>
    public string? Unit { get; set; }
    /// <summary>Risk tier this boundary opens, for BR-03 rows (<c>Critical</c>, <c>Medium</c>, <c>Low</c>).</summary>
    public string? Tier { get; set; }
    /// <summary>Inspection wording required by BR-04 when this threshold is breached.</summary>
    public string? RecommendedInspection { get; set; }
    /// <summary>Where the value comes from, e.g. the BRD title and section.</summary>
    public string SourceReference { get; set; } = string.Empty;
    /// <summary>False until accountable business and maintenance stakeholders approve the value (BRD §8.1).</summary>
    public bool IsValidated { get; set; }
    public string Description { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
}

public class InspectionFlag : ILifecycleEntity
{
    public LifecycleStatus StatusId { get; set; } = LifecycleStatus.Active;
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AircraftId { get; set; }
    public Aircraft? Aircraft { get; set; }
    public long? AnomalyId { get; set; }
    public Guid? PredictionId { get; set; }
    public Guid? WorkOrderId { get; set; }
    public InspectionFlagSeverity Severity { get; set; }
    public string Reason { get; set; } = string.Empty;
    public long RaisedByUserId { get; set; }
    public AppUser? RaisedBy { get; set; }
    public DateTime RaisedAtUtc { get; set; }
    public bool IsActive { get; set; } = true;
    public long? ClearedByUserId { get; set; }
    public AppUser? ClearedBy { get; set; }
    public DateTime? ClearedAtUtc { get; set; }
    public string? ClearanceNotes { get; set; }
}

public class MaintenanceLog : ILifecycleEntity
{
    public LifecycleStatus StatusId { get; set; } = LifecycleStatus.Active;
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AircraftId { get; set; }
    public Aircraft? Aircraft { get; set; }
    public string? ComponentId { get; set; }
    public MaintenanceEventType EventType { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool? ActualFailureConfirmed { get; set; }
    public bool IsVerified { get; set; }
    public MaintenanceLogSource Source { get; set; }
    public Guid? WorkOrderId { get; set; }
    public long? RecordedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class MaintenanceWorkOrder : ILifecycleEntity
{
    public LifecycleStatus StatusId { get; set; } = LifecycleStatus.Active;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string WorkOrderNumber { get; set; } = string.Empty;
    public Guid AircraftId { get; set; }
    public Aircraft? Aircraft { get; set; }
    public string? ComponentId { get; set; }
    public long? AnomalyId { get; set; }
    public AircraftAnomalyResult? Anomaly { get; set; }
    public Guid? PredictionId { get; set; }
    public AircraftRiskPrediction? Prediction { get; set; }
    public WorkOrderPriority Priority { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public WorkOrderStatus Status { get; set; } = WorkOrderStatus.Open;
    public long? AssignedTechnicianId { get; set; }
    public AppUser? AssignedTechnician { get; set; }
    public long CreatedByUserId { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? DueAtUtc { get; set; }
    public string? InspectionFindings { get; set; }
    public string? CorrectiveAction { get; set; }
    public bool? ActualFailureConfirmed { get; set; }
    public string? ResolutionNotes { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    /// <summary>
    /// The dispatcher's review when completing the work. Kept apart from the technician's report fields so
    /// that completing a ticket can never overwrite what the technician submitted.
    /// </summary>
    public string? DispatcherReviewNotes { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    // ---- added 2026-10-27 (Phase 5) -------------------------------------------------------------
    public WorkOrderSource Source { get; set; } = WorkOrderSource.Manual;
    /// <summary>The diagnosis snapshot written when the ticket was created; never regenerated afterwards.</summary>
    public DiagnosisStatus DiagnosisStatus { get; set; } = DiagnosisStatus.NotGenerated;
    public string? DiagnosisText { get; set; }
    public string? DiagnosisEvidence { get; set; }
    public string? DiagnosisMethod { get; set; }
    public string? DiagnosisRuleVersion { get; set; }
    public DateTime? DiagnosisGeneratedAtUtc { get; set; }
    public string? DiagnosisError { get; set; }
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();

    /// <summary>Every technician report ever submitted for this ticket, oldest first. Append-only.</summary>
    public List<WorkOrderReport> Reports { get; set; } = [];

    public bool IsTerminal => Status is WorkOrderStatus.Closed or WorkOrderStatus.Cancelled;

    public bool IsOverdue(DateTime nowUtc) =>
        DueAtUtc.HasValue && DueAtUtc.Value < nowUtc
        && Status is not (WorkOrderStatus.Resolved or WorkOrderStatus.Closed or WorkOrderStatus.Cancelled);
}


/// <summary>
/// One technician report, immutable once written.
/// <para>
/// A ticket the dispatcher sends back for more work is reported again, so a ticket can carry several of
/// these. Each submission is a new row; nothing ever updates or deletes one. The current values on
/// <see cref="MaintenanceWorkOrder"/> (<c>CorrectiveAction</c>, <c>ResolutionNotes</c>) mirror the latest
/// report for existing screens, but the history lives here.
/// </para>
/// </summary>
public sealed class WorkOrderReport : ILifecycleEntity
{
    public LifecycleStatus StatusId { get; set; } = LifecycleStatus.Active;
    public long Id { get; set; }
    public Guid WorkOrderId { get; set; }
    public MaintenanceWorkOrder? WorkOrder { get; set; }
    /// <summary>1 for the first report, 2 after one reassignment, ...</summary>
    public int Sequence { get; set; }
    /// <summary>The authenticated technician who submitted it - never a client-supplied identity.</summary>
    public long TechnicianUserId { get; set; }
    public AppUser? Technician { get; set; }
    public string? InspectionFindings { get; set; }
    public string CorrectiveAction { get; set; } = string.Empty;
    public string ResolutionNotes { get; set; } = string.Empty;
    public bool ActualFailureConfirmed { get; set; }
    public DateTime SubmittedAtUtc { get; set; }
    /// <summary>Approved maintenance action code (maintenance_action_code); null only for reports made before it existed.</summary>
    public string? ActionCode { get; set; }
}

/// <summary>Approved maintenance action codes a technician may report (seeded reference data, e.g. ENG_INSP).</summary>
public sealed class MaintenanceActionCode : ILifecycleEntity
{
    public LifecycleStatus StatusId { get; set; } = LifecycleStatus.Active;
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
}
