using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Application.Contracts;
using AFP.Application.Mapping;
using AFP.Domain.Entities;
using AFP.Domain.Enums;

namespace AFP.Application.Services;

public interface IUserAdminService
{
    Task<PagedResult<AdminUserDto>> ListAsync(UserFilter filter, PageRequest page, CancellationToken ct);
    Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken ct);
    Task<AdminUserDto> ApproveAsync(long id, ApproveUserRequest request, CancellationToken ct);
    Task<AdminUserDto> AssignRolesAsync(long id, AssignRolesRequest request, CancellationToken ct);
    Task<AdminUserDto> DisableAsync(long id, DisableUserRequest request, CancellationToken ct);
    Task<AdminUserDto> EnableAsync(long id, CancellationToken ct);
    Task<IReadOnlyList<TechnicianDto>> ListTechniciansAsync(CancellationToken ct);
}

/// <summary>User approval and role management (Flight Dispatcher only). Roles live in user_role; history is kept.</summary>
public sealed class UserAdminService(
    IUserRepository users, IActionAuditor auditor, IUnitOfWork unitOfWork, ICurrentUser currentUser, IClock clock) : IUserAdminService
{
    public async Task<PagedResult<AdminUserDto>> ListAsync(UserFilter filter, PageRequest page, CancellationToken ct) =>
        (await users.ListAsync(filter, page.Normalize(), ct)).Map(u => u.ToAdminDto());

    public async Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken ct) =>
        (await users.ListActiveRolesAsync(ct)).Select(r => new RoleDto((int)r.RoleId, r.RoleName)).ToList();

    public async Task<AdminUserDto> ApproveAsync(long id, ApproveUserRequest request, CancellationToken ct)
    {
        var roles = await ValidateRolesAsync(request.Roles, ct);
        var user = await GetAsync(id, ct);
        if (user.Status != UserStatus.Pending)
            throw new BusinessRuleException($"Only pending accounts can be approved (current status: {user.Status}).", "invalid_transition");

        var now = clock.UtcNow;
        var actor = currentUser.RequireUserId();
        user.Status = UserStatus.Active;
        user.ApprovedAtUtc = now;
        user.ApprovedByUserId = actor;
        user.UpdatedAtUtc = now;
        ApplyRoles(user, roles, actor, now);
        await auditor.RecordAsync("UserApproved", nameof(AppUser), user.Id.ToString(), new { roles = roles.Select(r => r.ToString()) }, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return user.ToAdminDto();
    }

    public async Task<AdminUserDto> AssignRolesAsync(long id, AssignRolesRequest request, CancellationToken ct)
    {
        var roles = await ValidateRolesAsync(request.Roles, ct);
        var user = await GetAsync(id, ct);
        if (user.Status == UserStatus.Pending)
            throw new BusinessRuleException("Approve the account to assign its first roles.", "invalid_transition");
        var actor = currentUser.RequireUserId();
        if (id == actor && !roles.Contains(UserRole.FlightDispatcher))
            throw new BusinessRuleException("You cannot remove your own Flight Dispatcher role.");

        var now = clock.UtcNow;
        var previous = user.ActiveRoles.ToList();
        ApplyRoles(user, roles, actor, now);
        user.RotateSecurityStamp(); // existing tokens carry the old roles
        user.UpdatedAtUtc = now;
        await AuthService.RevokeAllRefreshTokensAsync(users, user.Id, now, RefreshRevocation.AccountChanged, ct);
        await auditor.RecordAsync("UserRolesChanged", nameof(AppUser), user.Id.ToString(),
            new { from = previous.Select(r => r.ToString()), to = roles.Select(r => r.ToString()) }, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return user.ToAdminDto();
    }

    public async Task<AdminUserDto> DisableAsync(long id, DisableUserRequest request, CancellationToken ct)
    {
        new Validator().Required("reason", request.Reason, "Reason").MaxLength("reason", request.Reason, 500, "Reason").ThrowIfInvalid();
        if (id == currentUser.UserId) throw new BusinessRuleException("You cannot disable your own account.");
        var user = await GetAsync(id, ct);
        if (user.Status == UserStatus.Disabled) throw new BusinessRuleException("Account is already disabled.", "invalid_transition");

        var now = clock.UtcNow;
        user.Status = UserStatus.Disabled;
        user.DisabledReason = request.Reason!.Trim();
        user.RotateSecurityStamp();
        user.UpdatedAtUtc = now;
        await AuthService.RevokeAllRefreshTokensAsync(users, user.Id, now, RefreshRevocation.AccountChanged, ct);
        await auditor.RecordAsync("UserDisabled", nameof(AppUser), user.Id.ToString(), new { reason = user.DisabledReason }, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return user.ToAdminDto();
    }

    public async Task<AdminUserDto> EnableAsync(long id, CancellationToken ct)
    {
        var user = await GetAsync(id, ct);
        if (user.Status != UserStatus.Disabled) throw new BusinessRuleException("Account is not disabled.", "invalid_transition");

        user.Status = user.ActiveRoles.Count == 0 ? UserStatus.Pending : UserStatus.Active;
        user.DisabledReason = null;
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        user.UpdatedAtUtc = clock.UtcNow;
        await auditor.RecordAsync("UserEnabled", nameof(AppUser), user.Id.ToString(), null, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return user.ToAdminDto();
    }

    public async Task<IReadOnlyList<TechnicianDto>> ListTechniciansAsync(CancellationToken ct) =>
        (await users.ListActiveByRoleAsync(UserRole.MaintenanceTechnician, ct))
            .Select(u => new TechnicianDto(u.Id, u.DisplayName, u.Username)).ToList();

    /// <summary>Makes <paramref name="roles"/> the user's active set: new rows for added roles, Inactive (history) for removed.</summary>
    internal static void ApplyRoles(AppUser user, IReadOnlyCollection<UserRole> roles, long actor, DateTime now)
    {
        foreach (var assignment in user.UserRoles.Where(r => r.StatusId == LifecycleStatus.Active && !roles.Contains(r.RoleId)))
        {
            assignment.StatusId = LifecycleStatus.Inactive;
            assignment.RevokedAtUtc = now;
            assignment.RevokedByUserId = actor;
        }
        foreach (var role in roles.Where(r => !user.HasRole(r)))
            user.UserRoles.Add(new UserRoleAssignment { UserId = user.Id, RoleId = role, AssignedAtUtc = now, AssignedByUserId = actor });
    }

    private async Task<IReadOnlyList<UserRole>> ValidateRolesAsync(IReadOnlyList<UserRole>? requested, CancellationToken ct)
    {
        var active = (await users.ListActiveRolesAsync(ct)).Select(r => r.RoleId).ToHashSet();
        var roles = (requested ?? []).Distinct().ToList();
        new Validator()
            .Must(roles.Count > 0, "roles", "Select at least one role.")
            .Must(roles.All(active.Contains), "roles", "One or more roles are not available.")
            .ThrowIfInvalid();
        return roles;
    }

    private async Task<AppUser> GetAsync(long id, CancellationToken ct) =>
        await users.GetByIdAsync(id, ct) ?? throw new NotFoundException("User", id);
}
