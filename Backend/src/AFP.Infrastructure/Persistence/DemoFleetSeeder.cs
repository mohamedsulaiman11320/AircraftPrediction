using AFP.Domain.Entities;
using AFP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AFP.Infrastructure.Persistence;

/// <summary>
/// DEVELOPMENT ONLY. Creates fictional demo aircraft (DEMO-A01..DEMO-A12) matching the synthetic telemetry
/// generator in tools/data. Invoked explicitly with the <c>seed-demo-fleet</c> command; never runs automatically.
/// </summary>
public sealed class DemoFleetSeeder(AppDbContext db)
{
    private static readonly (string Model, string Manufacturer, string Group)[] Types =
    [
        ("A320neo", "Airbus", "Narrowbody East"),
        ("737-8", "Boeing", "Narrowbody West"),
        ("E195-E2", "Embraer", "Regional"),
        ("ATR 72-600", "ATR", "Regional Turboprop"),
    ];

    public async Task<int> SeedAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var existing = await db.Aircraft.Select(a => a.RegistrationNumber).ToListAsync(ct);
        var created = 0;
        for (var i = 1; i <= 12; i++)
        {
            var registration = $"DEMO-A{i:D2}";
            if (existing.Contains(registration)) continue;
            var type = Types[(i - 1) % Types.Length];
            db.Aircraft.Add(new Aircraft
            {
                RegistrationNumber = registration,
                Model = type.Model,
                Manufacturer = type.Manufacturer,
                FleetGroup = type.Group,
                SerialNumber = $"DEMO-SN-{1000 + i}",
                OperationalStatus = OperationalStatus.Available,
                StatusReason = "Demo data (development only).",
                StatusChangedAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
            created++;
        }
        await db.SaveChangesAsync(ct);
        return created;
    }
}
