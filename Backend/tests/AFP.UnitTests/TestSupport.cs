using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Application.Services;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Infrastructure.Persistence;
using AFP.Infrastructure.Persistence.Repositories;
using AFP.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AFP.UnitTests;

public sealed class FakeClock(DateTime utcNow) : IClock
{
    public DateTime UtcNow { get; set; } = utcNow;
    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}

public sealed class FakeCurrentUser : ICurrentUser
{
    public long? UserId { get; set; }
    public IReadOnlyList<UserRole> Roles { get; set; } = [];

    /// <summary>Acts as <paramref name="user"/> with its active roles.</summary>
    public void SignInAs(AppUser user)
    {
        UserId = user.Id;
        Roles = user.ActiveRoles;
    }
    public string? TokenId { get; set; }
    public DateTime? TokenExpiresAtUtc { get; set; }
    public string? IpAddress { get; set; } = "127.0.0.1";
    public string? UserAgent { get; set; } = "unit-test";
    public string? CorrelationId { get; set; } = "test";
}

public sealed class CapturingEmailSender : IEmailSender
{
    public List<EmailMessage> Sent { get; } = [];
    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        Sent.Add(message);
        return Task.CompletedTask;
    }
}

/// <summary>Real repositories and services over the EF Core in-memory provider.</summary>
public sealed class TestContext : IDisposable
{
    public static readonly DateTime Now = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

    public TestContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        Db = new AppDbContext(options);
        // Reference roles exactly as seeded on PostgreSQL (role.csv mirrors the UserRole enum).
        foreach (var role in Enum.GetValues<UserRole>()) Db.Roles.Add(new Role { RoleId = role, RoleName = role.ToString() });
        // Approved maintenance action codes exactly as seeded on PostgreSQL (maintenance_action_code.csv).
        Db.MaintenanceActionCodes.Add(new MaintenanceActionCode { Code = "ENG_INSP", Description = "Engine Inspection", DisplayOrder = 1 });
        Db.MaintenanceActionCodes.Add(new MaintenanceActionCode { Code = "OIL_REPL", Description = "Oil Filter Replacement", DisplayOrder = 2 });
        Db.SaveChanges();
        Users = new UserRepository(Db);
        Audit = new AuditRepository(Db);
        Aircraft = new AircraftRepository(Db);
        Maintenance = new MaintenanceRepository(Db);
        Telemetry = new TelemetryRepository(Db);
        Anomalies = new AnomalyRepository(Db);
        Predictions = new PredictionRepository(Db);
        Models = new ModelVersionRepository(Db);
        Runs = new PipelineRunRepository(Db);
        UnitOfWork = new UnitOfWork(Db);
        Auditor = new ActionAuditor(Audit, CurrentUser, Clock);
    }

    public AppDbContext Db { get; }
    public FakeClock Clock { get; } = new(Now);
    public FakeCurrentUser CurrentUser { get; } = new();
    public CapturingEmailSender Email { get; } = new();
    public IdentityPasswordHasher Hasher { get; } = new();
    public UserRepository Users { get; }
    public AuditRepository Audit { get; }
    public AircraftRepository Aircraft { get; }
    public MaintenanceRepository Maintenance { get; }
    public TelemetryRepository Telemetry { get; }
    public AnomalyRepository Anomalies { get; }
    public PredictionRepository Predictions { get; }
    public ModelVersionRepository Models { get; }
    public PipelineRunRepository Runs { get; }
    public UnitOfWork UnitOfWork { get; }
    public ActionAuditor Auditor { get; }

    public IOptions<AuthOptions> AuthOptions { get; } = Options.Create(new AuthOptions());
    public IOptions<PredictionOptions> PredictionOptions { get; } = Options.Create(new PredictionOptions());

    public JwtTokenService Tokens => new(Options.Create(new JwtOptions { SigningKey = new string('k', 48) }), Clock);

    public AuthService AuthService() =>
        new(Users, Audit, UnitOfWork, Hasher, Tokens, Email, CurrentUser, Clock, AuthOptions, NullLogger<AuthService>.Instance);

    public FailurePredictionStatusProvider FailureStatus() => new(Runs, Models, PredictionOptions);

    public WorkOrderService WorkOrderService() =>
        new(Maintenance, Aircraft, Anomalies, Predictions, Users, Audit, Auditor, UnitOfWork, CurrentUser, Clock, Diagnosis());

    public WorkOrderDiagnosisService Diagnosis() => new(Aircraft, Predictions, Maintenance, HealthEvaluator(), Clock);

    public ModelService ModelService() => new(Models, FailureStatus(), Auditor, UnitOfWork, CurrentUser, Clock);

    public AnomalyWorkOrderService AnomalyWorkOrderService(AnomalyWorkOrderOptions? options = null) =>
        new(Anomalies, Maintenance, Auditor, UnitOfWork, CurrentUser, Clock,
            Microsoft.Extensions.Options.Options.Create(options ?? new AnomalyWorkOrderOptions()), Aircraft, Diagnosis());

    public AircraftService AircraftService() =>
        new(Aircraft, Maintenance, Anomalies, Predictions, Auditor, UnitOfWork, CurrentUser, Clock, HealthEvaluator());

    public FleetHealthEvaluator HealthEvaluator() => new(Aircraft, Telemetry, Clock);

    public async Task<AppUser> AddUserAsync(UserRole? role, UserStatus status = UserStatus.Active, string password = "Correct-Horse-9!")
    {
        var name = Guid.NewGuid().ToString("N")[..12];
        var user = new AppUser
        {
            Username = $"user.{name}", Email = $"{name}@example.test", DisplayName = $"{role} user", Status = status,
            CreatedAtUtc = Now, UpdatedAtUtc = Now,
        };
        if (role is { } r) user.UserRoles.Add(new UserRoleAssignment { RoleId = r, AssignedAtUtc = Now });
        user.PasswordHash = Hasher.Hash(user, password);
        Db.Users.Add(user);
        await Db.SaveChangesAsync();
        return user;
    }

    public async Task<Aircraft> AddAircraftAsync(string registration = "DEMO-A01", OperationalStatus status = OperationalStatus.Available)
    {
        var aircraft = new Aircraft
        {
            RegistrationNumber = registration, Model = "A320neo", Manufacturer = "Airbus", FleetGroup = "Narrowbody East",
            OperationalStatus = status, StatusChangedAtUtc = Now, CreatedAtUtc = Now, UpdatedAtUtc = Now,
        };
        Db.Aircraft.Add(aircraft);
        await Db.SaveChangesAsync();
        return aircraft;
    }

    public void Dispose() => Db.Dispose();
}
