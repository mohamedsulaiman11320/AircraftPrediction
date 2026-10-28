using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Domain.Entities;
using AFP.Domain.Enums;

namespace AFP.Application.Services;

/// <summary>Server-side check run for every bearer token after signature validation.</summary>
public interface ISessionValidator
{
    Task<bool> IsValidAsync(long userId, string? jti, string? securityStamp, CancellationToken ct);
}

public sealed class SessionValidator(IUserRepository users) : ISessionValidator
{
    public async Task<bool> IsValidAsync(long userId, string? jti, string? securityStamp, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(jti) || string.IsNullOrEmpty(securityStamp)) return false;
        var user = await users.GetByIdAsync(userId, ct);
        if (user is null || user.Status != UserStatus.Active || user.SecurityStamp != securityStamp || user.ActiveRoles.Count == 0) return false;
        return !await users.IsTokenRevokedAsync(jti, ct);
    }
}

public interface IAdminBootstrapService
{
    Task<long> CreateFirstDispatcherAsync(string username, string email, string displayName, string password, CancellationToken ct);

    /// <summary>
    /// Creates the first active account of an administrative role: FlightDispatcher, or DataEngineer (who approves
    /// every other account). Refused when an active account with that role already exists.
    /// </summary>
    Task<long> CreateFirstAccountAsync(UserRole role, string username, string email, string displayName, string password, CancellationToken ct);
}

/// <summary>
/// Creates the first Flight Dispatcher (the administrator role) from a one-off command-line invocation.
/// Refuses to run once any active dispatcher exists, so it cannot be used to escalate privileges later.
/// </summary>
public sealed class AdminBootstrapService(
    IUserRepository users, IAuditRepository audit, IUnitOfWork unitOfWork, IPasswordHasher hasher, IClock clock) : IAdminBootstrapService
{
    /// <summary>Roles that can be bootstrapped; everyone else registers and is approved by a Data Engineer.</summary>
    public static readonly UserRole[] BootstrapRoles = [UserRole.FlightDispatcher, UserRole.DataEngineer];

    public Task<long> CreateFirstDispatcherAsync(string username, string email, string displayName, string password, CancellationToken ct) =>
        CreateFirstAccountAsync(UserRole.FlightDispatcher, username, email, displayName, password, ct);

    public async Task<long> CreateFirstAccountAsync(UserRole role, string username, string email, string displayName, string password, CancellationToken ct)
    {
        if (!BootstrapRoles.Contains(role))
            throw new ValidationException("role", $"Only {string.Join(" or ", BootstrapRoles)} can be bootstrapped; other accounts register and are approved.");
        var v = new Validator()
            .Required("username", username, "Username")
            .Email("email", email)
            .Required("displayName", displayName, "Display name")
            .Password("password", password, password, "password");
        if (!string.IsNullOrWhiteSpace(username) && !SecureTokens.IsValidUsername(username))
            v.Add("username", "Username must be 3–64 characters: letters, digits, '.', '_' or '-'.");
        v.ThrowIfInvalid();

        if (await users.AnyActiveUserWithRoleAsync(role, ct))
            throw new ConflictException(role == UserRole.FlightDispatcher
                ? "A flight dispatcher already exists. Use the administration screens to manage users."
                : $"An active {role} already exists. Use the administration screens to manage users.");

        var normalizedUsername = SecureTokens.NormalizeUsername(username);
        var normalizedEmail = SecureTokens.NormalizeEmail(email);
        if (await users.GetByUsernameAsync(normalizedUsername, ct) is not null || await users.GetByEmailAsync(normalizedEmail, ct) is not null)
            throw new ConflictException("An account with this username or email already exists.");

        var now = clock.UtcNow;
        var user = new AppUser
        {
            Username = normalizedUsername,
            Email = normalizedEmail,
            DisplayName = displayName.Trim(),
            Status = UserStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ApprovedAtUtc = now,
        };
        user.PasswordHash = hasher.Hash(user, password);
        user.UserRoles.Add(new UserRoleAssignment { RoleId = role, AssignedAtUtc = now });
        await users.AddAsync(user, ct);
        await unitOfWork.SaveChangesAsync(ct);
        await audit.AddAuthAsync(new AuthAudit
        {
            EventType = AuthEvents.AdminBootstrap, UserId = user.Id, Email = normalizedEmail, Succeeded = true,
            Detail = role == UserRole.FlightDispatcher
                ? "First flight dispatcher created via bootstrap command."
                : $"First {role} created via bootstrap command.", OccurredAtUtc = now,
        }, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return user.Id;
    }
}

public sealed class ActionAuditor(IAuditRepository audit, ICurrentUser currentUser, IClock clock) : IActionAuditor
{
    public Task RecordAsync(string action, string entityType, string entityId, object? details, CancellationToken ct) =>
        audit.AddActionAsync(new ActionAudit
        {
            UserId = currentUser.UserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Details = details is null ? null : Json.Serialize(details),
            OccurredAtUtc = clock.UtcNow,
            CorrelationId = currentUser.CorrelationId,
        }, ct);
}
