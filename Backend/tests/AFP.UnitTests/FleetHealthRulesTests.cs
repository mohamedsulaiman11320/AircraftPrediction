using AFP.Domain.Entities;
using AFP.Domain.Rules;

namespace AFP.UnitTests;

/// <summary>
/// The engine-health business rules read every number and every piece of wording from the seeded
/// health_threshold reference data, so these tests build that data explicitly rather than relying on
/// constants in the production code.
/// </summary>
public class FleetHealthRulesTests
{
    private const string Source = "The Hub BRD v0.4 (21 October 2026) section 8";

    private static HealthThreshold Threshold(
        string code, string rule, string measurement, string comparison, double? value,
        string? unit = null, string? tier = null, string? inspection = null, bool validated = false, int order = 1) =>
        new()
        {
            Code = code, RuleReference = rule, Measurement = measurement, Comparison = comparison,
            ThresholdValue = value, Unit = unit, Tier = tier, RecommendedInspection = inspection,
            SourceReference = Source, IsValidated = validated, Description = code, DisplayOrder = order,
        };

    private static List<HealthThreshold> Recorded() =>
    [
        Threshold("OilPressureLowPsi", "BR-01", FleetHealthRules.MeasurementOilPressure,
            FleetHealthRules.ComparisonLessThan, 35, "psi", inspection: "Inspect the oil pressure regulator.", order: 1),
        Threshold("EngineTemperatureHighC", "BR-01", FleetHealthRules.MeasurementEngineTemperature,
            FleetHealthRules.ComparisonGreaterThan, 700, "C", inspection: "Immediate thermal shroud and turbine inspection.", order: 2),
        Threshold(FleetHealthRules.HighRiskWindowHoursCode, "BR-01", FleetHealthRules.MeasurementNone, "None", 24, "hours", order: 3),
        Threshold(FleetHealthRules.RoutineCheckCode, "BR-04", FleetHealthRules.MeasurementNone, "None", null,
            inspection: "Routine operational check.", order: 4),
        Threshold("RiskTierCritical", "BR-03", FleetHealthRules.MeasurementFailureRiskScore,
            FleetHealthRules.ComparisonGreaterThan, 0.70, "score", tier: "Critical", order: 5),
        Threshold("RiskTierMedium", "BR-03", FleetHealthRules.MeasurementFailureRiskScore,
            FleetHealthRules.ComparisonGreaterThan, 0.30, "score", tier: "Medium", order: 6),
        Threshold("RiskTierLow", "BR-03", FleetHealthRules.MeasurementFailureRiskScore, "None", null, "score", tier: "Low", order: 7),
    ];

    private static AircraftHealthSnapshot Snapshot(double? minPsi, double? maxTempC, int readings = 6) =>
        new(Guid.NewGuid(), DateTime.UtcNow, maxTempC, minPsi, 1.2, minPsi, maxTempC, DateTime.UtcNow.AddHours(-24), readings);

    [Fact]
    public void Window_comes_from_the_reference_data() =>
        Assert.Equal(TimeSpan.FromHours(24), FleetHealthRules.Window(Recorded()));

    [Fact]
    public void Window_is_null_when_the_reference_data_is_missing() =>
        Assert.Null(FleetHealthRules.Window([]));

    [Fact]
    public void Within_limits_is_not_high_risk_and_recommends_the_routine_check()
    {
        var result = FleetHealthRules.Evaluate(Snapshot(minPsi: 48, maxTempC: 620), Recorded());

        Assert.True(result.IsAssessable);
        Assert.False(result.IsHighRisk);
        Assert.Empty(result.Breaches);
        Assert.Equal("Routine operational check.", result.RecommendedInspection);
    }

    [Fact]
    public void Each_breach_carries_a_plain_limit_label_and_never_a_rule_code()
    {
        var result = FleetHealthRules.Evaluate(Snapshot(minPsi: 30, maxTempC: 760), Recorded());
        Assert.Equal(["Oil < 35 PSI", "Temp > 700 °C"], result.Breaches.Select(b => b.Limit).Order());
        Assert.All(result.Breaches, b => Assert.DoesNotContain("BR-", b.Limit));
    }

    [Theory]
    [InlineData(34.9, 620, "OilPressureLowPsi", "Inspect the oil pressure regulator.")]
    [InlineData(48, 700.1, "EngineTemperatureHighC", "Immediate thermal shroud and turbine inspection.")]
    public void Either_breach_alone_makes_the_aircraft_high_risk(double psi, double tempC, string code, string inspection)
    {
        var result = FleetHealthRules.Evaluate(Snapshot(psi, tempC), Recorded());

        Assert.True(result.IsHighRisk);
        Assert.Equal(code, Assert.Single(result.Breaches).Code);
        Assert.Equal("BR-01", result.Breaches[0].RuleReference);
        Assert.Equal(inspection, result.RecommendedInspection);
    }

    [Theory]
    [InlineData(35, 700)]
    public void A_reading_exactly_on_the_threshold_is_within_limits(double psi, double tempC) =>
        Assert.False(FleetHealthRules.Evaluate(Snapshot(psi, tempC), Recorded()).IsHighRisk);

    [Fact]
    public void Both_breaches_are_reported_together()
    {
        var result = FleetHealthRules.Evaluate(Snapshot(minPsi: 6.2, maxTempC: 980), Recorded());

        Assert.True(result.IsHighRisk);
        Assert.Equal(2, result.Breaches.Count);
    }

    [Fact]
    public void No_reading_in_the_window_is_not_assessable_and_never_reads_as_healthy()
    {
        var result = FleetHealthRules.Evaluate(Snapshot(null, null, readings: 0), Recorded());

        Assert.False(result.IsAssessable);
        Assert.False(result.IsHighRisk);
        Assert.Empty(result.Breaches);
        Assert.Null(result.RecommendedInspection);
    }

    [Fact]
    public void Thresholds_are_reported_as_unvalidated_until_every_rule_is_approved()
    {
        Assert.False(FleetHealthRules.Evaluate(Snapshot(48, 620), Recorded()).ThresholdsValidated);

        var approved = Recorded();
        foreach (var t in approved) t.IsValidated = true;
        Assert.True(FleetHealthRules.Evaluate(Snapshot(48, 620), approved).ThresholdsValidated);
    }

    [Theory]
    [InlineData(0.71, "Critical")]
    [InlineData(0.70, "Medium")]
    [InlineData(0.31, "Medium")]
    [InlineData(0.30, "Low")]
    [InlineData(0.0, "Low")]
    public void Risk_tiers_follow_the_recorded_boundaries(double score, string expected) =>
        Assert.Equal(expected, FleetHealthRules.RiskTier(score, Recorded()));

    [Fact]
    public void A_missing_score_has_no_tier_and_is_never_defaulted_to_low() =>
        Assert.Null(FleetHealthRules.RiskTier(null, Recorded()));

    [Fact]
    public void No_configured_tiers_yields_no_tier() =>
        Assert.Null(FleetHealthRules.RiskTier(0.9, []));

    [Fact]
    public void Unseeded_rules_cannot_classify_an_aircraft_as_high_risk()
    {
        var result = FleetHealthRules.Evaluate(Snapshot(1, 5000), []);

        Assert.False(result.IsHighRisk);
        Assert.Empty(result.Breaches);
        Assert.False(result.ThresholdsValidated);
    }
}

/// <summary>
/// The telemetry series feeds the Fleet Health graph. Pressure is stored in kPa but the engine-health rules
/// (and the graph) are in PSI, so the conversion must happen exactly once, at the API boundary.
/// </summary>
public class TelemetrySeriesUnitTests
{
    /// <summary>The factor specified for the graph is the inverse of the ingest factor already in use.</summary>
    [Theory]
    [InlineData(100.0)]
    [InlineData(241.3165)]
    [InlineData(0.0)]
    public void Kpa_to_psi_matches_the_specified_factor(double kpa)
    {
        var converted = AFP.Application.Telemetry.TelemetryRowValidator.KpaToPsi(kpa);

        Assert.Equal(kpa * 0.1450377377, converted!.Value, 6);
    }

    [Fact]
    public void A_missing_pressure_converts_to_null_not_zero()
    {
        // A null bucket means "no reading", and plotting it as 0 PSI would invent an observation.
        Assert.Null(AFP.Application.Telemetry.TelemetryRowValidator.KpaToPsi(null));
    }

    [Fact]
    public void Round_tripping_psi_through_kpa_is_stable()
    {
        const double psi = 35.0;
        var kpa = AFP.Application.Telemetry.TelemetryRowValidator.ToKpa(psi, "PSI")!.Value;

        Assert.Equal(psi, AFP.Application.Telemetry.TelemetryRowValidator.KpaToPsi(kpa)!.Value, 9);
    }
}
