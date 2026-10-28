using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Domain.Rules;
using AFP.Infrastructure.Configuration;
using AFP.Infrastructure.Persistence;
using AFP.SeedSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AFP.UnitTests;

public class OperationalCriticalityRuleTests
{
    private static readonly ApprovedLimit Temp = new(null, 100);
    private static readonly ApprovedLimit Pressure = new(150, 300);

    [Theory]
    [InlineData(90, 200, OperationalCriticality.Normal)]
    [InlineData(100, 300, OperationalCriticality.Normal)]          // equality is within limits
    [InlineData(100.1, 200, OperationalCriticality.Critical)]
    [InlineData(90, 149.9, OperationalCriticality.Critical)]
    [InlineData(120, 400, OperationalCriticality.HighCritical)]
    public void Classifies_by_number_of_breaches(double t, double p, OperationalCriticality expected) =>
        Assert.Equal(expected, OperationalCriticalityRule.Classify(t, Temp, p, Pressure));

    [Fact]
    public void Missing_data_or_missing_limits_are_never_normal()
    {
        Assert.Equal(OperationalCriticality.Unclassified, OperationalCriticalityRule.Classify(null, Temp, 200, Pressure));
        Assert.Equal(OperationalCriticality.Unclassified, OperationalCriticalityRule.Classify(90, Temp, double.NaN, Pressure));
        Assert.Equal(OperationalCriticality.Unclassified, OperationalCriticalityRule.Classify(90, null, 200, Pressure));
    }
}

public class DatabaseTargetTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] values)
    {
        var d = values.ToDictionary(v => v.Key, v => v.Value);
        return k => d.GetValueOrDefault(k);
    }

    [Fact]
    public void Composes_api_connection_from_db_keys_and_defaults_to_predict_db()
    {
        var get = Env(("DB_APP_USER", "afp_predict_api"), ("API_USER_PASSWORD", "s3cret"), ("DB_PORT", "5433"));
        var cs = DatabaseTarget.ApiConnectionString(get)!;
        Assert.Equal("aircraft_predict_db", DatabaseTarget.Validate(cs, get, "test"));
        Assert.Contains("Port=5433", cs);
        Assert.Equal("afp_predict_api@localhost:5433/aircraft_predict_db", DatabaseTarget.Describe(cs));
        Assert.DoesNotContain("s3cret", DatabaseTarget.Describe(cs));
    }

    [Fact]
    public void Missing_credentials_yield_no_connection() =>
        Assert.Null(DatabaseTarget.ApiConnectionString(Env(("DB_APP_USER", "x"))));

    [Theory]
    [InlineData("Host=localhost;Database=aircraft_app;Username=u", "aircraft_predict_db")]
    [InlineData("Host=localhost;Database=AIRCRAFT_APP;Username=u", "aircraft_predict_db")]
    [InlineData("Host=localhost;Database=aircraft_app;Username=u", "aircraft_app")]
    [InlineData("Host=localhost;Database=other_db;Username=u", "aircraft_predict_db")]
    [InlineData("Host=localhost;Username=u", "aircraft_predict_db")]
    public void Guard_rejects_protected_mismatched_or_missing_database(string cs, string dbName) =>
        Assert.Throws<InvalidOperationException>(() => DatabaseTarget.Validate(cs, Env(("DB_NAME", dbName)), "test"));

    [Fact]
    public void Additional_forbidden_names_are_configurable() =>
        Assert.Throws<InvalidOperationException>(() => DatabaseTarget.Validate(
            "Host=h;Database=legacy_db;Username=u", Env(("DB_NAME", "legacy_db"), ("DB_FORBIDDEN_NAMES", "legacy_db")), "test"));

    [Fact]
    public void DotEnv_parses_comments_quotes_and_export()
    {
        var parsed = DotEnv.Parse([
            "# comment", "", "DB_NAME=aircraft_predict_db", "export DB_PORT=5432", "PW=\"a b#c\"", "X='q'", "Y=1 # trailing", "bad line",
        ]);
        Assert.Equal("aircraft_predict_db", parsed["DB_NAME"]);
        Assert.Equal("5432", parsed["DB_PORT"]);
        Assert.Equal("a b#c", parsed["PW"]);
        Assert.Equal("q", parsed["X"]);
        Assert.Equal("1", parsed["Y"]);
        Assert.False(parsed.ContainsKey("bad line"));
    }

    [Fact]
    public void DotEnv_never_overrides_existing_environment_variables()
    {
        var dir = Directory.CreateTempSubdirectory("afp-env");
        var key = "AFP_TEST_" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllLines(Path.Combine(dir.FullName, ".env"), [$"{key}=from-file", $"{key}_2=from-file"]);
            Environment.SetEnvironmentVariable(key, "from-environment");
            var nested = Directory.CreateDirectory(Path.Combine(dir.FullName, "a", "b"));
            Assert.Equal(Path.Combine(dir.FullName, ".env"), DotEnv.LoadFromAncestors(nested.FullName));
            Assert.Equal("from-environment", Environment.GetEnvironmentVariable(key));
            Assert.Equal("from-file", Environment.GetEnvironmentVariable(key + "_2"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
            Environment.SetEnvironmentVariable(key + "_2", null);
            DeleteWithRetry(dir);
        }
    }

    /// <summary>
    /// Windows antivirus/indexing briefly locks a just-written file, so deleting the temp folder could throw and fail
    /// a test whose assertions had passed (seen intermittently 2026-10-27). Cleanup retries briefly and never fails the test.
    /// </summary>
    private static void DeleteWithRetry(DirectoryInfo dir)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { dir.Delete(true); return; }
            catch (IOException) { Thread.Sleep(50 * (attempt + 1)); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50 * (attempt + 1)); }
        }
    }
}

public class SeedCompilerTests
{
    private static string RepoSeedRoot() => SeedPaths.FindSeedRoot(AppContext.BaseDirectory)!;

    private static string TempSeed(string datasetsJson, params (string File, string Content)[] files)
    {
        var dir = Directory.CreateTempSubdirectory("afp-seed").FullName;
        File.WriteAllText(Path.Combine(dir, "datasets.json"), datasetsJson);
        foreach (var (file, content) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, file))!);
            File.WriteAllText(Path.Combine(dir, file), content);
        }
        return dir;
    }

    private const string Manifest = """
        {"targetDatabase":"aircraft_predict_db","output":"seed.sql","datasets":[{"file":"csv/t.csv","table":"t","key":["id"],"unique":[["name"]],
         "columns":[{"name":"id","type":"int","required":true,"min":0},{"name":"name","type":"text","required":true,"maxLength":10},{"name":"note","type":"text"}]}]}
        """;

    [Fact]
    public void Committed_seed_sql_matches_csv_sources()
    {
        var root = RepoSeedRoot();
        var manifest = SeedCompiler.LoadManifest(root);
        var (sql, errors) = SeedCompiler.Compile(root, manifest);
        Assert.Empty(errors);
        Assert.Equal(sql, File.ReadAllText(Path.Combine(root, manifest.Output)));
    }

    [Fact]
    public void Role_enum_matches_role_csv()
    {
        var rows = SeedCompiler.ReadRecords(RepoSeedRoot(), "role");
        Assert.Equal(Enum.GetValues<UserRole>().Select(v => ((int)v).ToString()), rows.Select(r => r["role_id"]));
        Assert.Equal(Enum.GetNames<UserRole>(), rows.Select(r => r["role_name"]));
    }

    [Fact]
    public void Lifecycle_enum_matches_status_csv()
    {
        var rows = SeedCompiler.ReadRecords(RepoSeedRoot(), "status");
        Assert.Equal(Enum.GetValues<LifecycleStatus>().Select(v => ((int)v).ToString()), rows.Select(r => r["status_id"]));
        Assert.Equal(Enum.GetNames<LifecycleStatus>(), rows.Select(r => r["name"]));
    }

    [Fact]
    public void Generated_sql_is_deterministic_escaped_guarded_and_never_deletes()
    {
        var root = TempSeed(Manifest, ("csv/t.csv", "id,name,note\n2,b,\n1,O'Hare,x\n"));
        var m = SeedCompiler.LoadManifest(root);
        var first = SeedCompiler.Compile(root, m).Sql!;
        Assert.Equal(first, SeedCompiler.Compile(root, m).Sql);
        Assert.Contains("(1, 'O''Hare', 'x'),\n    (2, 'b', NULL)", first);
        Assert.Contains("ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name, note = EXCLUDED.note;", first);
        Assert.Contains("current_database() <> 'aircraft_predict_db'", first);
        Assert.DoesNotContain("\r", first);
        Assert.DoesNotContain("DELETE FROM", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TRUNCATE", first, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("-- GENERATED", first);
        Assert.Contains("BEGIN;", first);
        Assert.EndsWith("COMMIT;\n", first);
    }

    [Fact]
    public void Validation_reports_file_line_and_column()
    {
        var root = TempSeed(Manifest, ("csv/t.csv", "id,name,note\n1,a,\n1,b,\nx,c,\n3,,\n4,abcdefghijkl,\n5,a,\n6,d\n"));
        var (sql, errors) = SeedCompiler.Compile(root, SeedCompiler.LoadManifest(root));
        Assert.Null(sql);
        var text = errors.Select(e => e.ToString()).ToList();
        Assert.Contains(text, e => e.StartsWith("csv/t.csv:3 [id]") && e.Contains("duplicate key"));
        Assert.Contains(text, e => e.StartsWith("csv/t.csv:4 [id]") && e.Contains("not an integer"));
        Assert.Contains(text, e => e.StartsWith("csv/t.csv:5 [name]") && e.Contains("required"));
        Assert.Contains(text, e => e.StartsWith("csv/t.csv:6 [name]") && e.Contains("longer than 10"));
        Assert.Contains(text, e => e.StartsWith("csv/t.csv:7 [name]") && e.Contains("duplicate unique"));
        Assert.Contains(text, e => e.StartsWith("csv/t.csv:8:") && e.Contains("expected 3 fields"));
    }

    [Fact]
    public void Wrong_header_is_rejected()
    {
        var root = TempSeed(Manifest, ("csv/t.csv", "id,label,note\n1,a,\n"));
        var (_, errors) = SeedCompiler.Compile(root, SeedCompiler.LoadManifest(root));
        Assert.Contains(errors, e => e.Line == 1 && e.Message.Contains("header must be exactly"));
    }

    [Fact]
    public void Protected_target_database_is_refused()
    {
        var root = TempSeed(Manifest.Replace("aircraft_predict_db", "aircraft_app"));
        Assert.Throws<InvalidDataException>(() => SeedCompiler.LoadManifest(root));
    }
}

public class EfModelTests
{
    private static AppDbContext NpgsqlContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql("Host=localhost;Database=aircraft_predict_db;Username=model_check").UseSnakeCaseNamingConvention().Options);

    [Fact]
    public void Migrations_are_in_sync_with_the_model()
    {
        using var db = NpgsqlContext();
        Assert.False(db.Database.HasPendingModelChanges(), "Run dotnet ef migrations add: the model has changes not captured in a migration.");
    }

    [Fact]
    public void Status_table_has_integer_key_and_unique_name()
    {
        using var db = NpgsqlContext();
        var status = db.Model.FindEntityType(typeof(Status))!;
        Assert.Equal("status", status.GetTableName());
        var key = status.FindPrimaryKey()!.Properties.Single();
        Assert.Equal("status_id", key.GetColumnName());
        Assert.Equal("integer", key.GetColumnType());
        Assert.Equal(ValueGenerated.Never, key.ValueGenerated);
        Assert.Contains(status.GetIndexes(), i => i.IsUnique && i.Properties.Single().Name == nameof(Status.Name));
    }

    [Fact]
    public void Every_table_except_status_has_a_restricting_status_fk_defaulting_to_active()
    {
        using var db = NpgsqlContext();
        var tables = db.Model.GetEntityTypes().Where(t => t.ClrType != typeof(Status)).ToList();
        Assert.Equal(25, tables.Count);   // + maintenance_action_code (2026-10-27), + sensor_forecast (2026-10-28)
        foreach (var t in tables)
        {
            var fk = t.GetForeignKeys().SingleOrDefault(f => f.PrincipalEntityType.ClrType == typeof(Status));
            Assert.True(fk is not null, $"{t.GetTableName()} has no status FK");
            Assert.Equal(DeleteBehavior.Restrict, fk!.DeleteBehavior);
            var column = fk.Properties.Single();
            Assert.Equal("status_id", column.GetColumnName());
            Assert.Equal("integer", column.GetColumnType());
            Assert.False(column.IsNullable);
            Assert.Equal(LifecycleStatus.Active, column.GetDefaultValue());
        }
    }

    [Fact]
    public void Prediction_has_state_cutoff_and_idempotency_index()
    {
        using var db = NpgsqlContext();
        var p = db.Model.FindEntityType(typeof(AircraftRiskPrediction))!;
        Assert.Equal("prediction_state", p.FindProperty(nameof(AircraftRiskPrediction.PredictionState))!.GetColumnName());
        Assert.True(p.GetIndexes().Single(i => i.GetDatabaseName() == "ux_aircraft_risk_prediction_idempotency").IsUnique);
        var ddl = db.Database.GenerateCreateScript();
        Assert.Contains("CREATE UNIQUE INDEX ux_aircraft_risk_prediction_idempotency ON aircraft_risk_prediction " +
                        "(aircraft_id, basis, model_version_id, feature_cutoff_utc, horizon_days) NULLS NOT DISTINCT;", ddl);
        Assert.Equal(25, System.Text.RegularExpressions.Regex.Count(ddl, @"REFERENCES status \(status_id\) ON DELETE RESTRICT"));
    }
}
