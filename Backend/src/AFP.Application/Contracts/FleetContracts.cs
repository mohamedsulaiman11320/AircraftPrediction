using AFP.Domain.Enums;
using AFP.Domain.Rules;

namespace AFP.Application.Contracts;

public sealed record LatestRiskDto(string RiskCategory, PredictionBasis Basis, DateTime PredictionTimestampUtc, int HorizonDays);

/// <summary>One breached engine-health threshold, with the inspection wording the business rules require.</summary>
public sealed record HealthBreachDto(string Code, string RuleReference, string Description, string? RecommendedInspection,
    // Added 2026-10-28 (additive): short operational label, e.g. "Oil < 35 PSI" - what the UI shows instead of the rule reference.
    string? Limit = null);

/// <summary>
/// Threshold-based engine-health condition for one aircraft, derived from recorded telemetry and the seeded
/// engine-health business rules. This is descriptive, not a model prediction: <c>IsHighRisk</c> means a recorded
/// threshold was breached in the evaluation window, nothing more.
/// <para>
/// <c>IsAssessable</c> is false when no reading falls in the window — the aircraft is then neither high risk nor
/// confirmed healthy. <c>ThresholdsValidated</c> is false while the thresholds await stakeholder approval.
/// </para>
/// </summary>
public sealed record AircraftHealthDto(
    DateTime? LatestReadingAtUtc,
    double? EngineTemperatureC,
    double? OilPressurePsi,
    double? VibrationIndex,
    DateTime? WindowFromUtc,
    int ReadingsInWindow,
    bool IsAssessable,
    bool IsHighRisk,
    IReadOnlyList<HealthBreachDto> Breaches,
    string? RecommendedInspection,
    bool ThresholdsValidated,
    string? ThresholdSource,
    double? FailureRiskScore,
    string? FailureRiskTier,
    // Added 2026-10-27 (additive): whether the latest reading is older than the seeded TelemetryStaleAfterHours.
    // Null = unknown (no reading, or no rule) - never "fresh" by default.
    bool? IsStale = null,
    double? StaleAfterHours = null);

public record AircraftSummaryDto(
    Guid Id, string RegistrationNumber, string Model, string Manufacturer, string FleetGroup,
    OperationalStatus OperationalStatus, string? StatusReason, DateTime? LastTelemetryAtUtc, DateTime? LastInspectionAtUtc,
    int OpenAnomalyCount, int ActiveInspectionFlagCount, int OpenWorkOrderCount, LatestRiskDto? LatestRisk,
    AircraftHealthDto? Health = null,
    // Presentation state derived from OperationalStatus + the current maintenance workflow.
    // `OperationalStatus` remains the stored, authoritative value; this only separates
    // "registered but never released" from "actually being worked on". See FleetStatusRules.
    DerivedFleetState DerivedState = DerivedFleetState.UnderMaintenance,
    // Added 2026-10-28 (additive): "Critical" / "HighCritical" from the open forecast incident; null = none.
    string? ForecastCondition = null);

public sealed record DispatchAdvisoryDto(DispatchReadiness Readiness, IReadOnlyList<string> Reasons, string Disclaimer);

public sealed record AircraftDetailDto(
    Guid Id, string RegistrationNumber, string Model, string Manufacturer, string FleetGroup,
    OperationalStatus OperationalStatus, string? StatusReason, DateTime? LastTelemetryAtUtc, DateTime? LastInspectionAtUtc,
    int OpenAnomalyCount, int ActiveInspectionFlagCount, int OpenWorkOrderCount, LatestRiskDto? LatestRisk,
    string? SerialNumber, DateTime StatusChangedAtUtc, DateTime CreatedAtUtc,
    DispatchAdvisoryDto DispatchAdvisory,
    IReadOnlyList<InspectionFlagDto> ActiveInspectionFlags,
    IReadOnlyList<MaintenanceLogDto> RecentMaintenance,
    IReadOnlyList<AnomalyDto> RecentAnomalies,
    IReadOnlyList<PredictionDto> LatestPredictions,
    IReadOnlyList<WorkOrderDto> OpenWorkOrders,
    AircraftHealthDto? Health = null,
    DerivedFleetState DerivedState = DerivedFleetState.UnderMaintenance)
    : AircraftSummaryDto(Id, RegistrationNumber, Model, Manufacturer, FleetGroup, OperationalStatus, StatusReason,
        LastTelemetryAtUtc, LastInspectionAtUtc, OpenAnomalyCount, ActiveInspectionFlagCount, OpenWorkOrderCount, LatestRisk,
        Health, DerivedState);

public sealed record UpsertAircraftRequest(string? RegistrationNumber, string? Model, string? Manufacturer, string? FleetGroup, string? SerialNumber);
public sealed record ChangeAircraftStatusRequest(OperationalStatus? Status, string? Reason);

public sealed record InspectionFlagDto(
    Guid Id, Guid AircraftId, string RegistrationNumber, InspectionFlagSeverity Severity, string Reason,
    string RaisedBy, DateTime RaisedAtUtc, bool IsActive, string? ClearedBy, DateTime? ClearedAtUtc, string? ClearanceNotes,
    long? AnomalyId, Guid? PredictionId, Guid? WorkOrderId);

public sealed record RaiseInspectionFlagRequest(InspectionFlagSeverity? Severity, string? Reason, long? AnomalyId, Guid? PredictionId, Guid? WorkOrderId);
public sealed record ClearInspectionFlagRequest(string? ClearanceNotes);
