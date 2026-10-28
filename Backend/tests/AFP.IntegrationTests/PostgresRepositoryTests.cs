using AFP.Application.Common;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Infrastructure.Persistence;
using AFP.Infrastructure.Persistence.Repositories;
using AFP.SeedSql;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AFP.IntegrationTests;

/// <summary>
/// Real PostgreSQL tests on a DISPOSABLE database named <c>aircraft_predict_db_test_*</c> (never aircraft_app or
/// aircraft_predict_db). Mode 1 (no Docker): set AFP_TEST_PG_ADMIN to a connection string for a role allowed to
/// CREATE/DROP DATABASE, e.g. "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=...".
/// Mode 2: Testcontainers (Docker). Without either, tests print SKIPPED and return.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string TestDatabasePrefix = "aircraft_predict_db_test_";

    private PostgreSqlContainer? _container;
    private string? _adminConnectionString;
    private string? _databaseName;

    public string? ConnectionString { get; private set; }
    public string? ServerAdminConnectionString => _adminConnectionString;
    public string? SkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("AFP_TEST_PG_ADMIN") is { Length: > 0 } admin)
            {
                _adminConnectionString = admin;
                _databaseName = TestDatabasePrefix + Guid.NewGuid().ToString("N")[..8];
                await ExecuteAsync(admin, $"CREATE DATABASE \"{_databaseName}\"");
                ConnectionString = new NpgsqlConnectionStringBuilder(admin) { Database = _databaseName, Pooling = false }.ConnectionString;
            }
            else if (Environment.GetEnvironmentVariable("AFP_SKIP_DOCKER_TESTS") == "1")
            {
                SkipReason = "AFP_TEST_PG_ADMIN not set and AFP_SKIP_DOCKER_TESTS=1";
                return;
            }
            else
            {
                _container = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase(TestDatabasePrefix + "container").Build();
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                await _container.StartAsync(cts.Token);
                ConnectionString = _container.GetConnectionString();
                _adminConnectionString = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres" }.ConnectionString;
            }

            await using var db = Create();
            await db.Database.MigrateAsync();
            await ApplySeedAsync(ConnectionString!);
        }
        catch (Exception ex)
        {
            SkipReason = $"PostgreSQL unavailable (set AFP_TEST_PG_ADMIN or start Docker): {ex.GetType().Name}: {ex.Message.Split('\n')[0]}";
        }
    }

    public AppDbContext Create() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(ConnectionString).UseSnakeCaseNamingConvention().Options);

    /// <summary>Executes the committed, generated reference seed (tools/db/seed/seed.sql).</summary>
    public static async Task ApplySeedAsync(string connectionString)
    {
        var root = SeedPaths.FindSeedRoot(AppContext.BaseDirectory)!;
        var sql = await File.ReadAllTextAsync(Path.Combine(root, SeedCompiler.LoadManifest(root).Output));
        await ExecuteAsync(connectionString, sql);
    }

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
        // Drops ONLY the disposable database this fixture created (name guarded by prefix).
        if (_container is null && _adminConnectionString is not null && _databaseName?.StartsWith(TestDatabasePrefix, StringComparison.Ordinal) == true)
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(_adminConnectionString, $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)");
        }
    }
}

public class PostgresRepositoryTests(PostgresFixture fx) : IClassFixture<PostgresFixture>
{
    private bool Skip()
    {
        if (fx.SkipReason is null) return false;
        Console.WriteLine($"SKIPPED (requires PostgreSQL): {fx.SkipReason}");
        return true;
    }

    private static Aircraft NewAircraft(string registration) => new()
    {
        RegistrationNumber = registration, Model = "m", Manufacturer = "m", FleetGroup = "g",
        CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow, StatusChangedAtUtc = DateTime.UtcNow,
    };

    [Fact]
    public async Task Migrations_are_idempotent_and_fully_applied()
    {
        if (Skip()) return;
        await using var db = fx.Create();
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Seed_is_idempotent_and_matches_lifecycle_enum()
    {
        if (Skip()) return;
        await PostgresFixture.ApplySeedAsync(fx.ConnectionString!);
        await using var db = fx.Create();
        var statuses = await db.Statuses.OrderBy(s => s.StatusId).ToListAsync();
        Assert.Equal(Enum.GetValues<LifecycleStatus>(), statuses.Select(s => s.StatusId));
        Assert.Equal(Enum.GetNames<LifecycleStatus>(), statuses.Select(s => s.Name));
        Assert.Equal(6, await db.RiskStatuses.CountAsync());
    }

    [Fact]
    public async Task Seed_refuses_to_run_against_any_other_database()
    {
        if (Skip()) return;
        var ex = await Assert.ThrowsAsync<PostgresException>(() => PostgresFixture.ApplySeedAsync(fx.ServerAdminConnectionString!));
        Assert.Contains("Reference seed must target aircraft_predict_db", ex.MessageText);
    }

    [Fact]
    public async Task Status_fk_rejects_unknown_status_and_accepts_seeded_values()
    {
        if (Skip()) return;
        await using var db = fx.Create();
        var ok = NewAircraft("PG-ST1");
        ok.StatusId = LifecycleStatus.Inactive;            // explicit 0 must be stored, not replaced by the default
        db.Aircraft.Add(ok);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(LifecycleStatus.Inactive, (await db.Aircraft.SingleAsync(a => a.Id == ok.Id)).StatusId);

        var bad = NewAircraft("PG-ST2");
        bad.StatusId = (LifecycleStatus)99;
        db.Aircraft.Add(bad);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ((PostgresException)error.InnerException!).SqlState);
    }

    [Fact]
    public async Task Status_rows_cannot_be_deleted_while_referenced()
    {
        if (Skip()) return;
        await using var db = fx.Create();
        db.Aircraft.Add(NewAircraft("PG-ST3"));
        await db.SaveChangesAsync();
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            PostgresFixture.ExecuteAsync(fx.ConnectionString!, "DELETE FROM status WHERE status_id = 1"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    [Fact]
    public async Task Telemetry_unique_index_treats_null_sensor_as_duplicate()
    {
        if (Skip()) return;
        await using var db = fx.Create();
        var aircraft = NewAircraft("PG-T1");
        db.Aircraft.Add(aircraft);
        await db.SaveChangesAsync();
        var ts = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        db.TelemetryRecords.Add(new TelemetryRecord { AircraftId = aircraft.Id, RecordedAtUtc = ts, DataSource = "t", IngestedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.TelemetryRecords.Add(new TelemetryRecord { AircraftId = aircraft.Id, RecordedAtUtc = ts, DataSource = "t", IngestedAtUtc = DateTime.UtcNow });
        await Assert.ThrowsAsync<ConflictException>(() => new UnitOfWork(db).SaveChangesAsync(default));
    }

    [Fact]
    public async Task Only_one_active_model_per_type()
    {
        if (Skip()) return;
        await using var db = fx.Create();
        ModelVersion Model(string v) => new()
        {
            ModelName = "isolation_forest", Version = v, ModelType = ModelType.AnomalyDetection, Algorithm = "IsolationForest",
            Status = ModelStatus.Active, ArtifactPath = "p", ArtifactSha256 = new string('a', 64), CreatedAtUtc = DateTime.UtcNow,
        };
        db.ModelVersions.Add(Model("1"));
        await db.SaveChangesAsync();
        db.ModelVersions.Add(Model("2"));
        await Assert.ThrowsAsync<ConflictException>(() => new UnitOfWork(db).SaveChangesAsync(default));
    }

    [Fact]
    public async Task Repository_queries_translate_on_postgres()
    {
        if (Skip()) return;
        await using var db = fx.Create();
        var aircraftRepo = new AircraftRepository(db);
        var all = await aircraftRepo.ListAllAsync(default);
        await aircraftRepo.GetCountersAsync(all.Select(a => a.Id).ToList(), default);
        await new PredictionRepository(db).GetLatestByAircraftAsync(default);
        var telemetry = new TelemetryRepository(db);
        await telemetry.GetQualityTotalsAsync(null, null, null, default);
        await telemetry.GetQualityByAircraftAsync(null, null, null, default);
        await telemetry.GetImportTotalsAsync(default);
        await new AnomalyRepository(db).CountByInvestigationStatusAsync(default);
        var risk = await new PredictionRepository(db).ListRiskStatusesAsync(default);
        Assert.Equal(["Low", "Elevated", "High", "Undetermined", "Critical", "HighCritical"], risk.Select(r => r.Code));
    }
    /// <summary>
    /// Regression: enums persist as PascalCase STRINGS, so a `>=` severity predicate became an ALPHABETICAL
    /// SQL comparison - and 'Low'/'Medium' both sort after 'High', so every severity matched. The EF in-memory
    /// provider compares enums ordinally and therefore could not catch this; only real PostgreSQL can.
    /// A read-only dry run on live data exposed it: 12 Low + 11 Medium + 2 High were selected for a
    /// High-only threshold.
    /// </summary>
    [Fact]
    public async Task Anomaly_severity_threshold_is_not_an_alphabetical_string_comparison()
    {
        if (Skip()) return;
        await using var db = fx.Create();

        var aircraft = NewAircraft($"PG-SEV-{Guid.NewGuid():N}"[..12]);
        db.Aircraft.Add(aircraft);
        var model = new ModelVersion
        {
            ModelName = "iso", Version = Guid.NewGuid().ToString("N")[..10], ModelType = ModelType.AnomalyDetection,
            // Trained, not Active: the anomaly rows only need a model reference, and the database enforces
            // a single Active model per type, which another test in this shared fixture asserts.
            Algorithm = "IsolationForest", Status = ModelStatus.Trained, ArtifactPath = "x",
            ArtifactSha256 = new string('a', 64), CreatedAtUtc = DateTime.UtcNow,
        };
        db.ModelVersions.Add(model);
        foreach (var severity in new[] { AnomalySeverity.Low, AnomalySeverity.Medium, AnomalySeverity.High })
            db.AnomalyResults.Add(new AircraftAnomalyResult
            {
                AircraftId = aircraft.Id, ModelVersionId = model.Id, ObservedAtUtc = DateTime.UtcNow.AddHours(-1),
                AnomalyScore = 0.9, IsAnomaly = true, Severity = severity, Source = AnomalySource.ForecastModel,   // the eligible source
                InvestigationStatus = InvestigationStatus.New,
                ScoredAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var high = await new AnomalyRepository(db)
            .ListQualifyingWithoutWorkOrderAsync(AnomalySeverity.High, 50, default);

        var mine = high.Where(a => a.AircraftId == aircraft.Id).ToList();
        Assert.Single(mine);
        Assert.Equal(AnomalySeverity.High, mine[0].Severity);

        var medium = await new AnomalyRepository(db)
            .ListQualifyingWithoutWorkOrderAsync(AnomalySeverity.Medium, 50, default);
        Assert.Equal(2, medium.Count(a => a.AircraftId == aircraft.Id));
    }

    // ---- Phase 5: idempotency is enforced by PostgreSQL itself (the in-memory provider cannot prove this) ----

    private async Task<(Aircraft Aircraft, AppUser User, AircraftAnomalyResult Anomaly)> SeedAnomalyAsync(string tag)
    {
        await using var db = fx.Create();
        var aircraft = NewAircraft($"PG-{tag}-{Guid.NewGuid():N}"[..14]);
        var user = new AppUser
        {
            Username = $"u{Guid.NewGuid():N}"[..20], Email = $"{Guid.NewGuid():N}@x.test", DisplayName = "PG test",
            PasswordHash = "x", Status = UserStatus.Active, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        var anomaly = new AircraftAnomalyResult
        {
            Aircraft = aircraft, ObservedAtUtc = DateTime.UtcNow, ScoredAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
            IsAnomaly = true, Severity = AnomalySeverity.High, Source = AnomalySource.ThresholdRule,
            DedupKey = $"ThresholdRule:{Guid.NewGuid():N}:20261027T0000Z", RuleCodes = "EngineTemperatureHighC",
            Evidence = "{}", FeatureValues = "{}", InvestigationStatus = InvestigationStatus.New,
        };
        db.AddRange(aircraft, user, anomaly);
        await db.SaveChangesAsync();
        return (aircraft, user, anomaly);
    }

    [Fact]
    public async Task Concurrent_runs_cannot_create_two_work_orders_for_one_anomaly()
    {
        if (Skip()) return;
        var (aircraft, user, anomaly) = await SeedAnomalyAsync("WO");
        async Task<bool> TryCreate(string number)
        {
            await using var db = fx.Create();
            db.WorkOrders.Add(new MaintenanceWorkOrder
            {
                WorkOrderNumber = number, AircraftId = aircraft.Id, AnomalyId = anomaly.Id, Priority = WorkOrderPriority.High,
                Title = "t", Description = "d", Status = WorkOrderStatus.Open, Source = WorkOrderSource.Automatic,
                CreatedByUserId = user.Id, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
            });
            try { await db.SaveChangesAsync(); return true; }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) { return false; }
        }
        // Two genuinely concurrent inserts from separate connections, as two overlapping runs would do.
        var results = await Task.WhenAll(TryCreate($"WO-PG-{Guid.NewGuid():N}"[..20]), TryCreate($"WO-PG-{Guid.NewGuid():N}"[..20]));
        Assert.Equal(1, results.Count(ok => ok));
        await using var check = fx.Create();
        Assert.Equal(1, await check.WorkOrders.CountAsync(w => w.AnomalyId == anomaly.Id));
    }

    [Fact]
    public async Task The_same_logical_event_cannot_be_recorded_twice()
    {
        if (Skip()) return;
        var (aircraft, _, first) = await SeedAnomalyAsync("DK");
        await using var db = fx.Create();
        db.AnomalyResults.Add(new AircraftAnomalyResult
        {
            AircraftId = aircraft.Id, ObservedAtUtc = DateTime.UtcNow, ScoredAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
            IsAnomaly = true, Severity = AnomalySeverity.High, Source = AnomalySource.ThresholdRule, DedupKey = first.DedupKey,
            FeatureValues = "{}", InvestigationStatus = InvestigationStatus.New,
        });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, (ex.InnerException as PostgresException)?.SqlState);
    }

    [Fact]
    public async Task An_ml_insert_that_does_not_name_the_new_columns_still_records_an_isolation_forest_row()
    {
        if (Skip()) return;
        var (aircraft, _, _) = await SeedAnomalyAsync("ML");
        await using var db = fx.Create();
        var model = new ModelVersion
        {
            ModelName = "iso", Version = Guid.NewGuid().ToString("N")[..10], ModelType = ModelType.AnomalyDetection,
            Algorithm = "IsolationForest", Status = ModelStatus.Trained, ArtifactPath = "x", ArtifactSha256 = new string('a', 64),
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.ModelVersions.Add(model);
        await db.SaveChangesAsync();
        // Exactly the column list the Python scorer uses (no source / dedup_key / evidence).
        await PostgresFixture.ExecuteAsync(fx.ConnectionString!, $@"
            INSERT INTO aircraft_anomaly_result (aircraft_id, observed_at_utc, anomaly_score, is_anomaly, severity, model_version_id,
                scored_at_utc, feature_values, investigation_status, updated_at_utc)
            VALUES ('{aircraft.Id}', now(), 0.7, true, 'High', '{model.Id}', now(), '{{}}'::jsonb, 'New', now())");
        var row = await db.AnomalyResults.AsNoTracking().SingleAsync(a => a.ModelVersionId == model.Id);
        Assert.Equal(AnomalySource.IsolationForest, row.Source);
        Assert.Null(row.DedupKey);
    }
}
