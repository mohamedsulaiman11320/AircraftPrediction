using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AFP.Infrastructure.Persistence.Repositories;

public sealed class UserRepository(AppDbContext db) : IUserRepository
{
    private IQueryable<AppUser> WithRoles => db.Users.Include(u => u.UserRoles);

    private static IQueryable<AppUser> HavingActiveRole(IQueryable<AppUser> q, UserRole role) =>
        q.Where(u => u.UserRoles.Any(r => r.RoleId == role && r.StatusId == LifecycleStatus.Active));

    public Task<AppUser?> GetByIdAsync(long id, CancellationToken ct) => WithRoles.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<AppUser?> GetByUsernameAsync(string normalizedUsername, CancellationToken ct) =>
        WithRoles.FirstOrDefaultAsync(u => u.Username == normalizedUsername, ct);

    public Task<AppUser?> GetByEmailAsync(string normalizedEmail, CancellationToken ct) =>
        WithRoles.FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);

    public Task<bool> AnyActiveUserWithRoleAsync(UserRole role, CancellationToken ct) =>
        HavingActiveRole(db.Users.Where(u => u.Status == UserStatus.Active), role).AnyAsync(ct);

    public Task<PagedResult<AppUser>> ListAsync(UserFilter filter, PageRequest page, CancellationToken ct)
    {
        var q = WithRoles.AsNoTracking();
        if (filter.Status is { } status) q = q.Where(u => u.Status == status);
        if (filter.Role is { } role) q = HavingActiveRole(q, role);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = QueryExtensions.Pattern(filter.Search);
            q = q.Where(u => u.Username.Contains(s) || u.Email.Contains(s) || u.DisplayName.ToLower().Contains(s));
        }
        return q.OrderBy(u => u.Status).ThenByDescending(u => u.CreatedAtUtc).ToPagedAsync(page, ct);
    }

    public async Task<IReadOnlyList<AppUser>> ListActiveByRoleAsync(UserRole role, CancellationToken ct) =>
        await HavingActiveRole(WithRoles.AsNoTracking().Where(u => u.Status == UserStatus.Active), role)
            .OrderBy(u => u.DisplayName).ToListAsync(ct);

    public async Task<IReadOnlyDictionary<long, string>> GetDisplayNamesAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    public async Task AddAsync(AppUser user, CancellationToken ct) => await db.Users.AddAsync(user, ct);

    public async Task<IReadOnlyList<Role>> ListActiveRolesAsync(CancellationToken ct) =>
        await db.Roles.AsNoTracking().Where(r => r.StatusId == LifecycleStatus.Active).OrderBy(r => r.RoleId).ToListAsync(ct);

    public async Task AddResetTokenAsync(PasswordResetToken token, CancellationToken ct) => await db.PasswordResetTokens.AddAsync(token, ct);

    public Task<PasswordResetToken?> GetResetTokenByHashAsync(string tokenHash, CancellationToken ct) =>
        db.PasswordResetTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

    public async Task<IReadOnlyList<PasswordResetToken>> GetUsableResetTokensAsync(long userId, DateTime nowUtc, CancellationToken ct) =>
        await db.PasswordResetTokens.Where(t => t.UserId == userId && t.UsedAtUtc == null && t.ExpiresAtUtc > nowUtc).ToListAsync(ct);

    public async Task AddRevokedTokenAsync(RevokedToken token, CancellationToken ct) => await db.RevokedTokens.AddAsync(token, ct);

    public Task<bool> IsTokenRevokedAsync(string jti, CancellationToken ct) => db.RevokedTokens.AnyAsync(t => t.Jti == jti, ct);

    public async Task AddRefreshTokenAsync(RefreshToken token, CancellationToken ct) => await db.RefreshTokens.AddAsync(token, ct);

    public Task<RefreshToken?> GetRefreshTokenByHashAsync(string tokenHash, CancellationToken ct) =>
        db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

    public async Task<IReadOnlyList<RefreshToken>> ListUnrevokedRefreshTokensAsync(long userId, Guid? familyId, CancellationToken ct)
    {
        var q = db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAtUtc == null);
        if (familyId is { } family) q = q.Where(t => t.FamilyId == family);
        return await q.ToListAsync(ct);
    }
}

public sealed class AuditRepository(AppDbContext db) : IAuditRepository
{
    public async Task AddActionAsync(ActionAudit entry, CancellationToken ct) => await db.ActionAudits.AddAsync(entry, ct);

    public async Task AddAuthAsync(AuthAudit entry, CancellationToken ct) => await db.AuthAudits.AddAsync(entry, ct);

    public Task<PagedResult<ActionAudit>> ListActionsAsync(ActionAuditFilter f, PageRequest page, CancellationToken ct)
    {
        var q = db.ActionAudits.AsNoTracking().Include(a => a.User).AsQueryable();
        if (!string.IsNullOrWhiteSpace(f.EntityType)) q = q.Where(a => a.EntityType == f.EntityType);
        if (!string.IsNullOrWhiteSpace(f.EntityId)) q = q.Where(a => a.EntityId == f.EntityId);
        if (f.UserId is { } userId) q = q.Where(a => a.UserId == userId);
        if (f.FromUtc is { } from) q = q.Where(a => a.OccurredAtUtc >= from);
        if (f.ToUtc is { } to) q = q.Where(a => a.OccurredAtUtc <= to);
        return q.OrderByDescending(a => a.OccurredAtUtc).ThenByDescending(a => a.Id).ToPagedAsync(page, ct);
    }

    public Task<PagedResult<AuthAudit>> ListAuthAsync(AuthAuditFilter f, PageRequest page, CancellationToken ct)
    {
        var q = db.AuthAudits.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(f.Email)) q = q.Where(a => a.Email == f.Email.Trim().ToLower());
        if (!string.IsNullOrWhiteSpace(f.EventType)) q = q.Where(a => a.EventType == f.EventType);
        if (f.FromUtc is { } from) q = q.Where(a => a.OccurredAtUtc >= from);
        if (f.ToUtc is { } to) q = q.Where(a => a.OccurredAtUtc <= to);
        return q.OrderByDescending(a => a.OccurredAtUtc).ThenByDescending(a => a.Id).ToPagedAsync(page, ct);
    }
}
