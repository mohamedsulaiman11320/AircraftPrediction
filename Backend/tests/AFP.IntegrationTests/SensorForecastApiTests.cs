using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AFP.IntegrationTests;

/// <summary>
/// GET /aircraft/{id}/sensor-forecast serves stored forecasts of the ACTIVE SensorForecast model only, for its validated
/// horizons and metrics. Recorded telemetry is never returned in place of a forecast. One factory per class, so the
/// tests run in order of their own model state (only one Active model per type is allowed).
/// </summary>
public class SensorForecastApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Supported = "{\"supported_horizons\": {\"OneDay\": [\"temperatureC\"], \"SevenDays\": [\"temperatureC\", \"pressurePsi\"]}}";

    private async Task<Aircraft> AircraftWithTelemetryAsync(string registration)
    {
        var aircraft = await factory.CreateAircraftAsync(registration);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        for (var h = 1; h <= 3; h++)
            db.TelemetryRecords.Add(new TelemetryRecord
            {
                AircraftId = aircraft.Id, RecordedAtUtc = DateTime.UtcNow.AddHours(-h), TemperatureC = 650, TemperatureValue = 650,
                TemperatureUnit = "C", PressureKpa = 300, PressureValue = 300, PressureUnit = "kPa", VibrationIndex = 1.4,
                DataSource = "test", QualityStatus = QualityStatus.Valid, IngestedAtUtc = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        return aircraft;
    }

    private async Task<ModelVersion> ForecasterAsync(ModelStatus status, string metrics = Supported)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (status == ModelStatus.Active)
            foreach (var old in await db.ModelVersions.Where(m => m.ModelType == ModelType.SensorForecast && m.Status == ModelStatus.Active).ToListAsync())
                old.Status = ModelStatus.Retired;
        await db.SaveChangesAsync();
        var model = new ModelVersion
        {
            ModelName = "sensor_forecast_level_profile", Version = Guid.NewGuid().ToString("N")[..12], ModelType = ModelType.SensorForecast,
            Algorithm = "Level + hour-of-day profile", Status = status, ArtifactPath = "x", ArtifactSha256 = new string('d', 64),
            CreatedAtUtc = DateTime.UtcNow, Metrics = metrics,
        };
        db.ModelVersions.Add(model);
        await db.SaveChangesAsync();
        return model;
    }

    private async Task StoreForecastAsync(Guid aircraftId, Guid modelId, SensorForecastHorizon horizon, DateTime asOf)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var t = (int h) => asOf.AddHours(h).ToString("O");
        db.SensorForecasts.Add(new SensorForecast
        {
            AircraftId = aircraftId, ModelVersionId = modelId, Horizon = horizon, GeneratedAtUtc = DateTime.UtcNow, DataAsOfUtc = asOf,
            // The first point is AT the cutoff (must be dropped); vibration is stored but the model is not validated for it.
            Points = $"[{{\"timestampUtc\":\"{t(0)}\",\"temperatureC\":640}},"
                     + $"{{\"timestampUtc\":\"{t(1)}\",\"temperatureC\":651.5,\"temperatureCLow\":630,\"temperatureCHigh\":672,\"pressurePsi\":41,\"vibrationIndex\":9.9}},"
                     + $"{{\"timestampUtc\":\"{t(2)}\",\"temperatureC\":652.5,\"pressurePsi\":40.5}}]",
        });
        await db.SaveChangesAsync();
    }

    private async Task<JsonElement> GetAsync(HttpClient client, Guid id, string horizon)
    {
        var res = await client.GetAsync($"/api/v1/aircraft/{id}/sensor-forecast?horizon={horizon}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Without_an_active_forecaster_every_horizon_is_unavailable_and_telemetry_is_never_substituted()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var m in await db.ModelVersions.Where(m => m.ModelType == ModelType.SensorForecast && m.Status == ModelStatus.Active).ToListAsync())
                m.Status = ModelStatus.Retired;
            await db.SaveChangesAsync();
        }
        var aircraft = await AircraftWithTelemetryAsync("SF-NONE");
        var trained = await ForecasterAsync(ModelStatus.Trained);                    // a candidate with stored output...
        await StoreForecastAsync(aircraft.Id, trained.Id, SensorForecastHorizon.OneDay, DateTime.UtcNow.AddHours(-1));
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        foreach (var horizon in Enum.GetNames<SensorForecastHorizon>())
        {
            var body = await GetAsync(client, aircraft.Id, horizon);                 // ...is never served
            Assert.Equal("NotAvailable", body.GetProperty("status").GetString());
            Assert.Contains("No approved sensor-forecasting model is active", body.GetProperty("reason").GetString());
            Assert.Equal(0, body.GetProperty("points").GetArrayLength());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("generatedAtUtc").ValueKind);
        }
    }

    [Fact]
    public async Task The_active_forecaster_serves_only_validated_horizons_and_metrics_strictly_after_the_data_cutoff()
    {
        var aircraft = await AircraftWithTelemetryAsync("SF-ACTIVE");
        var active = await ForecasterAsync(ModelStatus.Active);
        var asOf = DateTime.UtcNow.AddHours(-1);
        await StoreForecastAsync(aircraft.Id, active.Id, SensorForecastHorizon.OneDay, asOf);
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);

        var body = await GetAsync(client, aircraft.Id, "OneDay");
        Assert.Equal("Available", body.GetProperty("status").GetString());
        Assert.Equal(active.Version, body.GetProperty("modelVersion").GetString());
        Assert.Equal(new[] { "OneDay", "SevenDays" }, body.GetProperty("supportedHorizons").EnumerateArray().Select(h => h.GetString()));
        Assert.Equal(new[] { "temperatureC" }, body.GetProperty("supportedMetrics").EnumerateArray().Select(h => h.GetString()));
        var points = body.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal(2, points.Count);                                                // the point AT the cutoff is dropped
        Assert.All(points, p => Assert.True(p.GetProperty("timestampUtc").GetDateTime() > asOf));
        Assert.Equal(651.5, points[0].GetProperty("temperatureC").GetDouble());
        Assert.Equal(630, points[0].GetProperty("temperatureCLow").GetDouble());
        Assert.All(points, p => Assert.Equal(JsonValueKind.Null, p.GetProperty("pressurePsi").ValueKind));     // not validated for OneDay
        Assert.All(points, p => Assert.Equal(JsonValueKind.Null, p.GetProperty("vibrationIndex").ValueKind));  // stored, never served

        var thirty = await GetAsync(client, aircraft.Id, "ThirtyDays");               // not validated
        Assert.Equal("NotAvailable", thirty.GetProperty("status").GetString());
        Assert.Contains("did not pass validation for this horizon", thirty.GetProperty("reason").GetString());
        var seven = await GetAsync(client, aircraft.Id, "SevenDays");                 // validated, nothing generated yet
        Assert.Contains("No forecast has been generated yet", seven.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_shorter_horizon_is_served_as_the_opening_hours_of_the_validated_longer_forecast()
    {
        var aircraft = await AircraftWithTelemetryAsync("SF-SLICE");
        var active = await ForecasterAsync(ModelStatus.Active, "{\"supported_horizons\": {\"ThirtyDays\": [\"temperatureC\"]}}");
        var asOf = DateTime.UtcNow.AddHours(-1);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var t = (int h) => asOf.AddHours(h).ToString("O");
            db.SensorForecasts.Add(new SensorForecast
            {
                AircraftId = aircraft.Id, ModelVersionId = active.Id, Horizon = SensorForecastHorizon.ThirtyDays,
                GeneratedAtUtc = DateTime.UtcNow, DataAsOfUtc = asOf,
                Points = $"[{{\"timestampUtc\":\"{t(1)}\",\"temperatureC\":650}},{{\"timestampUtc\":\"{t(24)}\",\"temperatureC\":660}},"
                         + $"{{\"timestampUtc\":\"{t(25)}\",\"temperatureC\":670}},{{\"timestampUtc\":\"{t(400)}\",\"temperatureC\":680}}]",
            });
            await db.SaveChangesAsync();
        }
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);

        var day = await GetAsync(client, aircraft.Id, "OneDay");
        Assert.Equal("Available", day.GetProperty("status").GetString());
        Assert.Equal("ThirtyDays", day.GetProperty("slicedFromHorizon").GetString());
        Assert.Equal(new[] { "OneDay", "SevenDays", "ThirtyDays" }, day.GetProperty("supportedHorizons").EnumerateArray().Select(h => h.GetString()));
        Assert.Equal(new[] { 650.0, 660.0 }, day.GetProperty("points").EnumerateArray().Select(p => p.GetProperty("temperatureC").GetDouble()));
        Assert.Equal(3, (await GetAsync(client, aircraft.Id, "SevenDays")).GetProperty("points").GetArrayLength());
        var month = await GetAsync(client, aircraft.Id, "ThirtyDays");
        Assert.Equal(4, month.GetProperty("points").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, month.GetProperty("slicedFromHorizon").ValueKind);
    }

    [Fact]
    public async Task A_forecaster_without_a_supported_horizon_cannot_be_approved()
    {
        var model = await ForecasterAsync(ModelStatus.Registered, "{\"supported_horizons\": {}}");
        var client = await factory.ClientAsAsync(UserRole.DataEngineer);
        var res = await client.PostAsJsonAsync($"/api/v1/models/{model.Id}/approve", new { notes = "Reviewed the backtest." });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("no_supported_horizon", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_missing_or_unknown_horizon_is_rejected_and_an_unknown_aircraft_is_404()
    {
        var aircraft = await factory.CreateAircraftAsync("SF-BAD");
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/v1/aircraft/{aircraft.Id}/sensor-forecast")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/v1/aircraft/{aircraft.Id}/sensor-forecast?horizon=TwoDays")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/aircraft/{Guid.NewGuid()}/sensor-forecast?horizon=OneDay")).StatusCode);
    }

    [Fact]
    public async Task Role_restrictions_match_the_other_aircraft_data_endpoints()
    {
        var aircraft = await factory.CreateAircraftAsync("SF-ROLE");
        var path = $"/api/v1/aircraft/{aircraft.Id}/sensor-forecast?horizon=SevenDays";
        Assert.Equal(HttpStatusCode.OK, (await (await factory.ClientAsAsync(UserRole.DataEngineer)).GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await factory.ClientAsAsync(UserRole.MaintenanceTechnician)).GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(path)).StatusCode);
    }
}
