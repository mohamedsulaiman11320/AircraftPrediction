using System.Text;
using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Application.Services;
using AFP.Application.Telemetry;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AFP.UnitTests;

/// <summary>In-memory stand-in for MinIO bronze storage.</summary>
public sealed class FakeRawTelemetryStore : IRawTelemetryStore
{
    public Task DeleteAsync(string location, CancellationToken ct) => Task.CompletedTask;

    public Task<int> DeletePrefixAsync(string bucket, string prefix, CancellationToken ct) => Task.FromResult(0);

    public Dictionary<string, byte[]> Objects { get; } = [];

    public async Task<string> PutAsync(string key, Stream content, long length, string contentType, CancellationToken ct)
    {
        var location = $"s3://bronze/telemetry/{key}";
        if (Objects.ContainsKey(location)) throw new ConflictException("exists", "object_exists");
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        Objects[location] = ms.ToArray();
        return location;
    }

    public Task<string> CopyAsync(string sourceLocation, string destinationKey, CancellationToken ct)
    {
        var location = $"s3://bronze/telemetry/{destinationKey}";
        Objects[location] = Objects[sourceLocation];
        return Task.FromResult(location);
    }
}

public class RawTelemetryJsonTests
{
    private static readonly string[] Keys = new RawTelemetryStorageOptions().RequiredRecordKeys;

    private static RawJsonCheck Check(string json) => RawTelemetryJson.Check(new MemoryStream(Encoding.UTF8.GetBytes(json)), Keys);

    public const string Valid = """
        [{"telemetry_id":"TEL-1","tail_number":"N120UA","timestamp_epoch":1788220800,
          "metrics":{"engine_temp_c":476.8,"oil_pressure_psi":25.5,"vibration_index":0.58},"status_flag":"NORMAL"},
         {"telemetry_id":"TEL-2","tail_number":"N237UA","timestamp_epoch":1788220800,"metrics":null,"status_flag":null}]
        """;

    [Fact]
    public void Valid_array_passes_and_counts_records() => Assert.Equal(new RawJsonCheck(2, []), Check(Valid) with { Errors = [] });

    [Theory]
    [InlineData("{not json", "not valid JSON")]
    [InlineData("""{"telemetry_id":"x"}""", "JSON array")]
    [InlineData("[]", "no telemetry records")]
    [InlineData("[1]", "must be a JSON object")]
    [InlineData("""[{"telemetry_id":"x","tail_number":"N1"}]""", "missing required field(s) timestamp_epoch, metrics")]
    [InlineData("""[{"telemetry_id":"x","tail_number":"N1","timestamp_epoch":1,"metrics":5}]""", "'metrics' must be an object")]
    public void Structural_problems_are_reported(string json, string expected) =>
        Assert.Contains(Check(json).Errors, e => e.Contains(expected));
}

public class RawJsonImportServiceTests : IDisposable
{
    private readonly TestContext _t = new();
    private readonly FakeRawTelemetryStore _store = new();
    public void Dispose() => _t.Dispose();

    private TelemetryService Service(RawTelemetryStorageOptions? options = null) => new(
        _t.Telemetry, _t.Aircraft, new LocalImportFileStoreStub(), _store, new NoopQueue(), _t.Auditor, _t.UnitOfWork, _t.CurrentUser, _t.Clock,
        Options.Create(new TelemetryImportOptions()),
        Options.Create(options ?? new RawTelemetryStorageOptions { AccessKey = "k", SecretKey = "s" }));

    private static UploadedFile File(string name, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return new UploadedFile(name, bytes.Length, new MemoryStream(bytes));
    }

    [Fact]
    public async Task Upload_stores_raw_object_and_queues_tracked_import()
    {
        _t.CurrentUser.SignInAs(await _t.AddUserAsync(UserRole.DataEngineer));
        var accepted = await Service().StartRawJsonImportAsync(File("batch.json", RawTelemetryJsonTests.Valid), default);

        Assert.Equal("Queued", accepted.Status);   // uploaded, not "complete"
        var import = await _t.Db.TelemetryImports.SingleAsync();
        Assert.Equal(TelemetryImportSource.RawJson, import.Source);
        Assert.Equal(2, import.TotalRows);
        Assert.StartsWith("s3://bronze/telemetry/2026/08/", import.StoredPath);
        Assert.Equal(Encoding.UTF8.GetBytes(RawTelemetryJsonTests.Valid), _store.Objects[import.StoredPath]);   // byte-for-byte
        Assert.Contains(await _t.Db.ActionAudits.ToListAsync(), a => a.Action == "RawTelemetryUploaded");

        await Assert.ThrowsAsync<ConflictException>(() => Service().StartRawJsonImportAsync(File("again.json", RawTelemetryJsonTests.Valid), default));
        Assert.Single(_store.Objects);
    }

    [Theory]
    [InlineData("data.csv", "Only .json files")]
    [InlineData("data.json", "missing required field")]
    public async Task Invalid_uploads_are_rejected_before_storage(string name, string expected)
    {
        _t.CurrentUser.SignInAs(await _t.AddUserAsync(UserRole.DataEngineer));
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            Service().StartRawJsonImportAsync(File(name, """[{"telemetry_id":"x"}]"""), default));
        Assert.Contains(ex.Errors["file"], e => e.Contains(expected));
        Assert.Empty(_store.Objects);
    }

    [Fact]
    public async Task Upload_requires_configured_storage()
    {
        _t.CurrentUser.SignInAs(await _t.AddUserAsync(UserRole.DataEngineer));
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            Service(new RawTelemetryStorageOptions()).StartRawJsonImportAsync(File("a.json", RawTelemetryJsonTests.Valid), default));
        Assert.Equal("storage_not_configured", ex.Code);
    }

    [Fact]
    public async Task Only_failed_raw_imports_can_be_retried_and_retry_uses_a_new_object()
    {
        _t.CurrentUser.SignInAs(await _t.AddUserAsync(UserRole.DataEngineer));
        var service = Service();
        var accepted = await service.StartRawJsonImportAsync(File("batch.json", RawTelemetryJsonTests.Valid), default);
        await Assert.ThrowsAsync<BusinessRuleException>(() => service.RetryImportAsync(accepted.Id, default));   // still Queued

        var import = await _t.Db.TelemetryImports.SingleAsync();
        var original = import.StoredPath;
        import.Status = TelemetryImportStatus.Failed;
        import.ErrorMessage = "ETL error";
        await _t.Db.SaveChangesAsync();

        var retried = await service.RetryImportAsync(accepted.Id, default);
        Assert.Equal(TelemetryImportStatus.Queued, retried.Status);
        Assert.Null(retried.ErrorMessage);
        var reloaded = await _t.Db.TelemetryImports.SingleAsync();
        Assert.NotEqual(original, reloaded.StoredPath);
        Assert.Contains("-retry-", reloaded.StoredPath);
        Assert.True(_store.Objects.ContainsKey(original));   // raw object preserved
    }

    [Fact]
    public async Task Csv_processor_ignores_raw_json_imports()
    {
        var user = await _t.AddUserAsync(UserRole.DataEngineer);
        var import = new TelemetryImport
        {
            FileName = "x.json", FileSha256 = new string('d', 64), StoredPath = "s3://bronze/telemetry/x.json",
            Source = TelemetryImportSource.RawJson, RequestedByUserId = user.Id, RequestedAtUtc = TestContext.Now,
        };
        _t.Db.TelemetryImports.Add(import);
        await _t.Db.SaveChangesAsync();
        await new TelemetryImportProcessor(_t.Telemetry, _t.Aircraft, _t.Runs, new LocalImportFileStoreStub(), _t.UnitOfWork, _t.Clock,
            Options.Create(new TelemetryImportOptions()), NullLogger<TelemetryImportProcessor>.Instance).ProcessAsync(import.Id, default);
        Assert.Equal(TelemetryImportStatus.Queued, (await _t.Db.TelemetryImports.SingleAsync()).Status);
        Assert.Empty(await _t.Db.PipelineRuns.ToListAsync());
    }

    private sealed class NoopQueue : ITelemetryImportQueue
    {
        public ValueTask EnqueueAsync(Guid importId, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class LocalImportFileStoreStub : IImportFileStore
    {
        public Task<string> SaveAsync(Guid importId, string originalFileName, Stream content, CancellationToken ct) => throw new NotSupportedException();
        public Stream OpenRead(string storedPath) => throw new NotSupportedException();
        public void Delete(string storedPath) { }
    }
}
