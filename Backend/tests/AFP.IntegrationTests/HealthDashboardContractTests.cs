using System.Net.Http.Json;
using System.Text.Json;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Domain.Rules;
using AFP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AFP.IntegrationTests;

/// <summary>
/// Phase 7 contract additions behind the redesigned Health Dashboard (all additive): the measured limits the chart
/// draws, per-aircraft breach codes, the anomaly <c>source</c> filter, the work-order <c>anomalyId</c> filter, and the
/// rule that creating or assigning a work order does not put an aircraft under maintenance.
/// </summary>
public class HealthDashboardContractTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string EtlReason = "Auto-registered by the telemetry ETL; complete model/manufacturer and release via the application.";

    private async Task<Aircraft> UnreleasedAircraftAsync(string registration, string fleet)
    {
        var aircraft = await factory.CreateAircraftAsync(registration, fleet);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Aircraft.SingleAsync(a => a.Id == aircraft.Id);
        row.OperationalStatus = OperationalStatus.UnderMaintenance;
        row.StatusReason = EtlReason;
        await db.SaveChangesAsync();
        return row;
    }

    private async Task<Guid> WorkOrderAsync(Guid aircraftId, string number, WorkOrderStatus status, long? anomalyId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var creator = await factory.CreateUserAsync(UserRole.FlightDispatcher);
        var wo = new MaintenanceWorkOrder
        {
            WorkOrderNumber = number, AircraftId = aircraftId, AnomalyId = anomalyId, Priority = WorkOrderPriority.High,
            Title = "t", Description = "d", Status = status, CreatedByUserId = creator.Id,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        db.WorkOrders.Add(wo);
        await db.SaveChangesAsync();
        return wo.Id;
    }

    private async Task SetStatusAsync(Guid workOrderId, WorkOrderStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.WorkOrders.SingleAsync(w => w.Id == workOrderId)).Status = status;
        await db.SaveChangesAsync();
    }

    private async Task<string> DerivedStateAsync(HttpClient client, string fleet, Guid aircraftId)
    {
        var dash = await client.GetFromJsonAsync<JsonElement>($"/api/v1/dashboards/dispatcher?fleetGroup={Uri.EscapeDataString(fleet)}");
        return dash.GetProperty("readiness").EnumerateArray()
            .Single(r => r.GetProperty("aircraftId").GetGuid() == aircraftId).GetProperty("derivedState").GetString()!;
    }

    [Fact]
    public async Task Creating_or_assigning_a_work_order_does_not_start_maintenance_but_starting_work_does()
    {
        const string fleet = "Fleet Maint P7";
        var aircraft = await UnreleasedAircraftAsync("P7-M1", fleet);
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        Assert.Equal("AwaitingRelease", await DerivedStateAsync(client, fleet, aircraft.Id));

        var wo = await WorkOrderAsync(aircraft.Id, "WO-P7-M1", WorkOrderStatus.Open);
        Assert.Equal("AwaitingRelease", await DerivedStateAsync(client, fleet, aircraft.Id));      // NEW: due, not started
        await SetStatusAsync(wo, WorkOrderStatus.Assigned);
        Assert.Equal("AwaitingRelease", await DerivedStateAsync(client, fleet, aircraft.Id));      // ASSIGNED: not started
        await SetStatusAsync(wo, WorkOrderStatus.InInspection);
        Assert.Equal("UnderMaintenance", await DerivedStateAsync(client, fleet, aircraft.Id));     // IN_PROGRESS
        await SetStatusAsync(wo, WorkOrderStatus.Resolved);
        Assert.Equal("UnderMaintenance", await DerivedStateAsync(client, fleet, aircraft.Id));     // REPORTED: not accepted yet
        await SetStatusAsync(wo, WorkOrderStatus.Closed);
        Assert.Equal("AwaitingRelease", await DerivedStateAsync(client, fleet, aircraft.Id));      // COMPLETED

        // Presentation only: the stored status is untouched throughout.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Aircraft.AsNoTracking().SingleAsync(a => a.Id == aircraft.Id);
        Assert.Equal(OperationalStatus.UnderMaintenance, stored.OperationalStatus);
        Assert.Equal(EtlReason, stored.StatusReason);
    }

    [Fact]
    public async Task The_dashboard_carries_the_seeded_measured_limits_and_each_aircrafts_breach_codes()
    {
        var aircraft = await factory.CreateAircraftAsync("P7-T1", "Fleet Limits P7");
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        var dash = await client.GetFromJsonAsync<JsonElement>("/api/v1/dashboards/dispatcher?fleetGroup=Fleet%20Limits%20P7");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var expected = (await db.HealthThresholds.AsNoTracking().ToListAsync())
            .Where(t => t.ThresholdValue.HasValue
                        && t.Measurement is not (FleetHealthRules.MeasurementNone or FleetHealthRules.MeasurementFailureRiskScore))
            .OrderBy(t => t.DisplayOrder).ToList();
        var actual = dash.GetProperty("thresholds").EnumerateArray().ToList();
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Select(t => t.Code), actual.Select(t => t.GetProperty("code").GetString()));
        foreach (var (t, a) in expected.Zip(actual))
        {
            Assert.Equal(t.ThresholdValue!.Value, a.GetProperty("value").GetDouble());
            Assert.Equal(t.Comparison, a.GetProperty("comparison").GetString());
            Assert.Equal(t.Measurement, a.GetProperty("measurement").GetString());
            Assert.Equal(t.IsValidated, a.GetProperty("isValidated").GetBoolean());
        }
        // No telemetry for this aircraft: nothing breached, and the list is present (empty), never absent.
        var row = dash.GetProperty("aircraftRisk").EnumerateArray().Single(r => r.GetProperty("aircraftId").GetGuid() == aircraft.Id);
        Assert.Equal(0, row.GetProperty("breachCodes").GetArrayLength());
    }

    [Fact]
    public async Task Anomalies_filter_by_source_and_work_orders_by_their_anomaly()
    {
        var aircraft = await factory.CreateAircraftAsync("P7-A1", "Fleet Links P7");
        long ruleId, mlId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var model = new ModelVersion
            {
                ModelName = "iso", Version = Guid.NewGuid().ToString("N")[..10], ModelType = ModelType.AnomalyDetection,
                Algorithm = "IsolationForest", Status = ModelStatus.Trained, ArtifactPath = "x", ArtifactSha256 = new string('a', 64),
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.ModelVersions.Add(model);
            var now = DateTime.UtcNow;
            var rule = new AircraftAnomalyResult
            {
                AircraftId = aircraft.Id, ObservedAtUtc = now, ScoredAtUtc = now, UpdatedAtUtc = now, IsAnomaly = true,
                Severity = AnomalySeverity.High, Source = AnomalySource.ThresholdRule, DedupKey = $"ThresholdRule:{aircraft.Id}:{now:O}",
            };
            var ml = new AircraftAnomalyResult
            {
                AircraftId = aircraft.Id, ObservedAtUtc = now, ScoredAtUtc = now, UpdatedAtUtc = now, IsAnomaly = true, AnomalyScore = 0.7,
                Severity = AnomalySeverity.High, Source = AnomalySource.IsolationForest, ModelVersion = model,
            };
            db.AnomalyResults.AddRange(rule, ml);
            await db.SaveChangesAsync();
            (ruleId, mlId) = (rule.Id, ml.Id);
        }
        var linked = await WorkOrderAsync(aircraft.Id, "WO-P7-A1", WorkOrderStatus.Open, ruleId);
        await WorkOrderAsync(aircraft.Id, "WO-P7-A2", WorkOrderStatus.Open);
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);

        var rules = await client.GetFromJsonAsync<JsonElement>($"/api/v1/anomalies?aircraftId={aircraft.Id}&source=ThresholdRule");
        Assert.Equal([ruleId], rules.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("id").GetInt64()));
        var all = await client.GetFromJsonAsync<JsonElement>($"/api/v1/anomalies?aircraftId={aircraft.Id}");
        Assert.Equal(new[] { ruleId, mlId }.Order(), all.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("id").GetInt64()).Order());

        var orders = await client.GetFromJsonAsync<JsonElement>($"/api/v1/work-orders?anomalyId={ruleId}");
        Assert.Equal([linked], orders.GetProperty("items").EnumerateArray().Select(w => w.GetProperty("id").GetGuid()));
        var none = await client.GetFromJsonAsync<JsonElement>($"/api/v1/work-orders?anomalyId={mlId}");
        Assert.Equal(0, none.GetProperty("items").GetArrayLength());
    }
}
