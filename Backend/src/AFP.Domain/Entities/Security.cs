using AFP.Domain.Enums;

namespace AFP.Domain.Entities;

/// <summary>Application account. Roles are granted only through <see cref="UserRoleAssignment"/> (table user_role).</summary>
public class AppUser : ILifecycleEntity
{
    public LifecycleStatus StatusId { get; set; } = LifecycleStatus.Active;
    public long Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserStatus Status { get; set; } = UserStatus.Pending;
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutEndUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public long? ApprovedByUserId { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
    public string? DisabledReason { get; set; }
    public List<UserRoleAssignment> UserRoles { get; set; } = [];

    /// <summary>Currently granted roles (assignments whose lifecycle status is Active).</summary>
    public IReadOnlyList<UserRole> ActiveRoles =>
        UserRoles.Where(r => r.StatusId == LifecycleStatus.Active).Select(r => r.RoleId).Distinct().OrderBy(r => r).ToList();

    public bool HasRole(UserRole role) => ActiveRoles.Contains(role);

    public bool IsLockedOut(DateTime nowUtc) => LockoutEndUtc.HasValue && LockoutEndUtc.Value > nowUtc;

    /// <summary>Invalidates every outstanding access token issued to this user.</summary>
    public void RotateSecurityStamp() => SecurityStamp = Guid.NewGuid().ToString("N");
}

/// <summary>Seeded reference row (table <c>role</c>, tools/db/seed/csv/role.csv). IDs mirror <see cref="UserRole"/>.</summary>
public class Role : ILifecycleEntity
{
    public LifecycleStatus StatusId { get; set; } = LifecycleStatus.Active;
    public UserRole RoleId { get; set; }
    public string RoleName { get; set; } = string.Empty;
}

/// <summary>
/// User–role transaction row (table <c>user_role</c>). Revoking a role sets its lifecycle status to Inactive,
/// so the assignment history is retained.
/// </summary>
public class UserRoleAssignment : ILifecycleEntity
{
    public LifecycleStatus StatusId { get; set; } = LifecycleStatus.Active;
    public long UserRoleId { get; set; }
    public long UserId { get; set; }
    public AppUser? User { get; set; }
    public UserRole RoleId { get; set; }
    public Role? Role { get; set; }
    public DateTime AssignedAtUtc { get; set; }
    public long? AssignedByUserId { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public long? RevokedByUserId { get; set; }
}

/// <summary>OAuth 2.0 refresh token (stored as a SHA-256 hash). Rotated on every use; reuse revokes the family.</summary>
public class RefreshToken : ILifecycleEntity
{
    public LifecycleStatus StatusId { get; set; } = LifecycleStatus.Active;
    public long Id { get; set; }
    public long UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public Guid FamilyId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? RevokedReason { get; set; }
    public string? CreatedByIp { get; set; }

    public bool IsUsable(DateTime nowUtc) => ConsumedAtUtc is null && RevokedAtUtc is null && ExpiresAtUtc > nowUtc;
}

public class PasswordResetToken : ILifecycleEntity
{
    public LifecycleStatus StatusId { get; set; } = LifecycleStatus.Active;
    public Guid Id { get; set; } = Guid.NewGuid();
    public long UserId { get; set; }
    public AppUser? User { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? UsedAtUtc { get; set; }
    public string? RequestedIp { get; set; }

    public bool IsUsable(DateTime nowUtc) => UsedAtUtc is null && ExpiresAtUtc > nowUtc;
}

public class RevokedToken : ILifecycleEntity
{
    public LifecycleStatus StatusId { get; set; } = LifecycleStatus.Active;
    public string Jti { get; set; } = string.Empty;
    public long UserId { get; set; }
    public DateTime RevokedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

public class AuthAudit : ILifecycleEntity
{
    public LifecycleStatus StatusId { get; set; } = LifecycleStatus.Active;
    public long Id { get; set; }
    public long? UserId { get; set; }
    public string? Email { get; set; }
    public string EventType { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? Detail { get; set; }
    public DateTime OccurredAtUtc { get; set; }
}

public class ActionAudit : ILifecycleEntity
{
    public LifecycleStatus StatusId { get; set; } = LifecycleStatus.Active;
    public long Id { get; set; }
    public long? UserId { get; set; }
    public AppUser? User { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string? Details { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string? CorrelationId { get; set; }
}
