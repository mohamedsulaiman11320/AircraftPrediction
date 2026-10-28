using AFP.Domain.Enums;
using AFP.Domain.Rules;

namespace AFP.Application.Contracts;

public sealed record FleetCountsDto(int Total, int Available, int UnderMaintenance, int Grounded);
public sealed record RiskCountDto(string RiskCategory, int Count);

public sealed record ReadinessRowDto(
    Guid AircraftId, string RegistrationNumber, string Model, string FleetGroup, OperationalStatus OperationalStatus,
    DispatchReadiness DispatchReadiness, IReadOnlyList<string> Restrictions, string? LatestRiskCategory, int OpenAnomalyCount,
    DateTime? LastTelemetryAtUtc,
    // Added 2026-10-27 (additive): the same presentation state Fleet Health shows (e.g. AwaitingRelease for an
    // ETL auto-registered aircraft), so the dashboard no longer shows those as "Under maintenance".
    DerivedFleetState? DerivedState = null);

public sealed record DispatcherDashboardDto(
    FleetCountsDto FleetCounts, IReadOnlyList<ReadinessRowDto> Readiness, IReadOnlyList<RiskCountDto> RiskOverview,
    IReadOnlyList<AnomalyDto> RecentAlerts, IReadOnlyList<InspectionFlagDto> ActiveRestrictions, int AircraftWithoutPrediction,
    // Added 2026-10-27 (additive). Everything below - and everything above - is for SelectedFleetGroup
    // (null = all fleets). Fleets lists the real fleet groups with their aircraft counts.
    string? SelectedFleetGroup = null,
    IReadOnlyList<FleetOptionDto>? Fleets = null,
    FleetHealthSummaryDto? Health = null,
    PredictionSummaryDto? Prediction = null,
    WorkOrderStatusCountsDto? WorkOrders = null,
    IReadOnlyList<AircraftRiskRowDto>? AircraftRisk = null,
    DateTime? LastScoringRunAtUtc = null,
    // Added 2026-10-27 (Phase 7, additive): the measured engine-health limits from the seeded health_threshold
    // table, so the dashboard draws the real limits instead of restating them.
    IReadOnlyList<ThresholdLimitDto>? Thresholds = null);

/// <summary>One measured limit (health_threshold rows with a measurement and a value). Comparison: LessThan | GreaterThan.</summary>
public sealed record ThresholdLimitDto(
    string Code, string Measurement, string Comparison, double Value, string? Unit, bool IsValidated, string Description);

public sealed record FleetOptionDto(string FleetGroup, int AircraftCount);

/// <summary>BR-01 threshold health (independent of any model). Stale = latest reading older than the seeded limit.</summary>
public sealed record FleetHealthSummaryDto(
    int Aircraft, int Assessable, int HighRisk, int WithinLimits, int NotAssessable, int Stale,
    DateTime? LatestReadingAtUtc, bool ThresholdsValidated);

/// <summary>
/// 30-day model failure risk. Only VALIDATED probabilities are bucketed into the BR-03 tiers; model output that is not
/// validated and aircraft with no model prediction are counted separately - never as Low.
/// </summary>
public sealed record PredictionSummaryDto(
    int HorizonDays, int Validated, int Low, int Medium, int Critical, int NotValidated, int NoModelPrediction,
    DateTime? LatestPredictionAtUtc);

/// <summary>Counts by stored status; UI labels NEW, ASSIGNED, IN_PROGRESS, REPORTED, COMPLETED, CANCELLED.</summary>
public sealed record WorkOrderStatusCountsDto(int Open, int Assigned, int InInspection, int Resolved, int Closed, int Cancelled);

/// <summary>
/// One aircraft's current position. PredictionStatus: "Validated" (FailureProbability and RiskTier set),
/// "NotValidated" (model output exists; only its ProvisionalScore is shown, labelled provisional) or "NoModelPrediction".
/// IsHighRisk is null when the aircraft cannot be assessed (no readings).
/// </summary>
public sealed record AircraftRiskRowDto(
    Guid AircraftId, string RegistrationNumber, string FleetGroup, bool? IsHighRisk, bool? IsStale, DateTime? LatestReadingAtUtc,
    string? RecommendedInspection, double? FailureProbability, string? RiskTier, string PredictionStatus,
    string? PredictionCategory, DateTime? PredictionAtUtc, DateTime? FeatureCutoffUtc, int OpenWorkOrders,
    // Added 2026-10-27 (additive): when PredictionStatus is "NotValidated", the model's own score (0..1) from the
    // prediction's feature snapshot - a PROVISIONAL score, never a validated probability (FailureProbability stays
    // null). ModelVersion identifies the model that produced it.
    double? ProvisionalScore = null, string? ModelVersion = null,
    // Added 2026-10-27 (Phase 7, additive): codes of the limits breached in the latest window (empty = none).
    IReadOnlyList<string>? BreachCodes = null,
    // Added 2026-10-28 (forecast-driven, additive): from the aircraft's OPEN forecast incident - "HighCritical" when the
    // forecast breaches both temperature and oil pressure, "Critical" when it breaches one; null = no open forecast risk.
    string? ForecastCondition = null, long? ForecastAnomalyId = null);

public sealed record TechnicianCountsDto(int AssignedOpen, int InInspection, int Overdue, int AwaitingClosure);

/// <summary>"My tickets": only work orders assigned to the calling technician.</summary>
public sealed record TechnicianDashboardDto(
    TechnicianCountsDto Counts, IReadOnlyList<WorkOrderDto> AssignedWorkOrders, IReadOnlyList<WorkOrderDto> RecentlyResolved);

public sealed record DataQualitySummaryDto(int TotalRecords, int ValidRecords, int SuspectRecords, double ValidPercent);
public sealed record FeaturePreparationDto(string Status, DateTime? LastPreparedAtUtc, IReadOnlyList<string> FeatureNames);
public sealed record DataEngineerDashboardDto(
    DataQualitySummaryDto DataQuality, IReadOnlyList<TelemetryImportDto> LatestImports, IReadOnlyList<PipelineRunDto> RecentRuns,
    PipelineRunDto? LatestAudit, PipelineRunDto? LatestScoring, FeaturePreparationDto FeaturePreparation,
    IReadOnlyList<ModelVersionDto> Models, FailurePredictionStatusDto FailurePredictionStatus);

public sealed record MonthlyMaintenanceDto(string Month, int Scheduled, int Unscheduled, int Inspections);
public sealed record UnscheduledStatsDto(int Last30Days, int Previous30Days, int Last90Days);
public sealed record LeadershipDashboardDto(
    FleetCountsDto FleetCounts, IReadOnlyList<RiskCountDto> RiskDistribution, IReadOnlyList<MonthlyMaintenanceDto> MaintenanceTrend,
    UnscheduledStatsDto Unscheduled, IReadOnlyList<DailyCountDto> AnomalyTrend, int OpenWorkOrders,
    IReadOnlyList<ModelVersionDto> ModelSummaries, FailurePredictionStatusDto FailurePredictionStatus, int AircraftWithoutPrediction);

// ---- Reports ---------------------------------------------------------------------------------

public sealed record FleetHealthRowDto(
    string RegistrationNumber, string Model, string FleetGroup, OperationalStatus OperationalStatus, string? RiskCategory,
    string? RiskBasis, int OpenAnomalies, int ActiveInspectionFlags, int OpenWorkOrders, DateTime? LastTelemetryAtUtc);

public sealed record FleetHealthReportDto(
    DateTime GeneratedAtUtc, FleetCountsDto Totals, IReadOnlyList<RiskCountDto> RiskDistribution, int AircraftWithOpenAnomalies,
    int ActiveInspectionFlags, int OpenWorkOrders, IReadOnlyList<FleetHealthRowDto> Aircraft, int AircraftWithoutPrediction);

public sealed record MaintenanceMonthDto(
    string Month, int Scheduled, int Unscheduled, int Inspections, int WorkOrdersOpened, int WorkOrdersClosed);

public sealed record MaintenanceHistoryReportDto(
    DateTime GeneratedAtUtc, DateTime FromUtc, DateTime ToUtc, IReadOnlyList<MaintenanceMonthDto> ByMonth,
    int UnscheduledTotal, int ConfirmedFailures, double? MeanResolutionHours);

public sealed record ModelPerformanceReportDto(
    DateTime GeneratedAtUtc, IReadOnlyList<ModelVersionDto> Models, IReadOnlyDictionary<string, int> AnomalyInvestigationOutcomes,
    IReadOnlyDictionary<string, int> PredictionOutcomes, FailurePredictionStatusDto FailurePredictionStatus, string Note);
