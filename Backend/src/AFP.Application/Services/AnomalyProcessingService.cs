using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Application.Contracts;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Domain.Rules;

namespace AFP.Application.Services;

/// <summary>Outcome of one anomaly-processing run (stored as the pipeline run's result summary).</summary>
public sealed record AnomalyProcessingResult(
    Guid PipelineRunId, string Status, int ThresholdIncidentsCreated, int ThresholdIncidentsContinuing,
    int ThresholdAlreadyProcessed, int ModelIncidentsCreated, int ModelAssessmentsSkippedProvisional,
    int WorkOrdersCreated, IReadOnlyList<string> WorkOrderNumbers, int DiagnosisFailures, IReadOnlyList<string> Notes,
    // Added 2026-10-27 (Phase 7, additive): validated scores skipped because their model is not approved for use.
    int ModelAssessmentsSkippedUnapproved = 0,
    // Added 2026-10-28 (forecast-driven, additive). The Threshold* and Model* counts above are retired and always 0.
    int ForecastIncidentsCreated = 0, int ForecastIncidentsContinuing = 0, int ForecastAlreadyProcessed = 0,
    IReadOnlyList<string>? PlacedInMaintenance = null, IReadOnlyList<string>? ReleasedToService = null);

public interface IAnomalyProcessingService
{
    /// <summary>Detects anomalies from the stored 30-day forecast, then raises their work orders. Idempotent.</summary>
    Task<AnomalyProcessingResult> RunAsync(CancellationToken ct);
}

/// <summary>
/// One run (forecast-only since 2026-10-28, user decision): (1) FORECAST incidents - the Active sensor forecaster's
/// latest stored forecast crossing an approved limit (engine temperature high, oil pressure low; vibration is not
/// judged), (2) one automatic work order per forecast anomaly (<see cref="AnomalyRules"/>), (3) maintenance status: an
/// aircraft with open forecast work is placed under maintenance, and released once all its forecast work orders are
/// COMPLETED (dispatcher-accepted). Anomalies are no longer raised from recorded telemetry or failure-model scores; the
/// model-assessment counts in the result are retired and always 0.
/// <para><b>One anomaly per breach episode start</b> (user decisions 2026-10-28): per limit, consecutive forecast hours
/// beyond it form an episode; a return within the limit ends it and the next breach is a new one. Episodes of different
/// limits that START in the same hour are one anomaly (codes and breaches merged). A newer forecast in which a breach is
/// still running extends its open anomaly (<c>LastObservedAtUtc</c>). The unique <c>DedupKey</c>
/// (<see cref="DedupKey"/>: tail, first breaching hour, forecaster) makes re-processing idempotent. Work orders group these anomalies by projected UTC day (<see cref="AnomalyWorkOrderService"/>).</para>
/// <para>Database unique indexes (dedup_key; one work order per anomaly) make concurrent runs safe: the loser's
/// batch is rolled back and reported, never duplicated.</para>
/// </summary>
public sealed class AnomalyProcessingService(
    IAircraftRepository aircraftRepo,
    IAnomalyRepository anomalies,
    IPipelineRunRepository runs,
    IAnomalyWorkOrderService workOrders,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClock clock,
    IModelVersionRepository models,
    ISensorForecastRepository forecasts,
    IMaintenanceRepository maintenance,
    IAircraftService aircraftService) : IAnomalyProcessingService
{
    public const string ThresholdKeyPrefix = "ThresholdRule";
    /// <summary>Longest horizon first: the 30-day forecast is the operational projection.</summary>
    private static readonly SensorForecastHorizon[] HorizonPreference =
        [SensorForecastHorizon.ThirtyDays, SensorForecastHorizon.SevenDays, SensorForecastHorizon.OneDay];
    private static readonly WorkOrderStatus[] ClosedOrCancelled = [WorkOrderStatus.Closed, WorkOrderStatus.Cancelled];

    public async Task<AnomalyProcessingResult> RunAsync(CancellationToken ct)
    {
        var run = new PipelineRun
        {
            JobType = PipelineJobType.AnomalyProcessing, Status = PipelineRunStatus.Running, Trigger = PipelineTrigger.Cli,
            RequestedByUserId = currentUser.UserId, RequestedAtUtc = clock.UtcNow, StartedAtUtc = clock.UtcNow,
            Parameters = "{}", WorkerHost = Environment.MachineName,
        };
        await runs.AddAsync(run, ct);
        await unitOfWork.SaveChangesAsync(ct);

        var notes = new List<string>();
        int created = 0, continuing = 0, already = 0, modelCreated = 0, provisional = 0, unapproved = 0;
        List<string> placed = [], released = [];
        AnomalyWorkOrderRunDto? generated = null;
        try
        {
            (created, continuing, already) = await DetectForecastIncidentsAsync(notes, ct);
            await unitOfWork.SaveChangesAsync(ct);
            generated = await workOrders.GenerateAsync(ct);
            if (generated.Note is { } note) notes.Add(note);
            (placed, released) = await SyncMaintenanceStatusAsync(notes, ct);
            Finish(run, PipelineRunStatus.Succeeded, null);
        }
        catch (ConflictException ex) when (ex.Code == "unique_violation")
        {
            // A concurrent run won the race for the same anomaly/ticket; this batch was rolled back, nothing duplicated.
            notes.Add("A concurrent run processed the same events; this run's batch was rolled back (no duplicate created).");
            Finish(run, PipelineRunStatus.Failed, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Finish(run, PipelineRunStatus.Failed, ex.Message);
            throw;
        }
        finally
        {
            var result = Result(run, created, continuing, already, modelCreated, provisional, generated, notes, unapproved, placed, released);
            run.ResultSummary = Json.Serialize(result);
            run.RecordsProcessed = created + modelCreated + (generated?.Created ?? 0);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
        }
        return Result(run, created, continuing, already, modelCreated, provisional, generated, notes, unapproved, placed, released);
    }

    /// <summary>Forecast incidents from the Active forecaster's latest stored forecast per aircraft (see class docs).</summary>
    private async Task<(int Created, int Continuing, int Already)> DetectForecastIncidentsAsync(List<string> notes, CancellationToken ct)
    {
        var thresholds = await aircraftRepo.ListHealthThresholdsAsync(ct);
        var rules = thresholds.Where(t => t.RuleReference == "BR-01" && t.ThresholdValue.HasValue
                                          && t.Measurement is FleetHealthRules.MeasurementOilPressure or FleetHealthRules.MeasurementEngineTemperature)
                              .ToList();
        if (rules.Count == 0 || !rules.All(r => r.IsValidated))
        {
            notes.Add("Forecast incidents skipped: the operating limits are not approved.");
            return (0, 0, 0);
        }
        var model = await models.GetActiveAsync(ModelType.SensorForecast, ct);
        if (model is null)
        {
            notes.Add("Forecast incidents skipped: no approved sensor forecaster is active.");
            return (0, 0, 0);
        }
        var supported = ForecastSupport(model.Metrics);
        int created = 0, continuing = 0, already = 0;
        var now = clock.UtcNow;
        foreach (var aircraft in await aircraftRepo.ListAllAsync(ct))
        {
            var open = (await anomalies.ListOpenBySourceAsync(aircraft.Id, AnomalySource.ForecastModel, ct)).ToList();
            var fresh = new List<(HealthThreshold Rule, List<ForecastPoint> Points, SensorForecast Stored, SensorForecastHorizon Horizon, string Field)>();
            foreach (var rule in rules)
            {
                var field = rule.Measurement == FleetHealthRules.MeasurementEngineTemperature ? "temperatureC" : "pressurePsi";
                var horizon = HorizonPreference.FirstOrDefault(h => supported.TryGetValue(h, out var m) && m.Contains(field));
                if (!supported.ContainsKey(horizon) || !supported[horizon].Contains(field)) continue;   // not validated
                var stored = await forecasts.GetLatestAsync(aircraft.Id, model.Id, horizon, ct);
                if (stored is null) continue;
                var future = ParsePoints(stored.Points).Where(p => p.TimestampUtc > stored.DataAsOfUtc).OrderBy(p => p.TimestampUtc).ToList();
                foreach (var episode in BreachEpisodes(future, field, rule))
                {
                    var first = episode[0].TimestampUtc;
                    var last = episode[^1].TimestampUtc;
                    // The same breach still running in a newer forecast continues its open anomaly - no new event.
                    var running = open.FirstOrDefault(a => CodesOf(a).Contains(rule.Code) && a.ObservedAtUtc <= last
                                                           && (a.LastObservedAtUtc ?? a.ObservedAtUtc) >= first.AddHours(-1));
                    if (running is not null)
                    {
                        if (last > (running.LastObservedAtUtc ?? running.ObservedAtUtc))
                        {
                            running.LastObservedAtUtc = last;
                            running.UpdatedAtUtc = now;
                        }
                        continuing++;
                        continue;
                    }
                    fresh.Add((rule, episode, stored, horizon, field));
                }
            }

            // One anomaly per aircraft and first breaching hour: limits that start breaching in the same hour are merged
            // into a single record (all their codes and breaches), keyed (tail, hour, forecaster) - user decision 2026-10-28.
            foreach (var hour in fresh.GroupBy(e => e.Points[0].TimestampUtc).OrderBy(g => g.Key))
            {
                var first = hour.Key;
                var key = DedupKey(aircraft.RegistrationNumber, first, model.Id);
                if (await anomalies.DedupKeyExistsAsync(key, ct)) { already++; continue; }
                var episodes = hour.OrderBy(e => e.Rule.Code, StringComparer.Ordinal).ToList();
                var last = episodes.Max(e => e.Points[^1].TimestampUtc);
                var evidence = Json.Serialize(new
                {
                    forecastModelVersionId = model.Id, forecastModelVersion = model.Version, horizon = episodes[0].Horizon.ToString(),
                    dataAsOfUtc = episodes.Max(e => e.Stored.DataAsOfUtc), firstBreachUtc = first, lastBreachUtc = last,
                    breaches = episodes.Select(e =>
                    {
                        var values = e.Points.Select(p => p.Value(e.Field)!.Value).ToList();
                        return new
                        {
                            e.Rule.Code, e.Rule.Measurement, e.Rule.Comparison, limit = e.Rule.ThresholdValue!.Value, e.Rule.Unit,
                            firstUtc = e.Points[0].TimestampUtc, lastUtc = e.Points[^1].TimestampUtc,
                            extreme = Math.Round(e.Rule.Comparison == FleetHealthRules.ComparisonLessThan ? values.Min() : values.Max(), 1),
                            points = e.Points.Count, e.Rule.RecommendedInspection,
                        };
                    }),
                });
                var anomaly = new AircraftAnomalyResult
                {
                    AircraftId = aircraft.Id, ObservedAtUtc = first, LastObservedAtUtc = last, ScoredAtUtc = now, UpdatedAtUtc = now,
                    IsAnomaly = true, Severity = AnomalySeverity.High, Source = AnomalySource.ForecastModel, ModelVersionId = model.Id,
                    DedupKey = key, RuleCodes = string.Join(",", episodes.Select(e => e.Rule.Code)), Evidence = evidence, FeatureValues = evidence,
                    InvestigationStatus = InvestigationStatus.New,
                };
                await anomalies.AddAsync(anomaly, ct);
                open.Add(anomaly);
                created++;
            }
        }
        return (created, continuing, already);
    }

    /// <summary>Natural key of a forecast anomaly: aircraft tail, first breaching hour and the forecaster version.</summary>
    public static string DedupKey(string registration, DateTime firstBreachUtc, Guid modelVersionId) =>
        $"{registration}_{firstBreachUtc:yyyyMMddHHmmss}_{modelVersionId}";

    private static string[] CodesOf(AircraftAnomalyResult a) => (a.RuleCodes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Consecutive forecast hours beyond the limit form one breach episode; a point back within the limit (or a missing
    /// value) ends it, and the next breach starts a new episode - and a new anomaly.
    /// </summary>
    private static IEnumerable<List<ForecastPoint>> BreachEpisodes(IEnumerable<ForecastPoint> points, string field, HealthThreshold rule)
    {
        List<ForecastPoint>? run = null;
        foreach (var p in points)
        {
            if (p.Value(field) is { } v && FleetHealthRules.IsBreached(v, rule)) (run ??= []).Add(p);
            else if (run is not null)
            {
                yield return run;
                run = null;
            }
        }
        if (run is not null) yield return run;
    }

    /// <summary>
    /// Open forecast work places an aircraft under maintenance; it is released once every forecast work order is
    /// COMPLETED (closed after dispatcher acceptance). Only system-set states are changed: an aircraft a person put
    /// under maintenance or grounded is never touched. Goes through the audited status change (policy + audit).
    /// </summary>
    private async Task<(List<string> Placed, List<string> Released)> SyncMaintenanceStatusAsync(List<string> notes, CancellationToken ct)
    {
        List<string> placed = [], released = [];
        foreach (var aircraft in await aircraftRepo.ListAllAsync(ct))
        {
            var forecastIds = (await anomalies.ListAsync(new AnomalyFilter(aircraft.Id, null, null, null, null, AnomalySource.ForecastModel),
                new PageRequest(1, PageRequest.DefaultMaxPageSize), ct)).Items.Select(a => a.Id).ToHashSet();
            if (forecastIds.Count == 0) continue;
            var orders = (await maintenance.ListWorkOrdersAsync(new WorkOrderFilter(aircraft.Id, null, null, null),
                new PageRequest(1, PageRequest.DefaultMaxPageSize), ct)).Items
                .Where(w => w.AnomalyId is { } id && forecastIds.Contains(id)).ToList();
            var open = orders.Where(w => !ClosedOrCancelled.Contains(w.Status)).ToList();
            var systemSet = aircraft.StatusReason is { } r
                            && (r.StartsWith(AnomalyRules.ForecastMaintenanceReason, StringComparison.Ordinal)
                                || FleetStatusRules.AwaitingReleaseReasons.Any(m => r.StartsWith(m, StringComparison.Ordinal)));
            try
            {
                if (open.Count > 0 && aircraft.OperationalStatus != OperationalStatus.Grounded
                    && (aircraft.OperationalStatus == OperationalStatus.Available || systemSet)
                    && aircraft.StatusReason?.StartsWith(AnomalyRules.ForecastMaintenanceReason, StringComparison.Ordinal) != true)
                {
                    await aircraftService.ChangeStatusAsync(aircraft.Id, new ChangeAircraftStatusRequest(OperationalStatus.UnderMaintenance,
                        $"{AnomalyRules.ForecastMaintenanceReason} ({string.Join(", ", open.Select(w => w.WorkOrderNumber))})."), ct);
                    placed.Add(aircraft.RegistrationNumber);
                }
                else if (open.Count == 0 && orders.Any(w => w.Status == WorkOrderStatus.Closed)
                         && aircraft.OperationalStatus == OperationalStatus.UnderMaintenance
                         && aircraft.StatusReason?.StartsWith(AnomalyRules.ForecastMaintenanceReason, StringComparison.Ordinal) == true)
                {
                    await aircraftService.ChangeStatusAsync(aircraft.Id, new ChangeAircraftStatusRequest(OperationalStatus.Available,
                        "Released: forecast maintenance work completed and accepted."), ct);
                    released.Add(aircraft.RegistrationNumber);
                }
            }
            catch (BusinessRuleException ex)
            {
                notes.Add($"{aircraft.RegistrationNumber}: status not changed - {ex.Message}");
            }
        }
        return (placed, released);
    }

    private sealed record ForecastPoint(DateTime TimestampUtc, double? TemperatureC, double? PressurePsi)
    {
        public double? Value(string field) => field == "temperatureC" ? TemperatureC : PressurePsi;
    }

    private static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static List<ForecastPoint> ParsePoints(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<List<ForecastPoint>>(json, Web) ?? [];

    private static Dictionary<SensorForecastHorizon, HashSet<string>> ForecastSupport(string metricsJson)
    {
        var result = new Dictionary<SensorForecastHorizon, HashSet<string>>();
        if (Json.Find(Json.Parse(metricsJson), "supported_horizons") is not { ValueKind: System.Text.Json.JsonValueKind.Object } map) return result;
        foreach (var entry in map.EnumerateObject())
            if (Enum.TryParse<SensorForecastHorizon>(entry.Name, out var h) && entry.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
                result[h] = entry.Value.EnumerateArray().Where(v => v.ValueKind == System.Text.Json.JsonValueKind.String).Select(v => v.GetString()!).ToHashSet();
        return result;
    }

    private void Finish(PipelineRun run, PipelineRunStatus status, string? error)
    {
        run.Status = status;
        run.CompletedAtUtc = clock.UtcNow;
        run.ErrorMessage = error is null ? null : error.Length > 4000 ? error[..4000] : error;
    }

    private static AnomalyProcessingResult Result(PipelineRun run, int created, int continuing, int already, int modelCreated,
        int provisional, AnomalyWorkOrderRunDto? generated, IReadOnlyList<string> notes, int unapproved,
        IReadOnlyList<string> placed, IReadOnlyList<string> released) =>
        new(run.Id, run.Status.ToString(), 0, 0, 0, modelCreated, provisional,
            generated?.Created ?? 0, generated?.WorkOrderNumbers ?? [], generated?.DiagnosisFailures ?? 0, notes, unapproved,
            created, continuing, already, placed, released);
}
