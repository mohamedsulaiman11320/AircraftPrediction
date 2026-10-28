using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AFP.Application.Abstractions;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace AFP.IntegrationTests;

/// <summary>
/// Hosts the real API pipeline (auth, policies, rate limiting, ProblemDetails, background import worker) over the
/// EF Core in-memory provider, so these tests run without PostgreSQL or Docker.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string Password = "Correct-Horse-9!";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "afp-it", Guid.NewGuid().ToString("N"));

    protected virtual int AuthPermitLimit => 10_000;

    static ApiFactory() => Environment.SetEnvironmentVariable("AFP_DISABLE_DOTENV", "1");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:AppDb", "Host=unused-in-tests;Database=aircraft_predict_db");
        builder.UseSetting("Jwt:SigningKey", "integration-test-signing-key-that-is-long-enough-123456");
        builder.UseSetting("RateLimiting:AuthPermitLimit", AuthPermitLimit.ToString());
        builder.UseSetting("RateLimiting:GlobalPermitLimit", "100000");
        builder.UseSetting("TelemetryImport:StorageDirectory", Path.Combine(_dataDir, "imports"));
        builder.UseSetting("Email:PickupDirectory", Path.Combine(_dataDir, "mail"));
        builder.UseSetting("MINIO_ACCESS_KEY", "test-uploader");
        builder.UseSetting("MINIO_SECRET_KEY", "test-secret");

        builder.ConfigureTestServices(services =>
        {
            foreach (var d in services.Where(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>)
                                                  || d.ServiceType == typeof(IDbContextOptionsConfiguration<AppDbContext>)).ToList())
                services.Remove(d);
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            // MinIO is replaced by an in-memory store so tests never need Docker.
            foreach (var d in services.Where(d => d.ServiceType == typeof(IRawTelemetryStore)).ToList()) services.Remove(d);
            services.AddSingleton<IRawTelemetryStore>(RawStore);
        });
    }

    public string MailDirectory => Path.Combine(_dataDir, "mail");

    public InMemoryRawStore RawStore { get; } = new();

    protected override Microsoft.Extensions.Hosting.IHost CreateHost(Microsoft.Extensions.Hosting.IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        // Loads reference data from the same CSV seed used for PostgreSQL (single source of truth).
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
        var seedRoot = AFP.SeedSql.SeedPaths.FindSeedRoot(AppContext.BaseDirectory)!;
        foreach (var r in AFP.SeedSql.SeedCompiler.ReadRecords(seedRoot, "status"))
            db.Statuses.Add(new Status { StatusId = (LifecycleStatus)int.Parse(r["status_id"]), Name = r["name"], Description = r["description"] });
        foreach (var r in AFP.SeedSql.SeedCompiler.ReadRecords(seedRoot, "role"))
            db.Roles.Add(new Role { RoleId = (UserRole)int.Parse(r["role_id"]), RoleName = r["role_name"] });
        foreach (var r in AFP.SeedSql.SeedCompiler.ReadRecords(seedRoot, "maintenance_action_code"))
            db.MaintenanceActionCodes.Add(new MaintenanceActionCode { Code = r["code"], Description = r["description"], DisplayOrder = int.Parse(r["display_order"]) });
        foreach (var r in AFP.SeedSql.SeedCompiler.ReadRecords(seedRoot, "risk_status"))
            db.RiskStatuses.Add(new RiskStatus { Code = r["code"], Description = r["description"], DisplayOrder = int.Parse(r["display_order"]) });
        foreach (var r in AFP.SeedSql.SeedCompiler.ReadRecords(seedRoot, "health_threshold"))
            db.HealthThresholds.Add(new HealthThreshold
            {
                Code = r["code"], RuleReference = r["rule_reference"], Measurement = r["measurement"],
                Comparison = r["comparison"], Unit = Null(r["unit"]), Tier = Null(r["tier"]),
                ThresholdValue = string.IsNullOrWhiteSpace(r["threshold_value"])
                    ? null
                    : double.Parse(r["threshold_value"], System.Globalization.CultureInfo.InvariantCulture),
                RecommendedInspection = Null(r["recommended_inspection"]), SourceReference = r["source_reference"],
                IsValidated = bool.Parse(r["is_validated"]), Description = r["description"],
                DisplayOrder = int.Parse(r["display_order"]),
            });
        db.SaveChanges();
        return host;
    }

    /// <summary>Empty CSV cells are absent values, not empty strings.</summary>
    private static string? Null(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>A model version in a known lifecycle state, for governance tests.</summary>
    public async Task<ModelVersion> CreateModelVersionAsync(ModelType modelType, ModelStatus status)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var model = new ModelVersion
        {
            ModelName = $"it-{modelType}".ToLowerInvariant(),
            Version = DateTime.UtcNow.Ticks.ToString(),
            ModelType = modelType,
            Algorithm = "test",
            Status = status,
            ArtifactPath = "/tmp/it-model",
            ArtifactSha256 = new string('a', 64),
            CreatedAtUtc = DateTime.UtcNow,
            RegisteredAtUtc = DateTime.UtcNow,
        };
        db.ModelVersions.Add(model);
        await db.SaveChangesAsync();
        return model;
    }

    public async Task<AppUser> CreateUserAsync(UserRole? role, UserStatus status = UserStatus.Active)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var name = $"{(role?.ToString() ?? "user").ToLowerInvariant()}.{Guid.NewGuid():N}"[..40];
        var user = new AppUser
        {
            Username = name, Email = $"{name}@example.test",
            DisplayName = $"{role} Test", Status = status,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        if (role is { } r) user.UserRoles.Add(new UserRoleAssignment { RoleId = r, AssignedAtUtc = DateTime.UtcNow });
        user.PasswordHash = hasher.Hash(user, Password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public async Task<Aircraft> CreateAircraftAsync(string registration, string fleetGroup = "Narrowbody East")
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var aircraft = new Aircraft
        {
            RegistrationNumber = registration, Model = "A320neo", Manufacturer = "Airbus", FleetGroup = fleetGroup,
            OperationalStatus = OperationalStatus.Available, StatusChangedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        db.Aircraft.Add(aircraft);
        await db.SaveChangesAsync();
        return aircraft;
    }

    public async Task<string> LoginAsync(string username)
    {
        var response = await CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { username, password = Password });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("accessToken").GetString()!;
    }

    public async Task<HttpClient> ClientAsAsync(UserRole role) => (await ClientAndUserAsync(role)).Client;

    public async Task<(HttpClient Client, AppUser User)> ClientAndUserAsync(UserRole role)
    {
        var user = await CreateUserAsync(role);
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(user.Username));
        return (client, user);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_dataDir))
        {
            foreach (var f in Directory.GetFiles(_dataDir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            try { Directory.Delete(_dataDir, true); } catch (IOException) { }
        }
    }
}

public sealed class InMemoryRawStore : IRawTelemetryStore
{
    public System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Objects { get; } = new();

    public async Task<string> PutAsync(string key, Stream content, long length, string contentType, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var location = $"s3://bronze/telemetry/{key}";
        Objects[location] = ms.ToArray();
        return location;
    }

    /// <summary>Set by a test to simulate object storage refusing deletes.</summary>
    public bool FailDeletes { get; set; }

    /// <summary>Idempotent, like the real store: removing a missing object is not an error.</summary>
    public Task DeleteAsync(string location, CancellationToken ct)
    {
        if (FailDeletes) throw new InvalidOperationException("storage unavailable (simulated)");
        Objects.TryRemove(location, out _);
        return Task.CompletedTask;
    }

    public Task<int> DeletePrefixAsync(string bucket, string prefix, CancellationToken ct)
    {
        if (FailDeletes) throw new InvalidOperationException("storage unavailable (simulated)");
        var match = $"s3://{bucket}/{prefix}";
        var keys = Objects.Keys.Where(k => k.StartsWith(match, StringComparison.Ordinal)).ToList();
        foreach (var key in keys) Objects.TryRemove(key, out _);
        return Task.FromResult(keys.Count);
    }

    public Task<string> CopyAsync(string sourceLocation, string destinationKey, CancellationToken ct)
    {
        var location = $"s3://bronze/telemetry/{destinationKey}";
        Objects[location] = Objects[sourceLocation];
        return Task.FromResult(location);
    }
}

public sealed class LowRateLimitApiFactory : ApiFactory
{
    protected override int AuthPermitLimit => 3;
}
