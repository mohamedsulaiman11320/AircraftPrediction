using System.Text.Json;
using System.Globalization;
using System.Text;
using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Application.Contracts;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Domain.Rules;

namespace AFP.Application.Services;

/// <summary>The diagnosis written onto a work order when it is created. Immutable once stored.</summary>
public sealed record DiagnosisSnapshot(string Text, string EvidenceJson, string Method, string RuleVersion);

public interface IWorkOrderDiagnosisService
{
    /// <summary>Builds the diagnosis for a new ticket from the evidence available now.</summary>
    Task<DiagnosisSnapshot> GenerateAsync(Aircraft aircraft, AircraftAnomalyResult? anomaly, string? reportedConcern, CancellationToken ct);

    /// <summary>The diagnosis of a daily forecast work order: every breach of the day's forecast anomalies.</summary>
    DiagnosisSnapshot GenerateForecastDay(Aircraft aircraft, IReadOnlyList<AircraftAnomalyResult> anomalies);
}

/// <summary>
/// Deterministic, evidence-grounded diagnosis (no external AI service is configured in this project, so this is the
/// transparent fallback the BRD allows). It never invents readings or causes:
/// <list type="bullet">
/// <item><b>Measured facts</b> come from stored telemetry over the BR-01 window (the same evaluation Fleet Health uses).</item>
/// <item><b>Breaches and the recommended inspection</b> come from the seeded health_threshold rows (BR-01 limits,
/// BR-04 wording) — never literals in code.</item>
/// <item><b>The model assessment</b> is reported with its real status: a validated probability, a PROVISIONAL score
/// (calibration not validated — explicitly not a probability), or none.</item>
/// <item><b>The cause is never asserted.</b> The text states that the cause is not confirmed and that the inspection is
/// how it will be established.</item>
/// </list>
/// </summary>
public sealed class WorkOrderDiagnosisService(
    IAircraftRepository aircraftRepo,
    IPredictionRepository predictions,
    IMaintenanceRepository maintenance,
    IFleetHealthEvaluator healthEvaluator,
    IClock clock) : IWorkOrderDiagnosisService
{
    public const string Method = "deterministic-rules";
    public const string MethodVersion = "diagnosis-v1";
    private const int RecentMaintenanceShown = 3;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public async Task<DiagnosisSnapshot> GenerateAsync(
        Aircraft aircraft, AircraftAnomalyResult? anomaly, string? reportedConcern, CancellationToken ct)
    {
        if (anomaly?.Source == AnomalySource.ForecastModel)
            return ForecastDiagnosis(aircraft, [anomaly]);
        var thresholds = await aircraftRepo.ListHealthThresholdsAsync(ct);
        var latest = await predictions.GetLatestByAircraftAsync(ct);
        var prediction = latest.GetValueOrDefault(aircraft.Id);
        var health = (await healthEvaluator.EvaluateAsync([aircraft.Id], latest, ct)).GetValueOrDefault(aircraft.Id);
        var history = (await maintenance.ListLogsAsync(new MaintenanceLogFilter(aircraft.Id, null, null, null),
            new PageRequest(1, RecentMaintenanceShown), ct)).Items;
        // The measurable limits only - not the window-length row, which is also tagged BR-01.
        var breachRules = thresholds.Where(t => t.RuleReference == "BR-01" && t.ThresholdValue.HasValue
                                                && t.Measurement is FleetHealthRules.MeasurementOilPressure or FleetHealthRules.MeasurementEngineTemperature)
                                    .ToList();
        var routine = thresholds.FirstOrDefault(t => t.Code == FleetHealthRules.RoutineCheckCode)?.RecommendedInspection;

        var sb = new StringBuilder();
        sb.AppendLine($"DIAGNOSTIC ASSESSMENT - {aircraft.RegistrationNumber} (deterministic rules; NOT a confirmed fault)");
        sb.AppendLine();
        sb.AppendLine("Trigger");
        sb.AppendLine(anomaly is null
            ? $"- Raised manually by a dispatcher: {Clean(reportedConcern) ?? "no description given"}"
            : $"- {Describe(anomaly)}");
        sb.AppendLine();

        sb.AppendLine("Measured facts");
        if (health is null || health.LatestReadingAtUtc is null)
        {
            sb.AppendLine("- No telemetry is recorded for this aircraft; nothing can be measured.");
        }
        else
        {
            sb.AppendLine($"- Latest reading {health.LatestReadingAtUtc:yyyy-MM-dd HH:mm} UTC: engine temperature {Num(health.EngineTemperatureC, "C")}, " +
                          $"oil pressure {Num(health.OilPressurePsi, "PSI")}, vibration index {Num(health.VibrationIndex, "")}.");
            sb.AppendLine($"- Evaluation window: the {health.ReadingsInWindow} reading(s) in the 24 h ending at that reading.");
            foreach (var rule in breachRules)
            {
                var breach = health.Breaches.FirstOrDefault(b => b.Code == rule.Code);
                sb.AppendLine($"- {rule.Description} {(breach is null ? "Not breached in the window." : "BREACHED in the window.")}");
            }
            if (health.IsStale == true)
                sb.AppendLine($"- The latest reading is older than {health.StaleAfterHours} h: these facts may not reflect the current condition.");
        }
        sb.AppendLine();

        sb.AppendLine("Model assessment (30-day failure risk)");
        sb.AppendLine(DescribePrediction(prediction));
        sb.AppendLine();

        sb.AppendLine("Recommended inspection");
        var recommendations = health?.Breaches.Select(b => b.RecommendedInspection).Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct().ToList() ?? [];
        if (recommendations.Count == 0)
            sb.AppendLine($"- {routine ?? "No threshold is breached; a routine diagnostic inspection is recommended."}");
        else
            foreach (var r in recommendations) sb.AppendLine($"- {r}");
        sb.AppendLine("- The cause is not confirmed. The inspection is how it will be established; do not assume a component has failed.");
        sb.AppendLine();

        sb.AppendLine("Recent maintenance");
        if (history.Count == 0) sb.AppendLine("- None recorded.");
        foreach (var m in history)
            sb.AppendLine($"- {m.OccurredAtUtc:yyyy-MM-dd}: {m.EventType} - {Truncate(m.Description, 160)}");
        sb.AppendLine();

        sb.AppendLine("Uncertainty");
        sb.AppendLine(health?.ThresholdsValidated == true
            ? "- The operating limits are approved by the business owner."
            : "- The operating limits are NOT yet approved; treat breaches as provisional.");
        if (prediction is null || !prediction.ProbabilityValidated)
            sb.AppendLine("- No validated failure probability exists for this aircraft.");

        var validatedRules = breachRules.Count > 0 && breachRules.All(t => t.IsValidated);
        var evidence = new
        {
            generatedAtUtc = clock.UtcNow,
            aircraft = new { aircraft.Id, aircraft.RegistrationNumber, aircraft.FleetGroup },
            anomaly = anomaly is null ? null : new
            {
                anomaly.Id, source = anomaly.Source.ToString(), anomaly.Severity, anomaly.ObservedAtUtc, anomaly.RuleCodes,
                anomaly.AnomalyScore, anomaly.ModelVersionId, evidence = Json.ParseOptional(anomaly.Evidence),
            },
            reportedConcern = Clean(reportedConcern),
            health,
            prediction = prediction is null ? null : new
            {
                prediction.Id, basis = prediction.Basis.ToString(), prediction.ProbabilityValidated,
                probability = prediction.ProbabilityValidated ? prediction.Probability : null,
                provisionalScore = prediction.ProbabilityValidated ? null : DashboardService.ProvisionalScore(prediction.FeatureSnapshot),
                prediction.RiskCategory, prediction.PredictionTimestampUtc, modelVersion = prediction.ModelVersion?.Version,
            },
            recentMaintenance = history.Select(m => new { m.OccurredAtUtc, eventType = m.EventType.ToString(), m.Description }),
        };
        var ruleVersion = $"health_threshold operating limits (approved: {(validatedRules ? "yes" : "no")}); {MethodVersion}";
        return new DiagnosisSnapshot(sb.ToString().TrimEnd(), Json.Serialize(evidence), Method, ruleVersion);
    }

    public const string ForecastMethodVersion = "forecast-diagnosis-v1";

    /// <summary>
    /// Plain-English diagnosis for a forecast anomaly, built only from its stored evidence (the limits, the forecast's
    /// first breach time and extreme value, the approved recommended inspection). No rule identifiers or codes.
    /// </summary>
    public DiagnosisSnapshot GenerateForecastDay(Aircraft aircraft, IReadOnlyList<AircraftAnomalyResult> anomalies) =>
        ForecastDiagnosis(aircraft, anomalies);

    private DiagnosisSnapshot ForecastDiagnosis(Aircraft aircraft, IReadOnlyList<AircraftAnomalyResult> anomalies)
    {
        var evidences = anomalies.Select(a => Json.ParseOptional(a.Evidence)).ToList();
        var ev = evidences.FirstOrDefault(e => e is not null);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Forecast concern for {aircraft.RegistrationNumber}");
        var inspections = new List<string>();
        var allBreaches = evidences
            .SelectMany(e => e is { } x && Json.Find(x, "breaches") is { ValueKind: JsonValueKind.Array } arr ? arr.EnumerateArray() : Enumerable.Empty<JsonElement>())
            .OrderBy(b => b.GetProperty("firstUtc").GetDateTime());
        foreach (var b in allBreaches)
            {
                var measurement = b.GetProperty("measurement").GetString();
                var lessThan = b.GetProperty("comparison").GetString() == FleetHealthRules.ComparisonLessThan;
                var (name, unit) = measurement == FleetHealthRules.MeasurementEngineTemperature
                    ? ("Engine exhaust temperature", "°C") : ("Oil pressure", "PSI");
                var limit = b.GetProperty("limit").GetDouble().ToString("0.#", Inv);
                var extreme = b.GetProperty("extreme").GetDouble().ToString("0.#", Inv);
                var first = b.GetProperty("firstUtc").GetDateTime();
                sb.AppendLine(lessThan
                    ? $"- {name} is forecast to fall below {limit} {unit} from {first:yyyy-MM-dd HH:mm} UTC (lowest forecast {extreme} {unit})."
                    : $"- {name} is forecast to rise above {limit} {unit} from {first:yyyy-MM-dd HH:mm} UTC (highest forecast {extreme} {unit}).");
                if (b.TryGetProperty("recommendedInspection", out var ri) && ri.GetString() is { Length: > 0 } text) inspections.Add(text);
            }
        sb.AppendLine();
        sb.AppendLine("Recommended action");
        foreach (var i in inspections.Distinct()) sb.AppendLine($"- {i}");
        if (inspections.Count == 0) sb.AppendLine("- Inspect the affected system before the forecast date.");
        sb.AppendLine("- Plan the inspection before the forecast date. The forecast follows this aircraft's recent readings; it is not a confirmed fault.");
        sb.AppendLine();
        sb.AppendLine("Basis");
        var asOf = ev is { } e2 && Json.Find(e2, "dataAsOfUtc") is { ValueKind: JsonValueKind.String } d ? d.GetDateTime().ToString("yyyy-MM-dd HH:mm", Inv) : "unknown";
        var version = ev is { } e3 && Json.Find(e3, "forecastModelVersion") is { ValueKind: JsonValueKind.String } v ? v.GetString() : "unknown";
        sb.AppendLine($"- 30-day sensor forecast (model {version}) from readings up to {asOf} UTC.");
        var evidence = new { generatedAtUtc = clock.UtcNow, aircraft = new { aircraft.Id, aircraft.RegistrationNumber },
            anomalies = anomalies.Select((a, i) => new { a.Id, source = a.Source.ToString(), a.ObservedAtUtc, a.LastObservedAtUtc, evidence = evidences[i] }) };
        return new DiagnosisSnapshot(sb.ToString().TrimEnd(), Json.Serialize(evidence), Method, ForecastMethodVersion);
    }

    private static string Describe(AircraftAnomalyResult a) => a.Source switch
    {
        AnomalySource.ThresholdRule =>
            $"Operating limit breach in recorded telemetry, in the 24 h window ending {a.ObservedAtUtc:yyyy-MM-dd HH:mm} UTC.",
        AnomalySource.FailureModel =>
            $"Validated failure-model assessment in the Critical tier (prediction made {a.ObservedAtUtc:yyyy-MM-dd HH:mm} UTC).",
        _ => $"Isolation Forest flagged an unusual reading at {a.ObservedAtUtc:yyyy-MM-dd HH:mm} UTC (severity {a.Severity}, " +
             $"score {a.AnomalyScore?.ToString("0.####", Inv) ?? "n/a"}). The score describes how unusual the reading is; it is not a failure probability.",
    };

    private static string DescribePrediction(AircraftRiskPrediction? p)
    {
        if (p is null || p.Basis != PredictionBasis.Model)
            return "- No 30-day failure-model output for this aircraft.";
        var version = p.ModelVersion?.Version ?? "unknown version";
        if (p.ProbabilityValidated && p.Probability is { } prob)
            return $"- Validated probability {prob.ToString("P1", Inv)} of a qualifying failure within {p.HorizonDays} days (model {version}, {p.PredictionTimestampUtc:yyyy-MM-dd}).";
        var score = DashboardService.ProvisionalScore(p.FeatureSnapshot);
        return score is null
            ? $"- Model {version} produced category {p.RiskCategory}; its calibration is not validated and no score is available."
            : $"- PROVISIONAL score {score.Value.ToString("0.000", Inv)} from model {version} (flag {p.RiskCategory}). Its calibration is not " +
              "validated, so this is a ranking signal, NOT a probability of failure.";
    }

    private static string Num(double? v, string unit) =>
        v is null ? "not recorded" : $"{v.Value.ToString("0.#", Inv)}{(unit.Length == 0 ? "" : " " + unit)}";

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
