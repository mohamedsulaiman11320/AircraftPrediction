using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AFP.IntegrationTests;

/// <summary>
/// Fleet Health (dispatcher tab 2) over the real HTTP pipeline. Values must trace back to stored telemetry and
/// to the seeded engine-health thresholds; nothing may be fabricated when data is missing.
/// </summary>
public class FleetHealthApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const double KpaPerPsi = 6.894757293168;

    private async Task<Aircraft> AircraftWithReadingsAsync(string registration, params (double Psi, double TempC, double Hours)[] readings)
    {
        var aircraft = await factory.CreateAircraftAsync(registration);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var (psi, tempC, hours) in readings)
        {
            db.TelemetryRecords.Add(new TelemetryRecord
            {
                AircraftId = aircraft.Id,
                RecordedAtUtc = DateTime.UtcNow.AddHours(-hours),
                TemperatureC = tempC,
                TemperatureValue = tempC,
                TemperatureUnit = "C",
                PressureKpa = psi * KpaPerPsi,
                PressureValue = psi,
                PressureUnit = "psi",
                VibrationIndex = 1.4,
                DataSource = "test",
                QualityStatus = QualityStatus.Valid,
                IngestedAtUtc = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        return aircraft;
    }

    private static JsonElement HealthOf(JsonElement page, Guid aircraftId) =>
        page.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetGuid() == aircraftId)
            .GetProperty("health");

    [Fact]
    public async Task Latest_readings_and_a_threshold_breach_are_reported_from_stored_telemetry()
    {
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        // Oil pressure 6.2 PSI is below the recorded 35 PSI limit; temperature stays within limits.
        var aircraft = await AircraftWithReadingsAsync("IT-FH-LOWPSI", (Psi: 6.2, TempC: 480, Hours: 1));

        var page = await client.GetFromJsonAsync<JsonElement>($"/api/v1/aircraft?search={aircraft.RegistrationNumber}");
        var health = HealthOf(page, aircraft.Id);

        Assert.True(health.GetProperty("isAssessable").GetBoolean());
        Assert.True(health.GetProperty("isHighRisk").GetBoolean());
        Assert.Equal(6.2, health.GetProperty("oilPressurePsi").GetDouble(), 3);
        Assert.Equal(480, health.GetProperty("engineTemperatureC").GetDouble(), 3);
        var breach = Assert.Single(health.GetProperty("breaches").EnumerateArray());
        Assert.Equal("OilPressureLowPsi", breach.GetProperty("code").GetString());
        Assert.Equal("BR-01", breach.GetProperty("ruleReference").GetString());       // contract field; the UI shows `limit`
        Assert.Equal("Oil < 35 PSI", breach.GetProperty("limit").GetString());
        Assert.Equal("Inspect the oil pressure regulator.", health.GetProperty("recommendedInspection").GetString());
    }

    [Fact]
    public async Task Readings_within_limits_are_not_high_risk_and_get_the_routine_recommendation()
    {
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        var aircraft = await AircraftWithReadingsAsync("IT-FH-OK", (Psi: 52, TempC: 610, Hours: 2));

        var health = HealthOf(await client.GetFromJsonAsync<JsonElement>($"/api/v1/aircraft?search={aircraft.RegistrationNumber}"), aircraft.Id);

        Assert.True(health.GetProperty("isAssessable").GetBoolean());
        Assert.False(health.GetProperty("isHighRisk").GetBoolean());
        Assert.Empty(health.GetProperty("breaches").EnumerateArray());
        Assert.Equal("Routine operational check.", health.GetProperty("recommendedInspection").GetString());
    }

    [Fact]
    public async Task A_breach_anywhere_in_the_window_counts_even_when_the_latest_reading_is_normal()
    {
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        var aircraft = await AircraftWithReadingsAsync("IT-FH-WINDOW",
            (Psi: 50, TempC: 950, Hours: 10),  // breaches the temperature limit earlier in the window
            (Psi: 50, TempC: 600, Hours: 1));  // most recent reading looks normal

        var health = HealthOf(await client.GetFromJsonAsync<JsonElement>($"/api/v1/aircraft?search={aircraft.RegistrationNumber}"), aircraft.Id);

        Assert.True(health.GetProperty("isHighRisk").GetBoolean());
        Assert.Equal("EngineTemperatureHighC", health.GetProperty("breaches")[0].GetProperty("code").GetString());
        Assert.Equal(600, health.GetProperty("engineTemperatureC").GetDouble(), 3);
    }

    /// <summary>
    /// Old data is assessed over its own latest 24 h and flagged stale: the last known breach stays visible and is
    /// clearly marked as old. (Until 2026-10-27 the window ended at "now", so old data became "not assessable" and
    /// the breach disappeared - see the next test for why that was wrong with daily batches.)
    /// </summary>
    [Fact]
    public async Task Old_readings_keep_their_last_known_breach_and_are_flagged_stale_never_healthy()
    {
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        var aircraft = await AircraftWithReadingsAsync("IT-FH-STALE", (Psi: 6, TempC: 980, Hours: 72));

        var health = HealthOf(await client.GetFromJsonAsync<JsonElement>($"/api/v1/aircraft?search={aircraft.RegistrationNumber}"), aircraft.Id);

        Assert.True(health.GetProperty("isStale").GetBoolean());
        Assert.Equal(48, health.GetProperty("staleAfterHours").GetDouble(), 3);   // seeded TelemetryStaleAfterHours
        Assert.True(health.GetProperty("isHighRisk").GetBoolean());               // last known state, not "healthy"
        Assert.Equal(1, health.GetProperty("readingsInWindow").GetInt32());
        Assert.Equal(980, health.GetProperty("engineTemperatureC").GetDouble(), 3);
    }

    /// <summary>
    /// Regression (2026-10-27): telemetry arrives as a daily batch at 00:00 IST, so by late evening the newest reading
    /// is ~20 h old. A window ending at "now" then held only part of the last day and BR-01 breaches dropped off the
    /// dashboard as the clock moved. The window must end at the aircraft's latest reading.
    /// </summary>
    [Fact]
    public async Task The_24h_window_ends_at_the_latest_reading_not_at_the_clock()
    {
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        // Newest reading 20 h ago (normal); a breach 40 h ago = 20 h before the newest reading -> inside its 24 h.
        var aircraft = await AircraftWithReadingsAsync("IT-FH-BATCH", (Psi: 50, TempC: 520, Hours: 20), (Psi: 20, TempC: 520, Hours: 40));

        var health = HealthOf(await client.GetFromJsonAsync<JsonElement>($"/api/v1/aircraft?search={aircraft.RegistrationNumber}"), aircraft.Id);

        Assert.Equal(2, health.GetProperty("readingsInWindow").GetInt32());
        Assert.True(health.GetProperty("isHighRisk").GetBoolean());               // 20 PSI < 35 PSI within the window
        Assert.False(health.GetProperty("isStale").GetBoolean());                 // 20 h < 48 h
    }

    [Fact]
    public async Task An_aircraft_with_no_telemetry_reports_no_readings_and_no_risk_score()
    {
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        var aircraft = await factory.CreateAircraftAsync("IT-FH-NONE");

        var health = HealthOf(await client.GetFromJsonAsync<JsonElement>($"/api/v1/aircraft?search={aircraft.RegistrationNumber}"), aircraft.Id);

        Assert.False(health.GetProperty("isAssessable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, health.GetProperty("latestReadingAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, health.GetProperty("engineTemperatureC").ValueKind);
    }

    /// <summary>No model has produced a validated probability, so no score and no tier may be reported.</summary>
    [Fact]
    public async Task Risk_score_and_tier_are_absent_until_a_validated_prediction_exists()
    {
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        var aircraft = await AircraftWithReadingsAsync("IT-FH-NORISK", (Psi: 50, TempC: 600, Hours: 1));

        var health = HealthOf(await client.GetFromJsonAsync<JsonElement>($"/api/v1/aircraft?search={aircraft.RegistrationNumber}"), aircraft.Id);

        Assert.Equal(JsonValueKind.Null, health.GetProperty("failureRiskScore").ValueKind);
        Assert.Equal(JsonValueKind.Null, health.GetProperty("failureRiskTier").ValueKind);
    }

    [Fact]
    /// <summary>
    /// thresholdsValidated must report the seeded approval state of the BR-01 limits - whatever it is - never a constant.
    /// (The limits were approved by the business owner on 2026-10-27; before that this asserted "false".)
    /// </summary>
    public async Task Thresholds_report_their_seeded_approval_state_and_carry_their_source()
    {
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        var aircraft = await AircraftWithReadingsAsync("IT-FH-SRC", (Psi: 50, TempC: 600, Hours: 1));

        var health = HealthOf(await client.GetFromJsonAsync<JsonElement>($"/api/v1/aircraft?search={aircraft.RegistrationNumber}"), aircraft.Id);

        var seedRoot = AFP.SeedSql.SeedPaths.FindSeedRoot(AppContext.BaseDirectory)!;
        var expected = AFP.SeedSql.SeedCompiler.ReadRecords(seedRoot, "health_threshold")
            .Where(r => r["code"] is "OilPressureLowPsi" or "EngineTemperatureHighC")
            .All(r => bool.Parse(r["is_validated"]));
        Assert.Equal(expected, health.GetProperty("thresholdsValidated").GetBoolean());
        Assert.Contains("BRD", health.GetProperty("thresholdSource").GetString());
    }

    [Fact]
    public async Task Aircraft_detail_carries_the_same_health_block()
    {
        var client = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        var aircraft = await AircraftWithReadingsAsync("IT-FH-DETAIL", (Psi: 6.5, TempC: 500, Hours: 1));

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/aircraft/{aircraft.Id}");
        var health = detail.GetProperty("health");

        Assert.True(health.GetProperty("isHighRisk").GetBoolean());
        Assert.Equal(6.5, health.GetProperty("oilPressurePsi").GetDouble(), 3);
    }

    [Fact]
    public async Task A_technician_cannot_read_fleet_health()
    {
        var client = await factory.ClientAsAsync(UserRole.MaintenanceTechnician);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/aircraft")).StatusCode);
    }
}
