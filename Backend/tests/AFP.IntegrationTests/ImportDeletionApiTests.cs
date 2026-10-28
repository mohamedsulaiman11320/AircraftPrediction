using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AFP.IntegrationTests;

/// <summary>
/// Import deletion must remove exactly what the import owns and nothing else. These tests are mostly
/// about what SURVIVES: shared lake partitions, other imports, aircraft and maintenance evidence.
/// </summary>
public class ImportDeletionApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<(Guid Id, Guid AircraftId)> SeedImportAsync(
        string fileName, long? lakeBatchId, int rows, int rejected, string registration)
    {
        var aircraft = await factory.CreateAircraftAsync(registration);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var import = new TelemetryImport
        {
            FileName = fileName,
            FileSha256 = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            StoredPath = $"s3://bronze/telemetry/{Guid.NewGuid():N}.json",
            Source = TelemetryImportSource.RawJson,
            Status = TelemetryImportStatus.Completed,
            LakeBatchId = lakeBatchId,
            RequestedAtUtc = DateTime.UtcNow,
        };
        db.TelemetryImports.Add(import);

        for (var i = 0; i < rows; i++)
            db.TelemetryRecords.Add(new TelemetryRecord
            {
                AircraftId = aircraft.Id, ImportId = import.Id, RecordedAtUtc = DateTime.UtcNow.AddMinutes(-i),
                TemperatureC = 500 + i, PressureKpa = 300, DataSource = "test",
                QualityStatus = QualityStatus.Valid, IngestedAtUtc = DateTime.UtcNow,
                SourceRecordId = $"{fileName}-{i}",
            });
        for (var i = 0; i < rejected; i++)
            db.TelemetryRejectedRows.Add(new TelemetryRejectedRow
            { ImportId = import.Id, RowNumber = i + 1, RawLine = "bad", Reasons = "test" });

        await db.SaveChangesAsync();
        return (import.Id, aircraft.Id);
    }

    [Fact]
    public async Task Deleting_an_import_removes_its_own_rows_and_leaves_another_import_intact()
    {
        var de = await factory.ClientAsAsync(UserRole.DataEngineer);
        var target = await SeedImportAsync("target.json", lakeBatchId: 9001, rows: 5, rejected: 2, "IT-DEL-A");
        var other = await SeedImportAsync("other.json", lakeBatchId: 9002, rows: 3, rejected: 1, "IT-DEL-B");

        var response = await de.DeleteAsync($"/api/v1/telemetry/imports/{target.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("completed").GetBoolean());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Null(await db.TelemetryImports.FirstOrDefaultAsync(i => i.Id == target.Id));
        Assert.Equal(0, await db.TelemetryRecords.CountAsync(r => r.ImportId == target.Id));
        Assert.Equal(0, await db.TelemetryRejectedRows.CountAsync(r => r.ImportId == target.Id));

        // The unrelated import is untouched.
        Assert.NotNull(await db.TelemetryImports.FirstOrDefaultAsync(i => i.Id == other.Id));
        Assert.Equal(3, await db.TelemetryRecords.CountAsync(r => r.ImportId == other.Id));
        Assert.Equal(1, await db.TelemetryRejectedRows.CountAsync(r => r.ImportId == other.Id));

        // Aircraft are shared infrastructure and are never removed by an import deletion.
        Assert.NotNull(await db.Aircraft.FirstOrDefaultAsync(a => a.Id == target.AircraftId));
    }

    [Fact]
    public async Task A_lake_partition_shared_with_another_import_is_preserved()
    {
        var de = await factory.ClientAsAsync(UserRole.DataEngineer);
        const long shared = 7777;
        var first = await SeedImportAsync("shared-a.json", shared, rows: 2, rejected: 0, "IT-DEL-S1");
        await SeedImportAsync("shared-b.json", shared, rows: 2, rejected: 0, "IT-DEL-S2");

        var plan = await de.GetFromJsonAsync<JsonElement>($"/api/v1/telemetry/imports/{first.Id}/deletion-plan");

        Assert.Equal(0, plan.GetProperty("silverPartitionsRemoved").GetInt32());
        Assert.Equal(0, plan.GetProperty("goldPartitionsRemoved").GetInt32());
        Assert.Contains(plan.GetProperty("preserved").EnumerateArray().Select(p => p.GetString()),
            p => p!.Contains("shared", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_import_with_no_recorded_batch_keeps_its_lake_data_rather_than_guessing()
    {
        var de = await factory.ClientAsAsync(UserRole.DataEngineer);
        var legacy = await SeedImportAsync("legacy.json", lakeBatchId: null, rows: 1, rejected: 0, "IT-DEL-L");

        var plan = await de.GetFromJsonAsync<JsonElement>($"/api/v1/telemetry/imports/{legacy.Id}/deletion-plan");

        Assert.Equal(0, plan.GetProperty("silverPartitionsRemoved").GetInt32());
        Assert.Contains(plan.GetProperty("preserved").EnumerateArray().Select(p => p.GetString()),
            p => p!.Contains("cannot be proven", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_exclusively_owned_batch_is_removed()
    {
        var de = await factory.ClientAsAsync(UserRole.DataEngineer);
        var only = await SeedImportAsync("solo.json", lakeBatchId: 8123, rows: 1, rejected: 0, "IT-DEL-SOLO");

        var plan = await de.GetFromJsonAsync<JsonElement>($"/api/v1/telemetry/imports/{only.Id}/deletion-plan");

        Assert.Equal(1, plan.GetProperty("silverPartitionsRemoved").GetInt32());
        Assert.Equal(1, plan.GetProperty("goldPartitionsRemoved").GetInt32());
    }

    /// <summary>
    /// Regression: object storage is cleaned BEFORE the database row is removed. If the row went first, a
    /// partial failure would leave nothing to retry against and the "safe to retry" promise would be false.
    /// Here the storage delete fails, so the import must still exist and a retry must be able to finish.
    /// </summary>
    [Fact]
    public async Task A_storage_failure_leaves_the_import_intact_so_the_retry_can_resume()
    {
        var de = await factory.ClientAsAsync(UserRole.DataEngineer);
        var import = await SeedImportAsync("resumable.json", 9400, rows: 3, rejected: 1, "IT-DEL-R");

        factory.RawStore.FailDeletes = true;
        var first = await de.DeleteAsync($"/api/v1/telemetry/imports/{import.Id}");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("completed").GetBoolean());
        Assert.NotEmpty(body.GetProperty("remainingWork").EnumerateArray());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Nothing was removed, so the retry has something to work with.
            Assert.NotNull(await db.TelemetryImports.FirstOrDefaultAsync(i => i.Id == import.Id));
            Assert.Equal(3, await db.TelemetryRecords.CountAsync(r => r.ImportId == import.Id));
        }

        factory.RawStore.FailDeletes = false;
        var retry = await de.DeleteAsync($"/api/v1/telemetry/imports/{import.Id}");

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.True((await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("completed").GetBoolean());

        using var verify = factory.Services.CreateScope();
        var check = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Null(await check.TelemetryImports.FirstOrDefaultAsync(i => i.Id == import.Id));
        Assert.Equal(0, await check.TelemetryRecords.CountAsync(r => r.ImportId == import.Id));
    }

    [Fact]
    public async Task Deleting_twice_is_safe_and_the_second_call_reports_not_found()
    {
        var de = await factory.ClientAsAsync(UserRole.DataEngineer);
        var import = await SeedImportAsync("twice.json", 9100, rows: 2, rejected: 0, "IT-DEL-T");

        Assert.Equal(HttpStatusCode.OK, (await de.DeleteAsync($"/api/v1/telemetry/imports/{import.Id}")).StatusCode);
        // Idempotent from the caller's perspective: the resource is gone, and nothing is corrupted.
        Assert.Equal(HttpStatusCode.NotFound, (await de.DeleteAsync($"/api/v1/telemetry/imports/{import.Id}")).StatusCode);
    }

    [Fact]
    public async Task An_import_still_processing_cannot_be_deleted()
    {
        var de = await factory.ClientAsAsync(UserRole.DataEngineer);
        var aircraft = await factory.CreateAircraftAsync("IT-DEL-BUSY");
        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var import = new TelemetryImport
            {
                FileName = "busy.json", FileSha256 = new string('b', 64), StoredPath = "s3://bronze/telemetry/busy.json",
                Source = TelemetryImportSource.RawJson, Status = TelemetryImportStatus.Processing,
                RequestedAtUtc = DateTime.UtcNow,
            };
            db.TelemetryImports.Add(import);
            await db.SaveChangesAsync();
            id = import.Id;
        }

        var response = await de.DeleteAsync($"/api/v1/telemetry/imports/{id}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("import_in_progress", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        _ = aircraft;
    }

    [Theory]
    [InlineData(UserRole.FlightDispatcher)]
    [InlineData(UserRole.MaintenanceTechnician)]
    public async Task Only_the_data_engineer_may_delete_an_import(UserRole role)
    {
        var client = await factory.ClientAsAsync(role);
        var response = await client.DeleteAsync($"/api/v1/telemetry/imports/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_unauthenticated_delete_is_rejected()
    {
        var response = await factory.CreateClient().DeleteAsync($"/api/v1/telemetry/imports/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_import_id_cannot_be_used_to_reach_anything()
    {
        var de = await factory.ClientAsAsync(UserRole.DataEngineer);
        var response = await de.DeleteAsync($"/api/v1/telemetry/imports/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deletion_is_recorded_in_the_action_audit()
    {
        var de = await factory.ClientAsAsync(UserRole.DataEngineer);
        var import = await SeedImportAsync("audited.json", 9200, rows: 1, rejected: 0, "IT-DEL-AUD");

        await de.DeleteAsync($"/api/v1/telemetry/imports/{import.Id}");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.ActionAudits.AnyAsync(a =>
            a.Action == "TelemetryImportDeleted" && a.EntityId == import.Id.ToString()));
    }

    [Fact]
    public async Task Maintenance_evidence_survives_an_import_deletion()
    {
        var de = await factory.ClientAsAsync(UserRole.DataEngineer);
        var import = await SeedImportAsync("with-maint.json", 9300, rows: 2, rejected: 0, "IT-DEL-M");
        Guid logId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var log = new MaintenanceLog
            {
                AircraftId = import.AircraftId, EventType = MaintenanceEventType.Unscheduled,
                OccurredAtUtc = DateTime.UtcNow.AddDays(-2), Description = "[LOG-1] ENG_UNSCHED_REMOVAL",
                IsVerified = true, ActualFailureConfirmed = true, Source = MaintenanceLogSource.Import,
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.MaintenanceLogs.Add(log);
            await db.SaveChangesAsync();
            logId = log.Id;
        }

        await de.DeleteAsync($"/api/v1/telemetry/imports/{import.Id}");

        using var verify = factory.Services.CreateScope();
        var check = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.NotNull(await check.MaintenanceLogs.FirstOrDefaultAsync(m => m.Id == logId));
    }

    [Fact]
    public async Task The_bronze_object_is_removed_from_storage()
    {
        var de = await factory.ClientAsAsync(UserRole.DataEngineer);
        var json = "[{\"telemetry_id\":\"DEL-" + Guid.NewGuid().ToString("N") + "\",\"tail_number\":\"N120UA\"," +
                   "\"timestamp_epoch\":1788220800,\"metrics\":{\"engine_temp_c\":470.1,\"oil_pressure_psi\":24.9," +
                   "\"vibration_index\":0.6},\"status_flag\":\"NORMAL\"}]";
        using var form = new MultipartFormDataContent
        { { new ByteArrayContent(Encoding.UTF8.GetBytes(json)), "file", "to-delete.json" } };
        var upload = await de.PostAsync("/api/v1/telemetry/raw-uploads", form);
        var accepted = await upload.Content.ReadFromJsonAsync<JsonElement>();
        var id = accepted.GetProperty("id").GetGuid();

        var detail = await de.GetFromJsonAsync<JsonElement>($"/api/v1/telemetry/imports/{id}");
        var storedPath = detail.GetProperty("storedPath").GetString()!;
        Assert.Contains(storedPath, factory.RawStore.Objects.Keys);

        // Queued imports are protected, so mark it finished the way the ETL would before deleting.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var import = await db.TelemetryImports.FirstAsync(i => i.Id == id);
            import.Status = TelemetryImportStatus.Completed;
            await db.SaveChangesAsync();
        }

        var response = await de.DeleteAsync($"/api/v1/telemetry/imports/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("completed").GetBoolean());
        Assert.DoesNotContain(storedPath, factory.RawStore.Objects.Keys);
    }
}
