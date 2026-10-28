using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AFP.IntegrationTests;

/// <summary>
/// The Health Dashboard over real HTTP: fleet filtering, BR-01 health, BR-03 tiers (validated probabilities only)
/// and work-order counts that agree with the work-order list. Its own factory, so the fleet is exactly what it seeds.
/// </summary>
public class DispatcherDashboardApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>Tests share one database, so each seeds its own uniquely named fleets.</summary>
    private async Task<(Aircraft East1, Aircraft East2, Aircraft West, string East, string WestFleet)> SeedAsync(string tag)
    {
        var (eastName, westName) = ($"Fleet East {tag}", $"Fleet West {tag}");
        var east1 = await factory.CreateAircraftAsync($"DD-{tag}-E1", eastName);
        var east2 = await factory.CreateAircraftAsync($"DD-{tag}-E2", eastName);
        var west = await factory.CreateAircraftAsync($"DD-{tag}-W1", westName);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        // A validated 0.82 (Critical), a model score that is NOT validated, and no prediction for the third - all from
        // an Active (approved) model, as operational scoring only ever uses the Active model.
        var active = Model(ModelStatus.Active);
        db.ModelVersions.Add(active);
        db.RiskPredictions.Add(Prediction(east1.Id, now, 0.82, validated: true, active.Id));
        db.RiskPredictions.Add(Prediction(east2.Id, now, 0.55, validated: false, active.Id));
        var creator = await factory.CreateUserAsync(UserRole.FlightDispatcher);
        db.WorkOrders.Add(WorkOrder(east1.Id, creator.Id, $"WO-{tag}-1", WorkOrderStatus.Open));
        db.WorkOrders.Add(WorkOrder(east1.Id, creator.Id, $"WO-{tag}-2", WorkOrderStatus.Resolved));
        db.WorkOrders.Add(WorkOrder(west.Id, creator.Id, $"WO-{tag}-3", WorkOrderStatus.Closed));
        await db.SaveChangesAsync();
        return (east1, east2, west, Uri.EscapeDataString(eastName), Uri.EscapeDataString(westName));
    }

    private static ModelVersion Model(ModelStatus status) => new()
    {
        ModelName = "failure", Version = Guid.NewGuid().ToString("N")[..12], ModelType = ModelType.FailurePrediction,
        Algorithm = "RandomForest", Status = status, ArtifactPath = "x", ArtifactSha256 = new string('c', 64), CreatedAtUtc = DateTime.UtcNow,
    };

    private static AircraftRiskPrediction Prediction(Guid aircraftId, DateTime at, double probability, bool validated, Guid modelVersionId) => new()
    {
        ModelVersionId = modelVersionId,
        AircraftId = aircraftId, PredictionTimestampUtc = at, FeatureCutoffUtc = at.Date, HorizonDays = 30,
        RiskCategory = "High", Basis = PredictionBasis.Model, PredictionState = PredictionState.Scored,
        // The database refuses a probability that is not validated; the model score then lives in the snapshot only.
        Probability = validated ? probability : null, ProbabilityValidated = validated,
        FeatureSnapshot = $"{{\"model_score\": {probability.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}",
    };

    private static MaintenanceWorkOrder WorkOrder(Guid aircraftId, long creatorId, string number, WorkOrderStatus status) => new()
    {
        WorkOrderNumber = number, AircraftId = aircraftId, Priority = WorkOrderPriority.Medium, Title = "t", Description = "d",
        Status = status, CreatedByUserId = creatorId, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
    };

    [Fact]
    public async Task The_dashboard_lists_real_fleets_and_every_section_follows_the_selected_fleet()
    {
        var (east1, east2, west, eastQ, _) = await SeedAsync("F");
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);

        var all = await client.GetFromJsonAsync<JsonElement>("/api/v1/dashboards/dispatcher");
        var fleets = all.GetProperty("fleets").EnumerateArray()
            .ToDictionary(f => f.GetProperty("fleetGroup").GetString()!, f => f.GetProperty("aircraftCount").GetInt32());
        Assert.Equal(2, fleets["Fleet East F"]);
        Assert.Equal(1, fleets["Fleet West F"]);
        Assert.Equal(JsonValueKind.Null, all.GetProperty("selectedFleetGroup").ValueKind);

        var east = await client.GetFromJsonAsync<JsonElement>($"/api/v1/dashboards/dispatcher?fleetGroup={eastQ}");
        Assert.Equal("Fleet East F", east.GetProperty("selectedFleetGroup").GetString());
        var rows = east.GetProperty("aircraftRisk").EnumerateArray().ToList();
        Assert.Equal(new[] { east1.Id, east2.Id }.Order(), rows.Select(r => r.GetProperty("aircraftId").GetGuid()).Order());
        Assert.DoesNotContain(rows, r => r.GetProperty("aircraftId").GetGuid() == west.Id);
        Assert.Equal(2, east.GetProperty("fleetCounts").GetProperty("total").GetInt32());
        Assert.Equal(2, east.GetProperty("health").GetProperty("aircraft").GetInt32());
        Assert.Equal(2, east.GetProperty("readiness").GetArrayLength());
        // Readiness rows carry the same derived presentation state as Fleet Health (never a bare stored status only).
        Assert.All(east.GetProperty("readiness").EnumerateArray(),
            r => Assert.Equal("Available", r.GetProperty("derivedState").GetString()));
    }

    /// <summary>
    /// Phase 7: a candidate (Trained, unapproved) model's score - e.g. written by development scoring - is never shown as
    /// a validated probability or a provisional score; the aircraft counts as having no (approved) model prediction.
    /// </summary>
    [Fact]
    public async Task A_candidate_models_score_is_not_presented_as_a_prediction()
    {
        var aircraft = await factory.CreateAircraftAsync("DD-C-1", "Fleet Candidate C");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var candidate = Model(ModelStatus.Trained);
            db.ModelVersions.Add(candidate);
            db.RiskPredictions.Add(Prediction(aircraft.Id, DateTime.UtcNow, 0.93, validated: true, candidate.Id));
            await db.SaveChangesAsync();
        }
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        var dash = await client.GetFromJsonAsync<JsonElement>("/api/v1/dashboards/dispatcher?fleetGroup=Fleet%20Candidate%20C");
        var prediction = dash.GetProperty("prediction");
        Assert.Equal(0, prediction.GetProperty("validated").GetInt32());
        Assert.Equal(0, prediction.GetProperty("critical").GetInt32());
        Assert.Equal(1, prediction.GetProperty("noModelPrediction").GetInt32());
        var row = dash.GetProperty("aircraftRisk").EnumerateArray().Single();
        Assert.Equal("NoModelPrediction", row.GetProperty("predictionStatus").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("failureProbability").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("provisionalScore").ValueKind);
    }

    /// <summary>
    /// Regression (2026-10-28): a second scoring run on the same UTC day inserts only a newer DESCRIPTIVE indicator
    /// (model rows are de-duplicated per feature cutoff). The aircraft's model assessment must still be the one shown.
    /// </summary>
    [Fact]
    public async Task A_newer_descriptive_indicator_does_not_hide_the_latest_model_prediction()
    {
        var aircraft = await factory.CreateAircraftAsync("DD-D-1", "Fleet Descriptive D");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var active = Model(ModelStatus.Active);
            db.ModelVersions.Add(active);
            var earlier = DateTime.UtcNow.AddHours(-8);
            db.RiskPredictions.Add(Prediction(aircraft.Id, earlier, 0.61, validated: false, active.Id));
            db.RiskPredictions.Add(new AircraftRiskPrediction
            {
                AircraftId = aircraft.Id, PredictionTimestampUtc = DateTime.UtcNow, FeatureCutoffUtc = DateTime.UtcNow.Date, HorizonDays = 30,
                RiskCategory = "High", Basis = PredictionBasis.DescriptiveIndicator, PredictionState = PredictionState.Scored, FeatureSnapshot = "{}",
            });
            await db.SaveChangesAsync();
        }
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        var dash = await client.GetFromJsonAsync<JsonElement>("/api/v1/dashboards/dispatcher?fleetGroup=Fleet%20Descriptive%20D");
        var row = dash.GetProperty("aircraftRisk").EnumerateArray().Single();
        Assert.Equal("NotValidated", row.GetProperty("predictionStatus").GetString());
        Assert.Equal(0.61, row.GetProperty("provisionalScore").GetDouble(), 3);
        Assert.Equal(1, dash.GetProperty("prediction").GetProperty("notValidated").GetInt32());
    }

    [Fact]
    public async Task Only_validated_probabilities_are_tiered_and_nothing_missing_is_counted_as_low()
    {
        var (east1, east2, _, eastQ, westQ) = await SeedAsync("P");
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        var dash = await client.GetFromJsonAsync<JsonElement>($"/api/v1/dashboards/dispatcher?fleetGroup={eastQ}");

        var prediction = dash.GetProperty("prediction");
        Assert.Equal(30, prediction.GetProperty("horizonDays").GetInt32());
        Assert.Equal(1, prediction.GetProperty("validated").GetInt32());
        Assert.Equal(1, prediction.GetProperty("critical").GetInt32());     // 0.82 > 0.70 (seeded BR-03)
        Assert.Equal(0, prediction.GetProperty("low").GetInt32());
        Assert.Equal(1, prediction.GetProperty("notValidated").GetInt32());

        var row = (Guid id) => dash.GetProperty("aircraftRisk").EnumerateArray().Single(r => r.GetProperty("aircraftId").GetGuid() == id);
        Assert.Equal("Validated", row(east1.Id).GetProperty("predictionStatus").GetString());
        Assert.Equal(0.82, row(east1.Id).GetProperty("failureProbability").GetDouble(), 3);
        Assert.Equal("Critical", row(east1.Id).GetProperty("riskTier").GetString());
        Assert.Equal("NotValidated", row(east2.Id).GetProperty("predictionStatus").GetString());
        Assert.Equal(JsonValueKind.Null, row(east2.Id).GetProperty("failureProbability").ValueKind);
        Assert.Equal(JsonValueKind.Null, row(east2.Id).GetProperty("riskTier").ValueKind);
        // The unvalidated model output is exposed only as a provisional score - never as failureProbability.
        Assert.Equal(0.55, row(east2.Id).GetProperty("provisionalScore").GetDouble(), 3);
        Assert.Equal(JsonValueKind.Null, row(east1.Id).GetProperty("provisionalScore").ValueKind);   // validated row

        var westDash = await client.GetFromJsonAsync<JsonElement>($"/api/v1/dashboards/dispatcher?fleetGroup={westQ}");
        Assert.Equal(1, westDash.GetProperty("prediction").GetProperty("noModelPrediction").GetInt32());
        Assert.Equal(0, westDash.GetProperty("prediction").GetProperty("low").GetInt32());
        // Without telemetry an aircraft is not assessable - never "within limits".
        Assert.Equal(1, westDash.GetProperty("health").GetProperty("notAssessable").GetInt32());
        Assert.Equal(0, westDash.GetProperty("health").GetProperty("withinLimits").GetInt32());
    }

    [Fact]
    public async Task Work_order_counts_agree_with_the_work_order_list()
    {
        var (east1, _, _, eastQ, _) = await SeedAsync("W");
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        var counts = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/dashboards/dispatcher?fleetGroup={eastQ}"))
            .GetProperty("workOrders");
        Assert.Equal(1, counts.GetProperty("open").GetInt32());
        Assert.Equal(1, counts.GetProperty("resolved").GetInt32());
        Assert.Equal(0, counts.GetProperty("closed").GetInt32());     // the Closed one belongs to Fleet West

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/v1/work-orders?aircraftId={east1.Id}&pageSize=100");
        var listed = list.GetProperty("items").EnumerateArray().GroupBy(w => w.GetProperty("status").GetString())
            .ToDictionary(g => g.Key!, g => g.Count());
        Assert.Equal(counts.GetProperty("open").GetInt32(), listed.GetValueOrDefault("Open"));
        Assert.Equal(counts.GetProperty("resolved").GetInt32(), listed.GetValueOrDefault("Resolved"));
    }

    [Fact]
    public async Task An_unknown_fleet_is_rejected_not_shown_as_an_empty_fleet()
    {
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        var response = await client.GetAsync("/api/v1/dashboards/dispatcher?fleetGroup=No%20Such%20Fleet");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
