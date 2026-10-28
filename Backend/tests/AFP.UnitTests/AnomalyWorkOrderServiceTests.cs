using AFP.Application.Common;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AFP.UnitTests;

/// <summary>
/// Automatic ticket creation must be idempotent above all else: re-scoring, a retried batch or a duplicate
/// trigger must never produce a second work order for the same anomaly.
/// </summary>
public class AnomalyWorkOrderServiceTests : IDisposable
{
    private readonly TestContext _t = new();
    public void Dispose() => _t.Dispose();

    private async Task<Aircraft> AircraftAsync(string registration = "N120UA")
    {
        var aircraft = new Aircraft
        {
            RegistrationNumber = registration, Model = "A320", Manufacturer = "Airbus", FleetGroup = "G",
            StatusChangedAtUtc = TestContext.Now, CreatedAtUtc = TestContext.Now, UpdatedAtUtc = TestContext.Now,
        };
        _t.Db.Aircraft.Add(aircraft);
        await _t.Db.SaveChangesAsync();
        return aircraft;
    }

    private async Task<ModelVersion> ModelAsync()
    {
        var model = new ModelVersion
        {
            ModelName = "iso", Version = Guid.NewGuid().ToString("N")[..10], ModelType = ModelType.AnomalyDetection,
            Algorithm = "IsolationForest", Status = ModelStatus.Active, ArtifactPath = "x",
            ArtifactSha256 = new string('a', 64), CreatedAtUtc = TestContext.Now,
        };
        _t.Db.ModelVersions.Add(model);
        await _t.Db.SaveChangesAsync();
        return model;
    }

    /// <summary>Forecast anomalies by default: since 2026-10-28 only forward-looking sources raise work orders.</summary>
    private async Task<AircraftAnomalyResult> AnomalyAsync(
        Guid aircraftId, Guid modelId, AnomalySeverity severity,
        InvestigationStatus status = InvestigationStatus.New, bool isAnomaly = true,
        AnomalySource source = AnomalySource.ForecastModel, int dayOffset = 0)
    {
        var anomaly = new AircraftAnomalyResult
        {
            // dayOffset spreads anomalies over projected days: forecast work orders are one per aircraft per UTC day.
            AircraftId = aircraftId, ModelVersionId = modelId, ObservedAtUtc = TestContext.Now.AddHours(-1).AddDays(dayOffset), Source = source,
            AnomalyScore = 0.9, IsAnomaly = isAnomaly, Severity = severity, InvestigationStatus = status,
            ScoredAtUtc = TestContext.Now, UpdatedAtUtc = TestContext.Now,
        };
        _t.Db.AnomalyResults.Add(anomaly);
        await _t.Db.SaveChangesAsync();
        return anomaly;
    }

    /// <summary>2026-10-28 (user decision): anomalies found in recorded telemetry never raise a work order.</summary>
    [Theory]
    [InlineData(AnomalySource.IsolationForest)]
    [InlineData(AnomalySource.ThresholdRule)]
    public async Task Anomalies_from_recorded_telemetry_never_raise_work_orders(AnomalySource source)
    {
        var aircraft = await AircraftAsync();
        var model = await ModelAsync();
        var recorded = await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.High, source: source);
        var forecast = await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.High);

        var dry = await _t.AnomalyWorkOrderService().DryRunAsync(default);
        Assert.Contains("forecasts only", dry.Anomalies.Single(r => r.AnomalyId == recorded.Id).ExclusionReason);
        var run = await _t.AnomalyWorkOrderService().GenerateAsync(default);

        Assert.Equal(1, run.Created);
        var order = Assert.Single(await _t.Db.WorkOrders.ToListAsync());
        Assert.Equal(forecast.Id, order.AnomalyId);
    }

    [Fact]
    public async Task A_high_severity_anomaly_raises_one_open_unassigned_work_order()
    {
        var aircraft = await AircraftAsync();
        var model = await ModelAsync();
        var anomaly = await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.High);

        var run = await _t.AnomalyWorkOrderService().GenerateAsync(default);

        Assert.Equal(1, run.Created);
        var workOrder = await _t.Db.WorkOrders.SingleAsync();
        Assert.Equal(anomaly.Id, workOrder.AnomalyId);
        Assert.Equal(WorkOrderStatus.Open, workOrder.Status);
        Assert.Equal(WorkOrderPriority.High, workOrder.Priority);
        // Assignment remains a dispatcher decision.
        Assert.Null(workOrder.AssignedTechnicianId);
    }

    [Fact]
    public async Task Running_twice_does_not_create_a_second_work_order_for_the_same_anomaly()
    {
        var aircraft = await AircraftAsync();
        var model = await ModelAsync();
        await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.High);

        var first = await _t.AnomalyWorkOrderService().GenerateAsync(default);
        var second = await _t.AnomalyWorkOrderService().GenerateAsync(default);

        Assert.Equal(1, first.Created);
        Assert.Equal(0, second.Created);
        Assert.Equal(1, await _t.Db.WorkOrders.CountAsync());
    }

    [Fact]
    public async Task Anomalies_below_the_configured_severity_are_ignored()
    {
        var aircraft = await AircraftAsync();
        var model = await ModelAsync();
        await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.Low);
        await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.Medium);

        var run = await _t.AnomalyWorkOrderService().GenerateAsync(default);

        Assert.Equal(0, run.Created);
        Assert.Equal(0, await _t.Db.WorkOrders.CountAsync());
    }

    [Fact]
    public async Task Lowering_the_configured_threshold_includes_medium_anomalies()
    {
        var aircraft = await AircraftAsync();
        var model = await ModelAsync();
        await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.Medium);

        var run = await _t.AnomalyWorkOrderService(
            new AnomalyWorkOrderOptions { MinimumSeverity = AnomalySeverity.Medium }).GenerateAsync(default);

        Assert.Equal(1, run.Created);
        Assert.Equal(WorkOrderPriority.Medium, (await _t.Db.WorkOrders.SingleAsync()).Priority);
    }

    [Fact]
    public async Task A_dismissed_anomaly_never_raises_a_ticket()
    {
        var aircraft = await AircraftAsync();
        var model = await ModelAsync();
        await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.High, InvestigationStatus.Dismissed);

        var run = await _t.AnomalyWorkOrderService().GenerateAsync(default);

        Assert.Equal(0, run.Created);
    }

    [Fact]
    public async Task A_row_that_is_not_an_anomaly_is_ignored()
    {
        var aircraft = await AircraftAsync();
        var model = await ModelAsync();
        await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.High, isAnomaly: false);

        Assert.Equal(0, (await _t.AnomalyWorkOrderService().GenerateAsync(default)).Created);
    }

    [Fact]
    public async Task A_backlog_is_capped_per_run_so_the_queue_is_not_flooded()
    {
        var aircraft = await AircraftAsync();
        var model = await ModelAsync();
        for (var i = 0; i < 5; i++) await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.High, dayOffset: i);

        var run = await _t.AnomalyWorkOrderService(
            new AnomalyWorkOrderOptions { MaxPerRun = 2 }).GenerateAsync(default);

        Assert.Equal(2, run.Created);
        Assert.Equal(2, await _t.Db.WorkOrders.CountAsync());
    }

    [Fact]
    public async Task The_feature_can_be_switched_off()
    {
        var aircraft = await AircraftAsync();
        var model = await ModelAsync();
        await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.High);

        var run = await _t.AnomalyWorkOrderService(
            new AnomalyWorkOrderOptions { Enabled = false }).GenerateAsync(default);

        Assert.Equal(0, run.Created);
        Assert.Equal(0, await _t.Db.WorkOrders.CountAsync());
    }

    [Fact]
    public async Task An_existing_manual_work_order_for_the_anomaly_suppresses_generation()
    {
        var aircraft = await AircraftAsync();
        var model = await ModelAsync();
        var anomaly = await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.High);
        _t.Db.WorkOrders.Add(new MaintenanceWorkOrder
        {
            WorkOrderNumber = "WO-MANUAL-1", AircraftId = aircraft.Id, AnomalyId = anomaly.Id,
            Priority = WorkOrderPriority.Low, Title = "raised by hand", Description = "d",
            Status = WorkOrderStatus.Open, CreatedByUserId = 1,
            CreatedAtUtc = TestContext.Now, UpdatedAtUtc = TestContext.Now,
        });
        await _t.Db.SaveChangesAsync();

        var run = await _t.AnomalyWorkOrderService().GenerateAsync(default);

        Assert.Equal(0, run.Created);
        Assert.Equal(1, await _t.Db.WorkOrders.CountAsync());
    }

    [Fact]
    public async Task Generation_does_not_write_maintenance_history()
    {
        var aircraft = await AircraftAsync();
        var model = await ModelAsync();
        await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.High);

        await _t.AnomalyWorkOrderService().GenerateAsync(default);

        // A completed maintenance record may only come from the technician completing the work.
        Assert.Equal(0, await _t.Db.MaintenanceLogs.CountAsync());
    }

    /// <summary>The dry run must be provably read-only, not merely intended to be.</summary>
    [Fact]
    public async Task The_dry_run_writes_nothing()
    {
        var aircraft = await AircraftAsync();
        var model = await ModelAsync();
        await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.High);
        await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.Low);

        var before = (
            WorkOrders: await _t.Db.WorkOrders.CountAsync(),
            Anomalies: await _t.Db.AnomalyResults.CountAsync(),
            Audits: await _t.Db.ActionAudits.CountAsync(),
            Logs: await _t.Db.MaintenanceLogs.CountAsync());

        var report = await _t.AnomalyWorkOrderService().DryRunAsync(default);

        Assert.Equal(before.WorkOrders, await _t.Db.WorkOrders.CountAsync());
        Assert.Equal(before.Anomalies, await _t.Db.AnomalyResults.CountAsync());
        Assert.Equal(before.Audits, await _t.Db.ActionAudits.CountAsync());
        Assert.Equal(before.Logs, await _t.Db.MaintenanceLogs.CountAsync());
        Assert.Equal(1, report.Counts.WouldCreate);
        Assert.Equal(2, report.Counts.Total);
    }

    [Fact]
    public async Task The_dry_run_predicts_exactly_what_generation_then_creates()
    {
        var aircraft = await AircraftAsync();
        var model = await ModelAsync();
        for (var i = 0; i < 3; i++) await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.High, dayOffset: i);
        await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.Low);

        var preview = await _t.AnomalyWorkOrderService().DryRunAsync(default);
        var run = await _t.AnomalyWorkOrderService().GenerateAsync(default);

        Assert.Equal(preview.Counts.WouldCreate, run.Created);
        Assert.Equal(3, run.Created);
    }

    [Fact]
    public async Task Forecast_anomalies_of_one_projected_day_share_one_NEW_work_order_and_later_ones_join_it()
    {
        var aircraft = await AircraftAsync();
        var model = await ModelAsync();
        var a1 = await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.High);
        var a2 = await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.High);
        var nextDay = await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.High, dayOffset: 1);

        var first = await _t.AnomalyWorkOrderService().GenerateAsync(default);
        Assert.Equal(2, first.Created);                                                  // one per projected day
        var orders = await _t.Db.WorkOrders.OrderBy(w => w.WorkOrderNumber).ToListAsync();
        Assert.All(orders, w => Assert.Equal(WorkOrderStatus.Open, w.Status));
        Assert.Equal(orders[0].Id, (await _t.Db.AnomalyResults.FindAsync(a1.Id))!.WorkOrderId);
        Assert.Equal(orders[0].Id, (await _t.Db.AnomalyResults.FindAsync(a2.Id))!.WorkOrderId);
        Assert.Equal(orders[1].Id, (await _t.Db.AnomalyResults.FindAsync(nextDay.Id))!.WorkOrderId);

        var later = await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.High);    // same day, next processing run
        var second = await _t.AnomalyWorkOrderService().GenerateAsync(default);
        Assert.Equal(0, second.Created);
        Assert.Contains("linked to an existing daily work order", second.Note);
        Assert.Equal(orders[0].Id, (await _t.Db.AnomalyResults.FindAsync(later.Id))!.WorkOrderId);
        Assert.Equal(2, await _t.Db.WorkOrders.CountAsync());
    }

    [Fact]
    public async Task The_dry_run_explains_every_exclusion()
    {
        var aircraft = await AircraftAsync();
        var model = await ModelAsync();
        await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.Low);
        await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.High, InvestigationStatus.Dismissed);
        await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.High, isAnomaly: false);

        var report = await _t.AnomalyWorkOrderService().DryRunAsync(default);

        Assert.Equal(0, report.Counts.WouldCreate);
        Assert.Equal(1, report.Counts.BelowThreshold);
        Assert.Equal(1, report.Counts.Dismissed);
        Assert.Equal(1, report.Counts.NotAnomaly);
        Assert.All(report.Anomalies, r => Assert.False(string.IsNullOrWhiteSpace(r.ExclusionReason)));
    }

    [Fact]
    public async Task Generation_does_not_change_aircraft_operational_status()
    {
        var aircraft = await AircraftAsync();
        var model = await ModelAsync();
        await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.High);
        var before = aircraft.OperationalStatus;

        await _t.AnomalyWorkOrderService().GenerateAsync(default);

        Assert.Equal(before, (await _t.Db.Aircraft.SingleAsync(a => a.Id == aircraft.Id)).OperationalStatus);
    }

    // ---- Phase 5 -----------------------------------------------------------------------------------

    private sealed class FailingDiagnosis : AFP.Application.Services.IWorkOrderDiagnosisService
    {
        public Task<AFP.Application.Services.DiagnosisSnapshot> GenerateAsync(
            Aircraft aircraft, AircraftAnomalyResult? anomaly, string? reportedConcern, CancellationToken ct) =>
            throw new InvalidOperationException("diagnosis backend unavailable");

        public AFP.Application.Services.DiagnosisSnapshot GenerateForecastDay(Aircraft aircraft, IReadOnlyList<AircraftAnomalyResult> anomalies) =>
            throw new InvalidOperationException("diagnosis backend unavailable");
    }

    [Fact]
    public async Task A_diagnosis_failure_never_loses_the_ticket_and_is_recorded_on_it()
    {
        var aircraft = await AircraftAsync();
        var anomaly = await AnomalyAsync(aircraft.Id, (await ModelAsync()).Id, AnomalySeverity.High);
        var service = new AFP.Application.Services.AnomalyWorkOrderService(
            _t.Anomalies, _t.Maintenance, _t.Auditor, _t.UnitOfWork, _t.CurrentUser, _t.Clock,
            Microsoft.Extensions.Options.Options.Create(new AnomalyWorkOrderOptions()), _t.Aircraft, new FailingDiagnosis());

        var run = await service.GenerateAsync(default);

        Assert.Equal(1, run.Created);
        Assert.Equal(1, run.DiagnosisFailures);
        var wo = await _t.Db.WorkOrders.SingleAsync(w => w.AnomalyId == anomaly.Id);
        Assert.Equal(DiagnosisStatus.Failed, wo.DiagnosisStatus);
        Assert.Equal("diagnosis backend unavailable", wo.DiagnosisError);
        Assert.Equal(WorkOrderStatus.Open, wo.Status);
        Assert.Equal(WorkOrderSource.Automatic, wo.Source);
    }

    [Fact]
    public async Task Historical_backlog_older_than_the_age_window_is_not_turned_into_new_work()
    {
        var aircraft = await AircraftAsync();
        var model = await ModelAsync();
        var old = await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.High);
        old.ObservedAtUtc = TestContext.Now.AddDays(-30);
        await _t.Db.SaveChangesAsync();
        var recent = await AnomalyAsync(aircraft.Id, model.Id, AnomalySeverity.High);

        var dry = await _t.AnomalyWorkOrderService().DryRunAsync(default);
        Assert.Equal(1, dry.Counts.TooOld);
        Assert.Equal(dry.Counts.Total, dry.Counts.WouldCreate + dry.Counts.TooOld + dry.Counts.AlreadyTicketed + dry.Counts.BelowThreshold
                                       + dry.Counts.Dismissed + dry.Counts.NotAnomaly + dry.Counts.DeferredByCap + dry.Counts.Disabled);

        var run = await _t.AnomalyWorkOrderService().GenerateAsync(default);
        Assert.Equal(1, run.Created);
        Assert.False(await _t.Db.WorkOrders.AnyAsync(w => w.AnomalyId == old.Id));
        Assert.True(await _t.Db.WorkOrders.AnyAsync(w => w.AnomalyId == recent.Id));
    }

    [Fact]
    public async Task The_stored_diagnosis_names_a_provisional_score_as_not_a_probability()
    {
        var aircraft = await AircraftAsync();
        _t.Db.RiskPredictions.Add(new AircraftRiskPrediction
        {
            AircraftId = aircraft.Id, PredictionTimestampUtc = TestContext.Now, FeatureCutoffUtc = TestContext.Now.Date, HorizonDays = 30,
            RiskCategory = "High", Basis = PredictionBasis.Model, PredictionState = PredictionState.Scored,
            Probability = null, ProbabilityValidated = false, FeatureSnapshot = "{\"model_score\": 0.934}",
        });
        await _t.Db.SaveChangesAsync();
        var snapshot = await _t.Diagnosis().GenerateAsync(aircraft, null, "Crew report", default);

        Assert.Contains("PROVISIONAL score 0.934", snapshot.Text);
        Assert.Contains("NOT a probability", snapshot.Text);
        Assert.DoesNotContain("93.4%", snapshot.Text);
        Assert.Contains("No telemetry is recorded", snapshot.Text);              // missing data is stated, not invented
        Assert.Contains("cause is not confirmed", snapshot.Text);
        using var evidence = System.Text.Json.JsonDocument.Parse(snapshot.EvidenceJson);
        var prediction = evidence.RootElement.GetProperty("prediction");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, prediction.GetProperty("probability").ValueKind);
        Assert.Equal(0.934, prediction.GetProperty("provisionalScore").GetDouble(), 3);
    }
}
