using System.Security.Claims;
using AFP.Application.Abstractions;
using AFP.Domain.Enums;
using AFP.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;

namespace AFP.Api.Security;

/// <summary>
/// Server-side RBAC. Flight Dispatcher = the five approved dispatcher tabs (health dashboard, fleet health,
/// anomalies/work orders, reports/maintenance log, user management); Maintenance Technician = own assigned
/// tickets only (row-level scoping in the services); Data Engineer = Data and Model areas (no maintenance,
/// dispatch, user administration or model approval). Every endpoint names one policy.
/// </summary>
public static class Policies
{
    /// <summary>Flight Dispatcher only (user administration, fleet health, anomalies, work orders, reports).</summary>
    public const string Dispatcher = nameof(Dispatcher);

    /// <summary>
    /// Deliberately held by no current role. Capabilities that fell outside the approved dispatcher tabs when the
    /// Dispatcher role was scoped down, and that no other role has yet been granted: prediction review/outcome,
    /// audit queries and the leadership dashboard.
    /// <para>
    /// These endpoints return 403 for everyone until the business assigns them an owner. The implementation is
    /// intact and reachable again by naming a role here — nothing was deleted.
    /// </para>
    /// </summary>
    public const string Unassigned = nameof(Unassigned);

    /// <summary>
    /// Model governance: approving or rejecting a trained candidate. Assigned to the Data Engineer
    /// (the BRD's "data engineer / technical operations owner") by the project owner on 2026-10-23;
    /// the BRD itself names no model approver.
    /// <para>
    /// Deliberately narrow — it grants review of candidates only. It does NOT grant training, activation,
    /// prediction review or any dispatcher capability, and it does not weaken the service-level gates:
    /// approval still requires evaluation metrics and a readiness status that is not Blocked, and only an
    /// approved candidate can be activated (<see cref="AFP.Domain.Rules.ModelLifecycle"/>).
    /// </para>
    /// </summary>
    public const string ModelApproval = nameof(ModelApproval);

    /// <summary>Maintenance Technician only ("My tickets" dashboard).</summary>
    public const string Technician = nameof(Technician);

    /// <summary>Data Engineer only.</summary>
    public const string DataEngineer = nameof(DataEngineer);

    /// <summary>Data and Model areas: Flight Dispatcher (all screens) or Data Engineer.</summary>
    public const string DataAccess = nameof(DataAccess);

    /// <summary>Work-order participants; the service restricts technicians to their own tickets.</summary>
    public const string DispatcherOrTechnician = nameof(DispatcherOrTechnician);

    public static void Configure(AuthorizationOptions options)
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        options.AddPolicy(Dispatcher, p => p.RequireAuthenticatedUser().RequireRole(UserRole.FlightDispatcher.ToString()));
        options.AddPolicy(Technician, p => p.RequireAuthenticatedUser().RequireRole(UserRole.MaintenanceTechnician.ToString()));
        options.AddPolicy(DataEngineer, p => p.RequireAuthenticatedUser().RequireRole(UserRole.DataEngineer.ToString()));
        options.AddPolicy(DataAccess, p => p.RequireAuthenticatedUser()
            .RequireRole(UserRole.FlightDispatcher.ToString(), UserRole.DataEngineer.ToString()));
        options.AddPolicy(DispatcherOrTechnician, p => p.RequireAuthenticatedUser()
            .RequireRole(UserRole.FlightDispatcher.ToString(), UserRole.MaintenanceTechnician.ToString()));
        // No role satisfies this: the requirement is unsatisfiable by construction, not merely unassigned by data.
        options.AddPolicy(Unassigned, p => p.RequireAuthenticatedUser().RequireAssertion(_ => false));
        options.AddPolicy(ModelApproval, p => p.RequireAuthenticatedUser().RequireRole(UserRole.DataEngineer.ToString()));
    }
}

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public long? UserId => long.TryParse(Principal?.FindFirstValue(AfpClaims.Subject), out var id) ? id : null;

    public IReadOnlyList<UserRole> Roles =>
        Principal?.FindAll(AfpClaims.Role)
            .Select(c => Enum.TryParse<UserRole>(c.Value, out var role) ? role : (UserRole?)null)
            .Where(r => r.HasValue).Select(r => r!.Value).Distinct().ToList() ?? [];

    public string? TokenId => Principal?.FindFirstValue(AfpClaims.TokenId);

    public DateTime? TokenExpiresAtUtc =>
        long.TryParse(Principal?.FindFirstValue("exp"), out var exp) ? DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime : null;

    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent => accessor.HttpContext?.Request.Headers.UserAgent.ToString();

    public string? CorrelationId => accessor.HttpContext?.TraceIdentifier;
}
