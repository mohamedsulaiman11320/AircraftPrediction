using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AFP.Application.Services;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AFP.IntegrationTests;

/// <summary>
/// Phase 5 end to end: approved BR-01 rules -> anomaly -> one automatic work order with a stored diagnosis -> the full
/// NEW/ASSIGNED/IN_PROGRESS/REPORTED/COMPLETED lifecycle over HTTP, with append-only reports and maintenance history
/// only after acceptance. The seeded health_threshold rows (BR-01 approved 2026-10-27) are the real ones.
/// </summary>
public class AnomalyWorkflowApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const double KpaPerPsi = 6.894757293168;

    private async Task<Aircraft> AircraftWithReadingsAsync(string registration, params (double Psi, double TempC, double Hours)[] readings)
    {
        var aircraft = await factory.CreateAircraftAsync(registration);
        await AddReadingsAsync(aircraft.Id, readings);
        return aircraft;
    }

    private async Task AddReadingsAsync(Guid aircraftId, params (double Psi, double TempC, double Hours)[] readings)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var (psi, tempC, hours) in readings)
            db.TelemetryRecords.Add(new TelemetryRecord
            {
                AircraftId = aircraftId, RecordedAtUtc = DateTime.UtcNow.AddHours(-hours), TemperatureC = tempC, TemperatureValue = tempC,
                TemperatureUnit = "C", PressureKpa = psi * KpaPerPsi, PressureValue = psi, PressureUnit = "psi", VibrationIndex = 1.4,
                DataSource = "test", QualityStatus = QualityStatus.Valid, IngestedAtUtc = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
    }

    private async Task<AnomalyProcessingResult> ProcessAsync()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAnomalyProcessingService>().RunAsync(default);
    }

    private async Task<List<AircraftAnomalyResult>> AnomaliesOf(Guid aircraftId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AnomalyResults.AsNoTracking()
            .Where(a => a.AircraftId == aircraftId).ToListAsync();
    }

    private async Task<List<MaintenanceWorkOrder>> WorkOrdersOf(Guid aircraftId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().WorkOrders.AsNoTracking()
            .Where(w => w.AircraftId == aircraftId).ToListAsync();
    }

    /// <summary>The Active sensor forecaster (one per type): created once, validated for 30-day temperature and pressure.</summary>
    private async Task<ModelVersion> ActiveForecasterAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.ModelVersions.FirstOrDefaultAsync(m => m.ModelType == ModelType.SensorForecast && m.Status == ModelStatus.Active);
        if (existing is not null) return existing;
        var model = new ModelVersion
        {
            ModelName = "sensor_forecast_level_profile", Version = Guid.NewGuid().ToString("N")[..12], ModelType = ModelType.SensorForecast,
            Algorithm = "Level + hour-of-day profile", Status = ModelStatus.Active, ArtifactPath = "x", ArtifactSha256 = new string('e', 64),
            CreatedAtUtc = DateTime.UtcNow, Metrics = "{\"supported_horizons\": {\"ThirtyDays\": [\"temperatureC\", \"pressurePsi\"]}}",
        };
        db.ModelVersions.Add(model);
        await db.SaveChangesAsync();
        return model;
    }

    /// <summary>Stores a 30-day forecast: one hourly point per (temperature, PSI) value, starting just after the cutoff.</summary>
    private async Task ForecastAsync(Guid aircraftId, DateTime asOf, params (double TempC, double Psi)[] values)
    {
        var model = await ActiveForecasterAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var points = values.Select((v, i) => new { timestampUtc = asOf.AddHours(i + 1), temperatureC = v.TempC, pressurePsi = v.Psi, vibrationIndex = (double?)null });
        db.SensorForecasts.Add(new SensorForecast
        {
            AircraftId = aircraftId, ModelVersionId = model.Id, Horizon = SensorForecastHorizon.ThirtyDays,
            GeneratedAtUtc = DateTime.UtcNow, DataAsOfUtc = asOf, Points = JsonSerializer.Serialize(points, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        });
        await db.SaveChangesAsync();
    }

    private async Task<Aircraft> AircraftRowAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Aircraft.AsNoTracking().SingleAsync(a => a.Id == id);
    }

    [Fact]
    public async Task Limits_that_start_breaching_in_the_same_hour_are_one_anomaly_with_one_NEW_work_order_and_a_plain_english_diagnosis()
    {
        var aircraft = await factory.CreateAircraftAsync("AW-FC");
        var asOf = DateTime.UtcNow.Date.AddHours(1);                                        // all points on one UTC day
        await ForecastAsync(aircraft.Id, asOf, (650, 45), (760, 30), (780, 29));

        await ProcessAsync();
        await ProcessAsync();                                                               // rerun: still one record

        var anomaly = Assert.Single(await AnomaliesOf(aircraft.Id));                       // temperature + oil, same hour
        Assert.Equal("EngineTemperatureHighC,OilPressureLowPsi", anomaly.RuleCodes);
        Assert.Equal(AnomalySource.ForecastModel, anomaly.Source);
        Assert.Equal(asOf.AddHours(2), anomaly.ObservedAtUtc, TimeSpan.FromSeconds(1));    // first breaching hour
        Assert.Equal(asOf.AddHours(3), anomaly.LastObservedAtUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(AnomalyProcessingService.DedupKey("AW-FC", asOf.AddHours(2), anomaly.ModelVersionId!.Value), anomaly.DedupKey);
        using (var evidence = JsonDocument.Parse(anomaly.Evidence!))
        {
            Assert.Equal(2, evidence.RootElement.GetProperty("breaches").GetArrayLength());
            Assert.Equal("ThirtyDays", evidence.RootElement.GetProperty("horizon").GetString());
        }
        var wo = Assert.Single(await WorkOrdersOf(aircraft.Id));                           // one ticket for the day
        Assert.Equal(anomaly.Id, wo.AnomalyId);
        Assert.Equal(wo.Id, anomaly.WorkOrderId);
        Assert.Equal(WorkOrderStatus.Open, wo.Status);                                      // NEW
        Assert.Equal(WorkOrderSource.Automatic, wo.Source);
        Assert.Null(wo.AssignedTechnicianId);
        Assert.Equal(DiagnosisStatus.Generated, wo.DiagnosisStatus);
        Assert.Contains("Engine exhaust temperature is forecast to rise above 700", wo.DiagnosisText);
        Assert.Contains("Oil pressure is forecast to fall below 35", wo.DiagnosisText);
        Assert.Contains("thermal shroud", wo.DiagnosisText);                                 // approved inspection wording
        Assert.Contains("Inspect the oil pressure regulator.", wo.DiagnosisText);
        Assert.Contains("not a confirmed fault", wo.DiagnosisText);
        foreach (var jargon in new[] { "BR-01", "BR-04", "EngineTemperatureHighC", "OilPressureLowPsi" })
        {
            Assert.DoesNotContain(jargon, wo.DiagnosisText);
            Assert.DoesNotContain(jargon, wo.Title);
        }
        var dispatcher = await factory.ClientAsAsync(UserRole.FlightDispatcher);              // a ticket is not maintenance
        var logs = await dispatcher.GetFromJsonAsync<JsonElement>($"/api/v1/maintenance-logs?aircraftId={aircraft.Id}");
        Assert.Equal(0, logs.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Reprocessing_and_a_newer_still_breaching_forecast_create_nothing_new_until_the_incident_is_closed()
    {
        var aircraft = await factory.CreateAircraftAsync("AW-FC-REPEAT");
        var asOf = DateTime.UtcNow.Date.AddHours(1);
        await ForecastAsync(aircraft.Id, asOf, (750, 45), (760, 45));
        await ProcessAsync();
        await ProcessAsync();                                                               // same forecast: idempotent
        Assert.Single(await AnomaliesOf(aircraft.Id));
        Assert.Single(await WorkOrdersOf(aircraft.Id));

        await ForecastAsync(aircraft.Id, asOf.AddHours(1), (770, 45), (790, 45));           // newer, still breaching
        var run = await ProcessAsync();
        var open = Assert.Single(await AnomaliesOf(aircraft.Id));
        Assert.NotNull(open.LastObservedAtUtc);
        Assert.Single(await WorkOrdersOf(aircraft.Id));                                     // no second ticket
        Assert.True(run.ForecastIncidentsContinuing >= 1);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.AnomalyResults.SingleAsync(a => a.Id == open.Id)).InvestigationStatus = InvestigationStatus.ConfirmedIssue;
            await db.SaveChangesAsync();
        }
        await ForecastAsync(aircraft.Id, asOf.AddHours(2), (800, 45));                     // after closure: a new incident
        await ProcessAsync();
        var both = await AnomaliesOf(aircraft.Id);
        Assert.Equal(2, both.Count);
        var ticket = Assert.Single(await WorkOrdersOf(aircraft.Id));                       // same projected day: joins its ticket
        Assert.All(both, a => Assert.Equal(ticket.Id, a.WorkOrderId));
    }

    [Fact]
    public async Task Limits_that_start_breaching_in_different_hours_are_separate_anomalies()
    {
        var aircraft = await factory.CreateAircraftAsync("AW-FC-STAGGER");
        var asOf = DateTime.UtcNow.Date.AddHours(1);
        await ForecastAsync(aircraft.Id, asOf, (760, 45), (770, 30), (780, 29));         // temp from +1 h, oil from +2 h

        await ProcessAsync();

        var anomalies = (await AnomaliesOf(aircraft.Id)).OrderBy(a => a.ObservedAtUtc).ToList();
        Assert.Equal(["EngineTemperatureHighC", "OilPressureLowPsi"], anomalies.Select(a => a.RuleCodes));
        Assert.Single(await WorkOrdersOf(aircraft.Id));                                     // same projected day
    }

    [Fact]
    public async Task A_breach_that_returns_within_limits_and_breaches_again_is_a_second_anomaly_and_each_projected_day_gets_its_own_ticket()
    {
        var aircraft = await factory.CreateAircraftAsync("AW-FC-EPISODES");
        var asOf = DateTime.UtcNow.Date;                                                    // hour 1 = today 01:00 UTC
        var points = Enumerable.Repeat((TempC: 650.0, Psi: 45.0), 30).ToArray();
        points[0] = (760, 45);                                                              // today 01:00
        points[2] = (770, 45);                                                              // today 03:00, after a dip
        points[25] = (780, 45);                                                             // tomorrow 02:00
        await ForecastAsync(aircraft.Id, asOf, points);

        var run = await ProcessAsync();

        var anomalies = (await AnomaliesOf(aircraft.Id)).OrderBy(a => a.ObservedAtUtc).ToList();
        Assert.Equal(3, run.ForecastIncidentsCreated);
        Assert.Equal([asOf.AddHours(1), asOf.AddHours(3), asOf.AddHours(26)], anomalies.Select(a => a.ObservedAtUtc.ToUniversalTime()));
        var tickets = await WorkOrdersOf(aircraft.Id);
        Assert.Equal(2, tickets.Count);                                                     // today and tomorrow
        Assert.Equal(anomalies[0].WorkOrderId, anomalies[1].WorkOrderId);
        Assert.NotEqual(anomalies[0].WorkOrderId, anomalies[2].WorkOrderId);
        var today = tickets.Single(t => t.Id == anomalies[0].WorkOrderId);
        Assert.Equal(2, today.DiagnosisText!.Split("forecast to rise above 700").Length - 1);   // both breaches of the day

        var anomaliesPage = await (await factory.ClientAsAsync(UserRole.FlightDispatcher))
            .GetFromJsonAsync<JsonElement>($"/api/v1/work-orders?anomalyId={anomalies[1].Id}");
        Assert.Equal(today.WorkOrderNumber, anomaliesPage.GetProperty("items")[0].GetProperty("workOrderNumber").GetString());
    }

    [Fact]
    public async Task Breaching_recorded_telemetry_raises_no_anomaly_and_no_work_order()
    {
        var aircraft = await AircraftWithReadingsAsync("AW-RECORDED", (Psi: 30, TempC: 760, Hours: 2));
        await ProcessAsync();
        Assert.Empty(await AnomaliesOf(aircraft.Id));
        Assert.Empty(await WorkOrdersOf(aircraft.Id));
    }

    [Fact]
    public async Task Open_forecast_work_places_the_aircraft_in_maintenance_and_completion_releases_it()
    {
        var aircraft = await factory.CreateAircraftAsync("AW-FC-SYNC");                    // created Available
        await ForecastAsync(aircraft.Id, DateTime.UtcNow.AddHours(-1), (760, 45));
        var run = await ProcessAsync();
        Assert.Contains("AW-FC-SYNC", run.PlacedInMaintenance!);
        var placed = await AircraftRowAsync(aircraft.Id);
        Assert.Equal(OperationalStatus.UnderMaintenance, placed.OperationalStatus);
        Assert.StartsWith(AFP.Domain.Rules.AnomalyRules.ForecastMaintenanceReason, placed.StatusReason);

        var wo = Assert.Single(await WorkOrdersOf(aircraft.Id));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.WorkOrders.SingleAsync(w => w.Id == wo.Id)).Status = WorkOrderStatus.Resolved;   // REPORTED only
            await db.SaveChangesAsync();
        }
        await ProcessAsync();
        Assert.Equal(OperationalStatus.UnderMaintenance, (await AircraftRowAsync(aircraft.Id)).OperationalStatus);   // not yet accepted

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.WorkOrders.SingleAsync(w => w.Id == wo.Id)).Status = WorkOrderStatus.Closed;     // COMPLETED
            await db.SaveChangesAsync();
        }
        var after = await ProcessAsync();
        Assert.Contains("AW-FC-SYNC", after.ReleasedToService!);
        Assert.Equal(OperationalStatus.Available, (await AircraftRowAsync(aircraft.Id)).OperationalStatus);
    }

    [Fact]
    public async Task An_aircraft_a_person_put_under_maintenance_is_never_released_or_relabelled_by_the_run()
    {
        var aircraft = await factory.CreateAircraftAsync("AW-FC-HUMAN");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Aircraft.SingleAsync(a => a.Id == aircraft.Id);
            row.OperationalStatus = OperationalStatus.UnderMaintenance;
            row.StatusReason = "Engine borescope scheduled by engineering.";
            await db.SaveChangesAsync();
        }
        await ForecastAsync(aircraft.Id, DateTime.UtcNow.AddHours(-1), (760, 45));
        await ProcessAsync();
        Assert.Equal("Engine borescope scheduled by engineering.", (await AircraftRowAsync(aircraft.Id)).StatusReason);
    }

    [Fact]
    public async Task An_aircraft_within_limits_raises_nothing()
    {
        var aircraft = await AircraftWithReadingsAsync("AW-OK", (Psi: 50, TempC: 600, Hours: 1));
        await ForecastAsync(aircraft.Id, DateTime.UtcNow.AddHours(-1), (600, 45), (610, 44));
        await ProcessAsync();
        Assert.Empty(await AnomaliesOf(aircraft.Id));
        Assert.Empty(await WorkOrdersOf(aircraft.Id));
    }

    private async Task<ModelVersion> FailureModelAsync(ModelStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var model = new ModelVersion
        {
            ModelName = "failure", Version = Guid.NewGuid().ToString("N")[..12], ModelType = ModelType.FailurePrediction,
            Algorithm = "RandomForest", Status = status, ArtifactPath = "x", ArtifactSha256 = new string('b', 64), CreatedAtUtc = DateTime.UtcNow,
        };
        db.ModelVersions.Add(model);
        await db.SaveChangesAsync();
        return model;
    }

    private async Task PredictAsync(Guid aircraftId, double? probability, bool validated, Guid? modelVersionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.RiskPredictions.Add(new AircraftRiskPrediction
        {
            AircraftId = aircraftId, PredictionTimestampUtc = DateTime.UtcNow, FeatureCutoffUtc = DateTime.UtcNow.Date,
            HorizonDays = 30, RiskCategory = "High", Basis = PredictionBasis.Model, PredictionState = PredictionState.Scored,
            Probability = validated ? probability : null, ProbabilityValidated = validated, ModelVersionId = modelVersionId,
            FeatureSnapshot = validated ? "{}" : "{\"model_score\": " + probability!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}",
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Runs <paramref name="body"/> with the BR-03 tier rows approved, restoring the seeded state afterwards.</summary>
    private async Task WithApprovedRiskTiersAsync(Func<Task> body)
    {
        async Task SetAsync(bool approved)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tiers = await db.HealthThresholds.Where(t => t.Measurement == AFP.Domain.Rules.FleetHealthRules.MeasurementFailureRiskScore).ToListAsync();
            Assert.NotEmpty(tiers);
            foreach (var t in tiers) t.IsValidated = approved;
            await db.SaveChangesAsync();
        }
        await SetAsync(true);
        try { await body(); }
        finally { await SetAsync(false); }
    }

    /// <summary>
    /// Since 2026-10-28 anomalies and work orders come from the forecast only: even a validated Critical probability from
    /// an Active model with approved risk tiers raises neither.
    /// </summary>
    [Fact]
    public async Task A_failure_model_probability_never_raises_an_anomaly_or_a_work_order()
    {
        var aircraft = await AircraftWithReadingsAsync("AW-VALID", (Psi: 50, TempC: 600, Hours: 1));
        var active = await FailureModelAsync(ModelStatus.Active);
        await PredictAsync(aircraft.Id, 0.95, validated: true, active.Id);

        await WithApprovedRiskTiersAsync(async () =>
        {
            var run = await ProcessAsync();
            Assert.Equal(0, run.ModelIncidentsCreated);
            Assert.Empty(await AnomaliesOf(aircraft.Id));
            Assert.Empty(await WorkOrdersOf(aircraft.Id));
        });
    }

    [Fact]
    public async Task The_whole_lifecycle_keeps_every_report_and_publishes_maintenance_only_on_acceptance()
    {
        var aircraft = await AircraftWithReadingsAsync("AW-LIFE", (Psi: 30, TempC: 600, Hours: 1));
        var (dispatcher, _) = await factory.ClientAndUserAsync(UserRole.FlightDispatcher);
        var (tech, techUser) = await factory.ClientAndUserAsync(UserRole.MaintenanceTechnician);
        var (otherTech, _) = await factory.ClientAndUserAsync(UserRole.MaintenanceTechnician);

        // Manual ticket: source MANUAL, status NEW, diagnosis stored.
        var created = await (await dispatcher.PostAsJsonAsync("/api/v1/work-orders", new
        {
            aircraftId = aircraft.Id, priority = "High", title = "Low oil pressure seen on walkround", description = "Crew report: low oil pressure",
        })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal("Manual", created.GetProperty("source").GetString());
        Assert.Equal("Open", created.GetProperty("status").GetString());
        Assert.Equal("Generated", created.GetProperty("diagnosisStatus").GetString());
        Assert.Contains("Raised manually by a dispatcher", created.GetProperty("diagnosisText").GetString());

        // A technician cannot act on a ticket that is not theirs; the dispatcher assigns.
        Assert.Equal(HttpStatusCode.NotFound, (await tech.PostAsJsonAsync($"/api/v1/work-orders/{id}/start-inspection", new { })).StatusCode);
        Assert.True((await dispatcher.PostAsJsonAsync($"/api/v1/work-orders/{id}/assign", new { technicianId = techUser.Id })).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherTech.GetAsync($"/api/v1/work-orders/{id}")).StatusCode);

        Assert.True((await tech.PostAsJsonAsync($"/api/v1/work-orders/{id}/start-inspection", new { })).IsSuccessStatusCode);   // IN_PROGRESS
        Assert.True((await tech.PostAsJsonAsync($"/api/v1/work-orders/{id}/inspection", new { inspectionFindings = "Regulator sticking" })).IsSuccessStatusCode);

        // The report must carry an APPROVED action code and >= 10 characters of notes.
        Assert.Equal(HttpStatusCode.BadRequest, (await tech.PostAsJsonAsync($"/api/v1/work-orders/{id}/resolve",
            new { correctiveAction = "Cleaned", actualFailureConfirmed = false, resolutionNotes = "Cleaned regulator" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await tech.PostAsJsonAsync($"/api/v1/work-orders/{id}/resolve",
            new { correctiveAction = "Cleaned", actualFailureConfirmed = false, resolutionNotes = "Cleaned regulator", actionCode = "TYRE_CHG" })).StatusCode);
        Assert.True((await tech.PostAsJsonAsync($"/api/v1/work-orders/{id}/resolve",
            new { correctiveAction = "Cleaned regulator", actualFailureConfirmed = false, resolutionNotes = "First attempt: cleaned regulator", actionCode = "ENG_INSP" })).IsSuccessStatusCode);   // REPORTED

        var logsOf = async () => (await dispatcher.GetFromJsonAsync<JsonElement>($"/api/v1/maintenance-logs?aircraftId={aircraft.Id}")).GetProperty("totalCount").GetInt32();
        Assert.Equal(0, await logsOf());                                           // reported, not yet accepted
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.PostAsJsonAsync($"/api/v1/work-orders/{id}/close", new { })).StatusCode);

        // Send back: a reason is mandatory; the ticket returns to ASSIGNED and the first report is kept.
        Assert.Equal(HttpStatusCode.BadRequest, (await dispatcher.PostAsJsonAsync($"/api/v1/work-orders/{id}/reopen", new { reason = "" })).StatusCode);
        var reopened = await (await dispatcher.PostAsJsonAsync($"/api/v1/work-orders/{id}/reopen", new { reason = "Pressure still low" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Assigned", reopened.GetProperty("status").GetString());

        Assert.True((await tech.PostAsJsonAsync($"/api/v1/work-orders/{id}/start-inspection", new { })).IsSuccessStatusCode);
        Assert.True((await tech.PostAsJsonAsync($"/api/v1/work-orders/{id}/resolve",
            new { correctiveAction = "Replaced oil filter", actualFailureConfirmed = false, resolutionNotes = "Second attempt: filter replaced", actionCode = "OIL_REPL" })).IsSuccessStatusCode);

        // The dispatcher accepts with review notes kept apart from the technician's report.
        var closed = await (await dispatcher.PostAsJsonAsync($"/api/v1/work-orders/{id}/close", new { resolutionNotes = "Accepted after leak check" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Closed", closed.GetProperty("status").GetString());          // COMPLETED
        Assert.Equal("Accepted after leak check", closed.GetProperty("dispatcherReviewNotes").GetString());
        Assert.Equal("Second attempt: filter replaced", closed.GetProperty("resolutionNotes").GetString());

        var detail = await dispatcher.GetFromJsonAsync<JsonElement>($"/api/v1/work-orders/{id}");
        var reports = detail.GetProperty("reports").EnumerateArray().ToList();
        Assert.Equal([1, 2], reports.Select(r => r.GetProperty("sequence").GetInt32()));
        Assert.Equal("First attempt: cleaned regulator", reports[0].GetProperty("resolutionNotes").GetString());   // never overwritten
        Assert.Equal("ENG_INSP", reports[0].GetProperty("actionCode").GetString());
        Assert.Equal("OIL_REPL", reports[1].GetProperty("actionCode").GetString());
        Assert.All(reports, r => Assert.Equal(techUser.Id, r.GetProperty("technicianUserId").GetInt64()));

        Assert.Equal(1, await logsOf());                                           // published exactly once, on acceptance
        var log = (await dispatcher.GetFromJsonAsync<JsonElement>($"/api/v1/maintenance-logs?aircraftId={aircraft.Id}"))
            .GetProperty("items")[0];
        Assert.Contains("[OIL_REPL]", log.GetProperty("description").GetString());
    }

    [Fact]
    public async Task The_work_order_list_filters_by_source_on_the_server()
    {
        var aircraft = await factory.CreateAircraftAsync("AW-SRC");
        await ForecastAsync(aircraft.Id, DateTime.UtcNow.AddHours(-1), (760, 30));
        await ProcessAsync();                                                   // one Automatic (forecast) ticket
        var dispatcher = await factory.ClientAsAsync(UserRole.FlightDispatcher);
        await dispatcher.PostAsJsonAsync("/api/v1/work-orders", new { aircraftId = aircraft.Id, priority = "Low", title = "Manual", description = "Manual check" });
        async Task<List<string?>> Sources(string query) =>
            (await dispatcher.GetFromJsonAsync<JsonElement>($"/api/v1/work-orders?aircraftId={aircraft.Id}{query}&pageSize=100"))
                .GetProperty("items").EnumerateArray().Select(w => w.GetProperty("source").GetString()).ToList();
        Assert.Equal(["Automatic"], await Sources("&source=Automatic"));
        Assert.Equal(["Manual"], await Sources("&source=Manual"));
        Assert.Equal(2, (await Sources("")).Count);
    }

    [Theory]
    [InlineData(UserRole.FlightDispatcher, HttpStatusCode.OK)]
    [InlineData(UserRole.MaintenanceTechnician, HttpStatusCode.OK)]
    [InlineData(UserRole.DataEngineer, HttpStatusCode.Forbidden)]
    public async Task Approved_action_codes_are_available_to_the_people_who_report_and_review_work(UserRole role, HttpStatusCode expected)
    {
        var client = await factory.ClientAsAsync(role);
        var response = await client.GetAsync("/api/v1/maintenance-action-codes");
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            var codes = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Select(c => c.GetProperty("code").GetString());
            Assert.Equal(["ENG_INSP", "OIL_REPL"], codes);                          // exactly the seeded, approved codes
        }
    }
}
