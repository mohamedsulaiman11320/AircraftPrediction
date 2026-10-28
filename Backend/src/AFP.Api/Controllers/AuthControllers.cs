using AFP.Api.Security;
using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Application.Contracts;
using AFP.Application.Services;
using AFP.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AFP.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
[Produces("application/json")]
public sealed class AuthController(IAuthService auth) : ControllerBase
{
    public const string RateLimitPolicy = "auth";

    private ClientInfo Client => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString());

    /// <summary>Creates a pending account (no role). The response never reveals whether the email already exists.</summary>
    [HttpPost("register"), AllowAnonymous, EnableRateLimiting(RateLimitPolicy)]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct) =>
        Accepted(await auth.RegisterAsync(request, Client, ct));

    [HttpPost("login"), AllowAnonymous, EnableRateLimiting(RateLimitPolicy)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status423Locked)]
    public Task<TokenResponse> Login(LoginRequest request, CancellationToken ct) => auth.LoginAsync(request, Client, ct);

    /// <summary>OAuth 2.0 refresh-token grant with rotation; reuse of a consumed token revokes its family.</summary>
    [HttpPost("refresh"), AllowAnonymous, EnableRateLimiting(RateLimitPolicy)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public Task<TokenResponse> Refresh(RefreshRequest request, CancellationToken ct) => auth.RefreshAsync(request, Client, ct);

    /// <summary>Revokes the presented access token and the refresh-token family server-side.</summary>
    [HttpPost("logout"), Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest? request, CancellationToken ct)
    {
        await auth.LogoutAsync(request ?? new LogoutRequest(null), ct);
        return NoContent();
    }

    [HttpPost("forgot-password"), AllowAnonymous, EnableRateLimiting(RateLimitPolicy)]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken ct) =>
        Accepted(await auth.ForgotPasswordAsync(request, Client, ct));

    [HttpPost("reset-password"), AllowAnonymous, EnableRateLimiting(RateLimitPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken ct)
    {
        await auth.ResetPasswordAsync(request, Client, ct);
        return NoContent();
    }

    [HttpGet("me"), Authorize]
    public Task<UserDto> Me(CancellationToken ct) => auth.GetCurrentUserAsync(ct);
}

[ApiController]
[Route("api/v1/admin/users")]
// User administration belongs to the Data Engineer / technical operations owner (2026-10-26);
// the Dispatcher no longer holds it.
[Authorize(Policy = Policies.DataEngineer)]
public sealed class AdminUsersController(IUserAdminService admin) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<AdminUserDto>> List(
        [FromQuery] UserStatus? status, [FromQuery] UserRole? role, [FromQuery] string? search,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        admin.ListAsync(new UserFilter(status, role, search), new PageRequest(page, pageSize), ct);

    [HttpPost("{id:long}/approve")]
    public Task<AdminUserDto> Approve(long id, ApproveUserRequest request, CancellationToken ct) => admin.ApproveAsync(id, request, ct);

    /// <summary>Replaces the user's active roles (removed assignments are kept as Inactive history).</summary>
    [HttpPut("{id:long}/roles")]
    public Task<AdminUserDto> AssignRoles(long id, AssignRolesRequest request, CancellationToken ct) => admin.AssignRolesAsync(id, request, ct);

    [HttpPost("{id:long}/disable")]
    public Task<AdminUserDto> Disable(long id, DisableUserRequest request, CancellationToken ct) => admin.DisableAsync(id, request, ct);

    [HttpPost("{id:long}/enable")]
    public Task<AdminUserDto> Enable(long id, CancellationToken ct) => admin.EnableAsync(id, ct);
}

[ApiController]
[Route("api/v1/admin/roles")]
// User administration belongs to the Data Engineer / technical operations owner (2026-10-26);
// the Dispatcher no longer holds it.
[Authorize(Policy = Policies.DataEngineer)]
public sealed class AdminRolesController(IUserAdminService admin) : ControllerBase
{
    /// <summary>Active roles from the seeded role table (clients must not hard-code role lists).</summary>
    [HttpGet]
    public Task<IReadOnlyList<RoleDto>> List(CancellationToken ct) => admin.ListRolesAsync(ct);
}

[ApiController]
[Route("api/v1/technicians")]
[Authorize(Policy = Policies.Dispatcher)]
public sealed class TechniciansController(IUserAdminService users) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<TechnicianDto>> List(CancellationToken ct) => users.ListTechniciansAsync(ct);
}

/// <summary>Approved maintenance action codes for the technician report form (and the dispatcher's review).</summary>
[ApiController]
[Route("api/v1/maintenance-action-codes")]
[Authorize(Policy = Policies.DispatcherOrTechnician)]
public sealed class MaintenanceActionCodesController(IWorkOrderService workOrders) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<MaintenanceActionCodeDto>> List(CancellationToken ct) => workOrders.ListActionCodesAsync(ct);
}

[ApiController]
[Route("api/v1/audit")]
public sealed class AuditController(IAuditQueryService audit) : ControllerBase
{
    [HttpGet("actions"), Authorize(Policy = Policies.Unassigned)]
    public Task<PagedResult<ActionAuditDto>> Actions(
        [FromQuery] string? entityType, [FromQuery] string? entityId, [FromQuery] long? userId,
        [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        audit.ListActionsAsync(new ActionAuditFilter(entityType, entityId, userId, fromUtc?.ToUniversalTime(), toUtc?.ToUniversalTime()),
            new PageRequest(page, pageSize), ct);

    [HttpGet("auth"), Authorize(Policy = Policies.Unassigned)]
    public Task<PagedResult<AuthAuditDto>> Auth(
        [FromQuery] string? email, [FromQuery] string? eventType, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        audit.ListAuthAsync(new AuthAuditFilter(email, eventType, fromUtc?.ToUniversalTime(), toUtc?.ToUniversalTime()),
            new PageRequest(page, pageSize), ct);
}
