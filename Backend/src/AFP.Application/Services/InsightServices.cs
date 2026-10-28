using System.Globalization;
using System.Text;
using System.Text.Json;
using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Application.Contracts;
using AFP.Application.Mapping;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Domain.Rules;

namespace AFP.Application.Services;

public interface IDashboardService
{
    Task<DispatcherDashboardDto> GetDispatcherAsync(string? fleetGroup, CancellationToken ct);
    Task<TechnicianDashboardDto> GetTechnicianAsync(CancellationToken ct);
    Task<DataEngineerDashboardDto> GetDataEngineerAsync(CancellationToken ct);
    Task<LeadershipDashboardDto> GetLeadershipAsync(CancellationToken ct);
}

public sealed class DashboardService(
    IAircraftRepository aircraftRepo,
    IMaintenanceRepository maintenance,
    IAnomalyRepository anomalies,
    IPredictionRepository predictions,
    ITelemetryRepository telemetry,
    IPipelineRunRepository runs,
    IModelVersionRepository models,
    IFailurePredictionStatusProvider failureStatus,
    ICurrentUser currentUser,
    IClock clock,
    IFleetHealthEvaluator healthEvaluator,
    Microsoft.Extensions.Options.IOptions<PredictionOptions> predictionOptions) : IDashboardService
{
    public async Task<DispatcherDashboardDto> GetDispatcherAsync(string? fleetGroup, CancellationToken ct)
    {
        var allAircraft = await aircraftRepo.ListAllAsync(ct);
        var fleets = allAircraft.GroupBy(a => a.FleetGroup).OrderBy(g => g.Key)
            .Select(g => new FleetOptionDto(g.Key, g.Count())).ToList();
        var selected = string.IsNullOrWhiteSpace(fleetGroup) ? null : fleetGroup.Trim();
        if (selected is not null && fleets.All(f => !string.Equals(f.FleetGroup, selected, StringComparison.Ordinal)))
            throw new ValidationException("fleetGroup", $"Unknown fleet group '{selected}'.");
        var fleet = selected is null ? allAircraft : allAircraft.Where(a => a.FleetGroup == selected).ToList();
        var inFleet = fleet.Select(a => a.Id).ToHashSet();
        var counters = await aircraftRepo.GetCountersAsync(fleet.Select(a => a.Id).ToList(), ct);
        var latest = await predictions.GetLatestByAircraftAsync(ct);
        var flags = (await maintenance.ListFlagsAsync(null, activeOnly: true, ct)).Where(f => inFleet.Contains(f.AircraftId)).ToList();
        var openOrders = (await maintenance.ListOpenWorkOrdersAsync(null, ct)).Where(w => inFleet.Contains(w.AircraftId)).ToList();

        var readiness = fleet.Select(a =>
        {
            var c = AircraftCountersView.From(counters.GetValueOrDefault(a.Id));
            var risk = latest.GetValueOrDefault(a.Id)?.RiskCategory;
            var advisory = DispatchAdvisoryPolicy.Evaluate(
                a, flags.Where(f => f.AircraftId == a.Id).ToList(), openOrders.Where(w => w.AircraftId == a.Id).ToList(),
                c.OpenHighAnomalies, risk);
            return new ReadinessRowDto(a.Id, a.RegistrationNumber, a.Model, a.FleetGroup, a.OperationalStatus, advisory.Readiness,
                advisory.Reasons, risk, c.OpenAnomalies, a.LastTelemetryAtUtc,
                FleetStatusRules.Derive(a, c.WorkOrdersInProgress, c.ActiveFlags));
        }).OrderByDescending(r => r.DispatchReadiness).ThenBy(r => r.RegistrationNumber).ToList();

        var alerts = (await anomalies.ListAsync(new AnomalyFilter(null, null, null, clock.UtcNow.AddDays(-14), null), new PageRequest(1, 50), ct))
            .Items.Where(a => inFleet.Contains(a.AircraftId)).Take(10).ToList();

        // The same evaluation Fleet Health uses, so the two screens cannot disagree.
        var ids = fleet.Select(a => a.Id).ToList();
        var health = await healthEvaluator.EvaluateAsync(ids, latest, ct);
        var byStatus = await maintenance.CountWorkOrdersByStatusAsync(ids, ct);
        var openByAircraft = openOrders.GroupBy(w => w.AircraftId).ToDictionary(g => g.Key, g => g.Count());
        var limitMeasurement = (await aircraftRepo.ListHealthThresholdsAsync(ct)).ToDictionary(t => t.Code, t => t.Measurement);
        var forecastRisk = new Dictionary<Guid, (string Condition, long AnomalyId)>();
        foreach (var a in fleet)
        {
            var open = await anomalies.ListOpenBySourceAsync(a.Id, AnomalySource.ForecastModel, ct);
            if (open.Count == 0) continue;
            var measured = open.SelectMany(o => (o.RuleCodes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                .Select(c => limitMeasurement.GetValueOrDefault(c)).ToHashSet();
            forecastRisk[a.Id] = (AnomalyRules.ForecastCondition(measured), open[0].Id);   // the earliest open breach
        }
        var rows = fleet.OrderBy(a => a.RegistrationNumber).Select(a =>
        {
            var h = health.GetValueOrDefault(a.Id);
            var p = latest.GetValueOrDefault(a.Id);
            // Only an approved model's output counts; a candidate's score (development scoring) is "no model prediction".
            var isModel = p?.Basis == PredictionBasis.Model && ModelGovernance.IsApprovedForUse(p.ModelVersion?.Status);
            var status = !isModel ? "NoModelPrediction" : p!.ProbabilityValidated ? "Validated" : "NotValidated";
            return new AircraftRiskRowDto(
                a.Id, a.RegistrationNumber, a.FleetGroup, h is { IsAssessable: true } ? h.IsHighRisk : null, h?.IsStale,
                h?.LatestReadingAtUtc, h?.RecommendedInspection, h?.FailureRiskScore, h?.FailureRiskTier, status,
                isModel ? p!.RiskCategory : null, isModel ? p!.PredictionTimestampUtc : null, isModel ? p!.FeatureCutoffUtc : null,
                openByAircraft.GetValueOrDefault(a.Id),
                status == "NotValidated" ? ProvisionalScore(p!.FeatureSnapshot) : null,
                isModel ? p!.ModelVersion?.Version : null,
                h?.Breaches.Select(b => b.Code).ToList() ?? [],
                forecastRisk.TryGetValue(a.Id, out var fr) ? fr.Condition : null,
                forecastRisk.TryGetValue(a.Id, out var fr2) ? fr2.AnomalyId : null);
        }).ToList();

        var healthSummary = new FleetHealthSummaryDto(
            rows.Count, rows.Count(r => r.IsHighRisk is not null), rows.Count(r => r.IsHighRisk == true),
            rows.Count(r => r.IsHighRisk == false), rows.Count(r => r.IsHighRisk is null), rows.Count(r => r.IsStale == true),
            rows.Max(r => r.LatestReadingAtUtc), health.Values.Any() && health.Values.All(v => v.ThresholdsValidated));
        var validated = rows.Where(r => r.PredictionStatus == "Validated").ToList();
        var predictionSummary = new PredictionSummaryDto(
            predictionOptions.Value.HorizonDays, validated.Count,
            validated.Count(r => r.RiskTier == "Low"), validated.Count(r => r.RiskTier == "Medium"), validated.Count(r => r.RiskTier == "Critical"),
            rows.Count(r => r.PredictionStatus == "NotValidated"), rows.Count(r => r.PredictionStatus == "NoModelPrediction"),
            rows.Max(r => r.PredictionAtUtc));
        var workOrders = new WorkOrderStatusCountsDto(
            byStatus.GetValueOrDefault(WorkOrderStatus.Open), byStatus.GetValueOrDefault(WorkOrderStatus.Assigned),
            byStatus.GetValueOrDefault(WorkOrderStatus.InInspection), byStatus.GetValueOrDefault(WorkOrderStatus.Resolved),
            byStatus.GetValueOrDefault(WorkOrderStatus.Closed), byStatus.GetValueOrDefault(WorkOrderStatus.Cancelled));
        var lastScore = await runs.GetLatestAsync(PipelineJobType.Score, PipelineRunStatus.Succeeded, ct);

        return new DispatcherDashboardDto(
            FleetCounts(fleet), readiness, RiskCounts(fleet, latest, await predictions.ListRiskStatusesAsync(ct)), alerts.Select(a => a.ToDto()).ToList(),
            flags.Where(f => f.Severity != InspectionFlagSeverity.Advisory).Select(f => f.ToDto()).ToList(), WithoutPrediction(fleet, latest),
            selected, fleets, healthSummary, predictionSummary, workOrders, rows, lastScore?.CompletedAtUtc,
            (await aircraftRepo.ListHealthThresholdsAsync(ct))
                .Where(t => t.ThresholdValue.HasValue && t.Measurement is not (FleetHealthRules.MeasurementNone or FleetHealthRules.MeasurementFailureRiskScore))
                .OrderBy(t => t.DisplayOrder)
                .Select(t => new ThresholdLimitDto(t.Code, t.Measurement, t.Comparison, t.ThresholdValue!.Value, t.Unit, t.IsValidated, t.Description))
                .ToList());
    }

    public async Task<TechnicianDashboardDto> GetTechnicianAsync(CancellationToken ct)
    {
        // "My tickets": strictly the caller's own work orders.
        var now = clock.UtcNow;
        var me = currentUser.RequireUserId();
        var own = (await maintenance.ListWorkOrdersAsync(
            new WorkOrderFilter(null, null, null, me), new PageRequest(1, PageRequest.DefaultMaxPageSize), ct)).Items;
        var active = own.Where(w => w.Status is WorkOrderStatus.Assigned or WorkOrderStatus.InInspection)
            .OrderByDescending(w => w.Priority).ThenBy(w => w.DueAtUtc ?? DateTime.MaxValue).ToList();
        var resolved = own.Where(w => w.Status is WorkOrderStatus.Resolved or WorkOrderStatus.Closed)
            .OrderByDescending(w => w.ResolvedAtUtc ?? w.UpdatedAtUtc).Take(10).ToList();

        return new TechnicianDashboardDto(
            new TechnicianCountsDto(
                active.Count,
                active.Count(w => w.Status == WorkOrderStatus.InInspection),
                active.Count(w => w.IsOverdue(now)),
                own.Count(w => w.Status == WorkOrderStatus.Resolved)),
            active.Select(w => w.ToDto(now, currentUser)).ToList(),
            resolved.Select(w => w.ToDto(now, currentUser)).ToList());
    }

    public async Task<DataEngineerDashboardDto> GetDataEngineerAsync(CancellationToken ct)
    {
        var totals = await telemetry.GetQualityTotalsAsync(null, null, null, ct);
        var imports = await telemetry.ListImportsAsync(new PageRequest(1, 5), ct);
        var recent = await runs.ListAsync(null, null, new PageRequest(1, 10), ct);
        var audit = await runs.GetLatestAsync(PipelineJobType.DatasetAudit, null, ct);
        var scoring = await runs.GetLatestAsync(PipelineJobType.Score, null, ct);
        var allModels = await models.ListAsync(null, null, ct);

        var anomalyModel = allModels.FirstOrDefault(m => m.ModelType == ModelType.AnomalyDetection && m.Status == ModelStatus.Active)
                           ?? allModels.Where(m => m.ModelType == ModelType.AnomalyDetection).MaxBy(m => m.CreatedAtUtc);
        var features = anomalyModel is null
            ? new FeaturePreparationDto("NotPrepared", null, [])
            : new FeaturePreparationDto("Prepared", anomalyModel.CreatedAtUtc, ReadStrings(anomalyModel.FeatureNames));

        return new DataEngineerDashboardDto(
            new DataQualitySummaryDto(totals.Total, totals.Valid, totals.Suspect,
                totals.Total == 0 ? 0 : Math.Round(totals.Valid * 100.0 / totals.Total, 2)),
            imports.Items.Select(i => i.ToDto()).ToList(),
            recent.Items.Select(r => r.ToDto()).ToList(),
            audit?.ToDto(), scoring?.ToDto(), features,
            allModels.Take(10).Select(m => m.ToDto()).ToList(),
            await failureStatus.GetAsync(ct));
    }

    public async Task<LeadershipDashboardDto> GetLeadershipAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var fleet = await aircraftRepo.ListAllAsync(ct);
        var latest = await predictions.GetLatestByAircraftAsync(ct);
        var logs = await maintenance.ListLogsSinceAsync(now.AddMonths(-6), now, ct);
        var anomalyRows = await anomalies.GetSinceAsync(now.Date.AddDays(-29), ct);
        var open = await maintenance.ListOpenWorkOrdersAsync(null, ct);
        var allModels = await models.ListAsync(null, null, ct);

        var trend = Months(now, 6).Select(m => new MonthlyMaintenanceDto(
            m.ToString("yyyy-MM"),
            logs.Count(l => SameMonth(l.OccurredAtUtc, m) && l.EventType == MaintenanceEventType.Scheduled),
            logs.Count(l => SameMonth(l.OccurredAtUtc, m) && l.EventType == MaintenanceEventType.Unscheduled),
            logs.Count(l => SameMonth(l.OccurredAtUtc, m) && l.EventType == MaintenanceEventType.Inspection))).ToList();
        var unscheduled = logs.Where(l => l.EventType == MaintenanceEventType.Unscheduled).ToList();

        return new LeadershipDashboardDto(
            FleetCounts(fleet), RiskCounts(fleet, latest, await predictions.ListRiskStatusesAsync(ct)), trend,
            new UnscheduledStatsDto(
                unscheduled.Count(l => l.OccurredAtUtc >= now.AddDays(-30)),
                unscheduled.Count(l => l.OccurredAtUtc >= now.AddDays(-60) && l.OccurredAtUtc < now.AddDays(-30)),
                unscheduled.Count(l => l.OccurredAtUtc >= now.AddDays(-90))),
            AnomalyService.BuildSummary(anomalyRows, now.Date.AddDays(-29), 30).ByDay,
            open.Count,
            allModels.Where(m => m.Status is ModelStatus.Active or ModelStatus.Approved or ModelStatus.Registered).Select(m => m.ToDto()).ToList(),
            await failureStatus.GetAsync(ct), WithoutPrediction(fleet, latest));
    }

    /// <summary>The model's raw score from the stored feature snapshot (written by the scorer), when it is a number in [0, 1].</summary>
    internal static double? ProvisionalScore(string featureSnapshot)
    {
        var snapshot = Json.Parse(featureSnapshot);
        return snapshot.ValueKind == JsonValueKind.Object && snapshot.TryGetProperty("model_score", out var s)
               && s.ValueKind == JsonValueKind.Number && s.GetDouble() is var v and >= 0 and <= 1 ? v : null;
    }

    internal static FleetCountsDto FleetCounts(IReadOnlyCollection<Aircraft> fleet) =>
        new(fleet.Count,
            fleet.Count(a => a.OperationalStatus == OperationalStatus.Available),
            fleet.Count(a => a.OperationalStatus == OperationalStatus.UnderMaintenance),
            fleet.Count(a => a.OperationalStatus == OperationalStatus.Grounded));

    /// <summary>Categories come from the seeded risk_status table; aircraft with no prediction are counted separately.</summary>
    internal static IReadOnlyList<RiskCountDto> RiskCounts(
        IReadOnlyCollection<Aircraft> fleet, IReadOnlyDictionary<Guid, AircraftRiskPrediction> latest, IReadOnlyList<RiskStatus> categories) =>
        categories.Select(c => new RiskCountDto(c.Code, fleet.Count(a => latest.GetValueOrDefault(a.Id)?.RiskCategory == c.Code))).ToList();

    internal static int WithoutPrediction(IReadOnlyCollection<Aircraft> fleet, IReadOnlyDictionary<Guid, AircraftRiskPrediction> latest) =>
        fleet.Count(a => !latest.ContainsKey(a.Id));

    internal static IEnumerable<DateTime> Months(DateTime now, int count) =>
        Enumerable.Range(0, count).Select(i => new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-(count - 1 - i)));

    internal static bool SameMonth(DateTime value, DateTime month) => value.Year == month.Year && value.Month == month.Month;

    private static IReadOnlyList<string> ReadStrings(string json)
    {
        var element = Json.Parse(json, Json.Parse("[]"));
        return element.ValueKind == JsonValueKind.Array ? element.EnumerateArray().Select(e => e.ToString()).ToList() : [];
    }
}

public sealed record ReportFile(string FileName, string ContentType, byte[] Content);

public interface IReportService
{
    Task<FleetHealthReportDto> GetFleetHealthAsync(CancellationToken ct);
    Task<MaintenanceHistoryReportDto> GetMaintenanceHistoryAsync(DateTime? fromUtc, DateTime? toUtc, CancellationToken ct);
    Task<DataQualityReportDto> GetDataQualityAsync(CancellationToken ct);
    Task<ModelPerformanceReportDto> GetModelPerformanceAsync(CancellationToken ct);
    ReportFile ToCsv(string reportName, object report);
}

public sealed class ReportService(
    IAircraftRepository aircraftRepo,
    IMaintenanceRepository maintenance,
    IAnomalyRepository anomalies,
    IPredictionRepository predictions,
    IModelVersionRepository models,
    ITelemetryService telemetryService,
    IFailurePredictionStatusProvider failureStatus,
    IClock clock) : IReportService
{
    public async Task<FleetHealthReportDto> GetFleetHealthAsync(CancellationToken ct)
    {
        var fleet = await aircraftRepo.ListAllAsync(ct);
        var counters = await aircraftRepo.GetCountersAsync(fleet.Select(a => a.Id).ToList(), ct);
        var latest = await predictions.GetLatestByAircraftAsync(ct);
        var rows = fleet.Select(a =>
        {
            var c = AircraftCountersView.From(counters.GetValueOrDefault(a.Id));
            var p = latest.GetValueOrDefault(a.Id);
            return new FleetHealthRowDto(a.RegistrationNumber, a.Model, a.FleetGroup, a.OperationalStatus, p?.RiskCategory,
                p?.Basis.ToString(), c.OpenAnomalies, c.ActiveFlags, c.OpenWorkOrders, a.LastTelemetryAtUtc);
        }).ToList();
        return new FleetHealthReportDto(clock.UtcNow, DashboardService.FleetCounts(fleet),
            DashboardService.RiskCounts(fleet, latest, await predictions.ListRiskStatusesAsync(ct)),
            rows.Count(r => r.OpenAnomalies > 0), rows.Sum(r => r.ActiveInspectionFlags), rows.Sum(r => r.OpenWorkOrders), rows,
            DashboardService.WithoutPrediction(fleet, latest));
    }

    public async Task<MaintenanceHistoryReportDto> GetMaintenanceHistoryAsync(DateTime? fromUtc, DateTime? toUtc, CancellationToken ct)
    {
        var to = toUtc ?? clock.UtcNow;
        var from = fromUtc ?? to.AddMonths(-12);
        if (from > to) throw new ValidationException("fromUtc", "fromUtc must be before toUtc.");
        var logs = await maintenance.ListLogsSinceAsync(from, to, ct);
        var closed = await maintenance.ListClosedWorkOrdersAsync(from, to, ct);
        var monthCount = (to.Year - from.Year) * 12 + to.Month - from.Month + 1;
        var months = new List<MaintenanceMonthDto>();
        foreach (var m in DashboardService.Months(to, Math.Clamp(monthCount, 1, 60)))
        {
            var opened = await maintenance.CountWorkOrdersCreatedBetweenAsync(m, m.AddMonths(1), ct);
            months.Add(new MaintenanceMonthDto(m.ToString("yyyy-MM"),
                logs.Count(l => DashboardService.SameMonth(l.OccurredAtUtc, m) && l.EventType == MaintenanceEventType.Scheduled),
                logs.Count(l => DashboardService.SameMonth(l.OccurredAtUtc, m) && l.EventType == MaintenanceEventType.Unscheduled),
                logs.Count(l => DashboardService.SameMonth(l.OccurredAtUtc, m) && l.EventType == MaintenanceEventType.Inspection),
                opened, closed.Count(w => w.ClosedAtUtc is { } c && DashboardService.SameMonth(c, m))));
        }
        var resolutionHours = closed.Where(w => w.ResolvedAtUtc.HasValue).Select(w => (w.ResolvedAtUtc!.Value - w.CreatedAtUtc).TotalHours).ToList();
        return new MaintenanceHistoryReportDto(clock.UtcNow, from, to, months,
            logs.Count(l => l.EventType == MaintenanceEventType.Unscheduled),
            logs.Count(l => l.ActualFailureConfirmed == true),
            resolutionHours.Count == 0 ? null : Math.Round(resolutionHours.Average(), 1));
    }

    public Task<DataQualityReportDto> GetDataQualityAsync(CancellationToken ct) => telemetryService.GetDataQualityAsync(null, null, null, ct);

    public async Task<ModelPerformanceReportDto> GetModelPerformanceAsync(CancellationToken ct)
    {
        var all = await models.ListAsync(null, null, ct);
        var investigation = await anomalies.CountByInvestigationStatusAsync(ct);
        var outcomes = await predictions.CountByOutcomeAsync(ct);
        return new ModelPerformanceReportDto(clock.UtcNow, all.Select(m => m.ToDto()).ToList(),
            Enum.GetValues<InvestigationStatus>().ToDictionary(s => s.ToString(), s => investigation.GetValueOrDefault(s)),
            Enum.GetValues<MaintenanceOutcome>().ToDictionary(s => s.ToString(), s => outcomes.GetValueOrDefault(s)),
            await failureStatus.GetAsync(ct),
            "Metrics are reported exactly as recorded by the ML pipeline at training time. Isolation Forest metrics describe score " +
            "distributions, not accuracy; failure-prediction metrics are only present for models trained on verified labels.");
    }

    public ReportFile ToCsv(string reportName, object report)
    {
        var sb = new StringBuilder();
        switch (report)
        {
            case FleetHealthReportDto r:
                Row(sb, "registration_number", "model", "fleet_group", "operational_status", "risk_category", "risk_basis",
                    "open_anomalies", "active_inspection_flags", "open_work_orders", "last_telemetry_at_utc");
                foreach (var a in r.Aircraft)
                    Row(sb, a.RegistrationNumber, a.Model, a.FleetGroup, a.OperationalStatus, a.RiskCategory, a.RiskBasis,
                        a.OpenAnomalies, a.ActiveInspectionFlags, a.OpenWorkOrders, a.LastTelemetryAtUtc);
                break;
            case MaintenanceHistoryReportDto r:
                Row(sb, "month", "scheduled", "unscheduled", "inspections", "work_orders_opened", "work_orders_closed");
                foreach (var m in r.ByMonth) Row(sb, m.Month, m.Scheduled, m.Unscheduled, m.Inspections, m.WorkOrdersOpened, m.WorkOrdersClosed);
                break;
            case DataQualityReportDto r:
                Row(sb, "registration_number", "records", "suspect_records", "first_record_utc", "last_record_utc");
                foreach (var a in r.ByAircraft) Row(sb, a.RegistrationNumber, a.Records, a.SuspectRecords, a.FirstRecordUtc, a.LastRecordUtc);
                Row(sb, "TOTAL", r.TotalRecords, r.SuspectRecords, r.FirstRecordUtc, r.LastRecordUtc);
                break;
            case ModelPerformanceReportDto r:
                Row(sb, "model_name", "version", "model_type", "algorithm", "status", "training_rows", "metrics_json");
                foreach (var m in r.Models)
                    Row(sb, m.ModelName, m.Version, m.ModelType, m.Algorithm, m.Status, m.TrainingRowCount, m.Metrics.GetRawText());
                break;
            default:
                throw new ArgumentException($"Unsupported report type {report.GetType().Name}");
        }
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return new ReportFile($"{reportName}-{clock.UtcNow:yyyyMMdd-HHmmss}.csv", "text/csv", bytes);
    }

    private static void Row(StringBuilder sb, params object?[] values)
    {
        sb.AppendJoin(',', values.Select(Escape));
        sb.Append("\r\n");
    }

    private static string Escape(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            DateTime dt => dt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
        // Neutralise spreadsheet formula injection.
        if (text.Length > 0 && "=+-@".Contains(text[0]) && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            text = "'" + text;
        return text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
    }
}
