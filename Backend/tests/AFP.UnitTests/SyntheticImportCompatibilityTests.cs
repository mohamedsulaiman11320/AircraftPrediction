using AFP.Application.Common;
using AFP.Application.Telemetry;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Infrastructure.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AFP.UnitTests;

/// <summary>
/// Opt-in check that the ML team's synthetic generator output (tools/data) is accepted by the import pipeline.
/// Set AFP_SYNTHETIC_CSV to a generated file to run it; otherwise it returns immediately.
/// </summary>
public class SyntheticImportCompatibilityTests
{
    [Fact]
    public async Task Generated_synthetic_csv_imports_with_expected_rejections()
    {
        var csv = Environment.GetEnvironmentVariable("AFP_SYNTHETIC_CSV");
        if (string.IsNullOrEmpty(csv) || !File.Exists(csv)) return;

        using var t = new TestContext();
        t.Clock.UtcNow = DateTime.UtcNow;
        var user = await t.AddUserAsync(UserRole.FlightDispatcher);
        for (var i = 1; i <= 12; i++) await t.AddAircraftAsync($"DEMO-A{i:D2}");

        var dir = Path.Combine(Path.GetTempPath(), "afp-synth", Guid.NewGuid().ToString("N"));
        var store = new LocalImportFileStore(Options.Create(new TelemetryImportOptions { StorageDirectory = dir }));
        var importId = Guid.NewGuid();
        await using (var source = File.OpenRead(csv))
        {
            var path = await store.SaveAsync(importId, "synthetic.csv", source, default);
            t.Db.TelemetryImports.Add(new TelemetryImport
            {
                Id = importId, FileName = "synthetic.csv", FileSha256 = new string('c', 64), StoredPath = path,
                RequestedByUserId = user.Id, RequestedAtUtc = t.Clock.UtcNow,
            });
        }
        await t.Db.SaveChangesAsync();

        var started = DateTime.UtcNow;
        await new TelemetryImportProcessor(t.Telemetry, t.Aircraft, t.Runs, store, t.UnitOfWork, t.Clock,
            Options.Create(new TelemetryImportOptions()), NullLogger<TelemetryImportProcessor>.Instance).ProcessAsync(importId, default);

        t.Db.ChangeTracker.Clear();
        var import = await t.Db.TelemetryImports.SingleAsync();
        Console.WriteLine($"status={import.Status} total={import.TotalRows} accepted={import.AcceptedRows} rejected={import.RejectedRows} " +
                          $"duplicates={import.DuplicateRows} elapsed={(DateTime.UtcNow - started).TotalSeconds:F1}s error={import.ErrorMessage}");
        foreach (var w in import.Warnings) Console.WriteLine($"warning: {w}");
        foreach (var r in await t.Db.TelemetryRejectedRows.Take(15).ToListAsync()) Console.WriteLine($"rejected #{r.RowNumber}: {r.Reasons}");

        Assert.Equal(TelemetryImportStatus.Completed, import.Status);
        Assert.True(import.AcceptedRows > import.TotalRows * 0.99, "almost all synthetic rows should be accepted");
        Assert.True(import.RejectedRows > 0, "the generator includes deliberately invalid rows");
        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, true);
    }
}
