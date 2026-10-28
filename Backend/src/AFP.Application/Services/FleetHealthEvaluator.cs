using AFP.Application.Abstractions;
using AFP.Application.Contracts;
using AFP.Domain.Entities;
using AFP.Domain.Rules;

namespace AFP.Application.Services;

/// <summary>
/// The single place the seeded engine-health rules (BR-01 breaches, BR-03 tiers, BR-04 wording, staleness) are
/// applied to recorded telemetry. Fleet Health and the Health Dashboard both use it, so they cannot disagree.
/// </summary>
public interface IFleetHealthEvaluator
{
    /// <summary>
    /// One health block per aircraft. Empty when the reference data defines no evaluation window, so a missing or
    /// unseeded rule set surfaces as "not assessable" rather than as a clean bill of health.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, AircraftHealthDto>> EvaluateAsync(
        IReadOnlyList<Guid> aircraftIds, IReadOnlyDictionary<Guid, AircraftRiskPrediction> latestPredictions, CancellationToken ct);
}

public sealed class FleetHealthEvaluator(IAircraftRepository aircraftRepo, ITelemetryRepository telemetry, IClock clock)
    : IFleetHealthEvaluator
{
    public async Task<IReadOnlyDictionary<Guid, AircraftHealthDto>> EvaluateAsync(
        IReadOnlyList<Guid> aircraftIds, IReadOnlyDictionary<Guid, AircraftRiskPrediction> latestPredictions, CancellationToken ct)
    {
        var empty = new Dictionary<Guid, AircraftHealthDto>();
        if (aircraftIds.Count == 0) return empty;

        var thresholds = await aircraftRepo.ListHealthThresholdsAsync(ct);
        var window = FleetHealthRules.Window(thresholds);
        if (window is null) return empty;

        var now = clock.UtcNow;
        var snapshots = await telemetry.GetHealthSnapshotsAsync(aircraftIds, window.Value, ct);
        var source = thresholds.Select(t => t.SourceReference).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        var staleAfter = FleetHealthRules.StaleAfter(thresholds);

        return aircraftIds.ToDictionary(id => id, id =>
        {
            var snapshot = snapshots.GetValueOrDefault(id)
                           ?? new AircraftHealthSnapshot(id, null, null, null, null, null, null, null, 0);
            var assessment = FleetHealthRules.Evaluate(snapshot, thresholds);

            // A probability is only ever shown once the pipeline has marked it validated.
            var prediction = latestPredictions.GetValueOrDefault(id);
            var score = prediction?.ProbabilityValidated == true && ModelGovernance.IsApprovedForUse(prediction.ModelVersion?.Status)
                ? prediction.Probability : null;

            return new AircraftHealthDto(
                snapshot.LatestAtUtc, snapshot.LatestTemperatureC, snapshot.LatestPressurePsi, snapshot.LatestVibrationIndex,
                snapshot.WindowFromUtc, snapshot.ReadingsInWindow,
                assessment.IsAssessable, assessment.IsHighRisk,
                assessment.Breaches.Select(b => new HealthBreachDto(b.Code, b.RuleReference, b.Description, b.RecommendedInspection, b.Limit)).ToList(),
                assessment.RecommendedInspection, assessment.ThresholdsValidated, source,
                score, FleetHealthRules.RiskTier(score, thresholds),
                FleetHealthRules.IsStale(snapshot.LatestAtUtc, now, thresholds), staleAfter?.TotalHours);
        });
    }
}
