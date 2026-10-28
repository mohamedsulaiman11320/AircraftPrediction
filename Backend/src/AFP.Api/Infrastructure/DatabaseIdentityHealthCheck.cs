using AFP.Infrastructure.Configuration;
using AFP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AFP.Api.Infrastructure;

/// <summary>Verifies the database is reachable AND is the configured one (runtime isolation check).</summary>
public sealed class DatabaseIdentityHealthCheck(AppDbContext db, IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        if (!db.Database.IsRelational()) return HealthCheckResult.Healthy("Non-relational test provider.");
        try
        {
            var actual = await db.Database.SqlQueryRaw<string>("SELECT current_database() AS \"Value\"").SingleAsync(ct);
            var expected = DatabaseTarget.ExpectedName(key => configuration[key]);
            return actual == expected
                ? HealthCheckResult.Healthy($"Connected to {actual}.")
                : HealthCheckResult.Unhealthy($"Connected to '{actual}' but DB_NAME is '{expected}'.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database unreachable.", ex);
        }
    }
}
