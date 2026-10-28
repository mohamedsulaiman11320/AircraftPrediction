using AFP.Domain.Entities;

namespace AFP.Domain.Rules;

/// <summary>
/// Measured engine condition for one aircraft over the evaluation window, in the units the business rules are
/// written in. Pressure is PSI and temperature is °C; the caller converts from the stored canonical units.
/// </summary>
/// <param name="LatestAtUtc">Timestamp of the most recent reading, or null when the aircraft has none.</param>
/// <param name="WindowFromUtc">Start of the evaluation window the aggregates were taken over.</param>
/// <param name="ReadingsInWindow">How many readings fell inside the window.</param>
public sealed record AircraftHealthSnapshot(
    Guid AircraftId,
    DateTime? LatestAtUtc,
    double? LatestTemperatureC,
    double? LatestPressurePsi,
    double? LatestVibrationIndex,
    double? MinPressurePsiInWindow,
    double? MaxTemperatureCInWindow,
    DateTime? WindowFromUtc,
    int ReadingsInWindow);

/// <summary>Why an aircraft breached a threshold, carrying the rule and the wording BR-04 requires.</summary>
public sealed record HealthBreach(string Code, string RuleReference, string Description, string? RecommendedInspection, string? Limit = null);

/// <summary>
/// Outcome of evaluating the recorded business rules against one aircraft.
/// <paramref name="IsAssessable"/> is false when there is no reading in the window — the aircraft is then neither
/// high risk nor confirmed healthy, and callers must say so rather than implying a clean result.
/// </summary>
public sealed record FleetHealthAssessment(
    bool IsAssessable,
    bool IsHighRisk,
    IReadOnlyList<HealthBreach> Breaches,
    string? RecommendedInspection,
    bool ThresholdsValidated);

/// <summary>
/// Evaluates the engine-health business rules recorded in the BRD. Every number and every piece of wording comes
/// from the seeded <see cref="HealthThreshold"/> reference data — this class holds the comparison logic only,
/// so a threshold change is a data change, not a code change.
/// <para>
/// These rules are threshold-based and descriptive. They are NOT a model prediction, they never make an aircraft
/// dispatchable, and they are only as trustworthy as the thresholds: see <see cref="HealthThreshold.IsValidated"/>.
/// </para>
/// </summary>
public static class FleetHealthRules
{
    public const string MeasurementOilPressure = "OilPressure";
    public const string MeasurementEngineTemperature = "EngineTemperature";
    public const string MeasurementFailureRiskScore = "FailureRiskScore";
    public const string MeasurementNone = "None";

    public const string ComparisonLessThan = "LessThan";
    public const string ComparisonGreaterThan = "GreaterThan";

    /// <summary>Code of the row that carries the BR-01 evaluation window length, in hours.</summary>
    public const string HighRiskWindowHoursCode = "HighRiskWindowHours";

    /// <summary>Code of the row carrying the BR-04 wording used when nothing is breached.</summary>
    public const string RoutineCheckCode = "RoutineOperationalCheck";

    /// <summary>Code of the row saying how old the latest reading may be before it is shown as stale.</summary>
    public const string TelemetryStaleAfterHoursCode = "TelemetryStaleAfterHours";

    /// <summary>Age after which telemetry is stale. Null when the reference data does not define it.</summary>
    public static TimeSpan? StaleAfter(IReadOnlyCollection<HealthThreshold> thresholds)
    {
        var hours = thresholds.FirstOrDefault(t => t.Code == TelemetryStaleAfterHoursCode)?.ThresholdValue;
        return hours is > 0 ? TimeSpan.FromHours((double)hours.Value) : null;
    }

    /// <summary>
    /// True when the latest reading is older than the configured age. Unknown (null) when there is no reading or
    /// no rule: an unconfigured rule never makes data look fresh.
    /// </summary>
    public static bool? IsStale(DateTime? latestAtUtc, DateTime nowUtc, IReadOnlyCollection<HealthThreshold> thresholds) =>
        latestAtUtc is null || StaleAfter(thresholds) is not { } limit ? null : nowUtc - latestAtUtc.Value > limit;

    /// <summary>Length of the BR-01 evaluation window. Null when the reference data does not define one.</summary>
    public static TimeSpan? Window(IReadOnlyCollection<HealthThreshold> thresholds)
    {
        var hours = thresholds.FirstOrDefault(t => t.Code == HighRiskWindowHoursCode)?.ThresholdValue;
        return hours is > 0 ? TimeSpan.FromHours(hours.Value) : null;
    }

    /// <summary>Applies the BR-01 breach thresholds and the BR-04 wording to one aircraft's measurements.</summary>
    public static FleetHealthAssessment Evaluate(AircraftHealthSnapshot snapshot, IReadOnlyCollection<HealthThreshold> thresholds)
    {
        var breachRules = thresholds
            .Where(t => t.ThresholdValue.HasValue && t.Tier is null && t.Code != HighRiskWindowHoursCode && t.Code != TelemetryStaleAfterHoursCode)
            .Where(t => t.Measurement is MeasurementOilPressure or MeasurementEngineTemperature)
            .OrderBy(t => t.DisplayOrder)
            .ToList();

        var validated = breachRules.Count > 0 && breachRules.All(t => t.IsValidated);

        if (snapshot.ReadingsInWindow == 0)
            return new FleetHealthAssessment(false, false, [], null, validated);

        var breaches = new List<HealthBreach>();
        foreach (var rule in breachRules)
        {
            var measured = rule.Measurement switch
            {
                MeasurementOilPressure => snapshot.MinPressurePsiInWindow,
                MeasurementEngineTemperature => snapshot.MaxTemperatureCInWindow,
                _ => null,
            };
            if (measured is null || !IsBreached(measured.Value, rule)) continue;
            breaches.Add(new HealthBreach(rule.Code, rule.RuleReference, rule.Description, rule.RecommendedInspection, LimitLabel(rule)));
        }

        // BR-04: the breached rule supplies the wording; with no breach the recorded default applies.
        var recommendation = breaches.Count > 0
            ? breaches.Select(b => b.RecommendedInspection).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r))
            : thresholds.FirstOrDefault(t => t.Code == RoutineCheckCode)?.RecommendedInspection;

        return new FleetHealthAssessment(true, breaches.Count > 0, breaches, recommendation, validated);
    }

    /// <summary>
    /// Maps a failure-risk score to the BR-03 tier. Returns null when no score is supplied or no tiers are
    /// configured — callers must never substitute a default tier for a missing assessment.
    /// </summary>
    public static string? RiskTier(double? score, IReadOnlyCollection<HealthThreshold> thresholds)
    {
        if (score is null) return null;
        var tiers = thresholds
            .Where(t => t.Measurement == MeasurementFailureRiskScore && t.Tier is not null && t.ThresholdValue.HasValue)
            .OrderByDescending(t => t.ThresholdValue!.Value)
            .ToList();
        if (tiers.Count == 0) return null;

        foreach (var tier in tiers.Where(tier => IsBreached(score.Value, tier)))
            return tier.Tier;

        // Below every configured boundary: the lowest tier recorded, if the reference data names one.
        return thresholds
            .Where(t => t.Measurement == MeasurementFailureRiskScore && t.Tier is not null && !t.ThresholdValue.HasValue)
            .Select(t => t.Tier)
            .FirstOrDefault();
    }

    /// <summary>Short operational label of a measured limit, e.g. "Temp > 700 °C" or "Oil < 35 PSI" (never a rule code).</summary>
    public static string? LimitLabel(HealthThreshold rule)
    {
        if (rule.ThresholdValue is not { } value) return null;
        var name = rule.Measurement switch
        {
            MeasurementEngineTemperature => "Temp",
            MeasurementOilPressure => "Oil",
            _ => rule.Measurement,
        };
        var op = rule.Comparison == ComparisonLessThan ? "<" : rule.Comparison == ComparisonGreaterThan ? ">" : "vs";
        var unit = rule.Unit switch { "C" => "°C", "psi" => "PSI", null => "", var u => u };
        return $"{name} {op} {value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} {unit}".TrimEnd();
    }

    public static bool IsBreached(double measured, HealthThreshold rule) => rule.Comparison switch
    {
        ComparisonLessThan => measured < rule.ThresholdValue,
        ComparisonGreaterThan => measured > rule.ThresholdValue,
        _ => false,
    };
}
