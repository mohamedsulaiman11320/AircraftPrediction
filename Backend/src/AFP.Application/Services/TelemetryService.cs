using System.Security.Cryptography;
using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Application.Contracts;
using AFP.Application.Mapping;
using AFP.Application.Telemetry;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using Microsoft.Extensions.Options;

namespace AFP.Application.Services;

public sealed record UploadedFile(string FileName, long Length, Stream Content);

public interface ITelemetryService
{
    Task<PagedResult<TelemetryRecordDto>> QueryAsync(TelemetryFilter filter, PageRequest page, CancellationToken ct);
    Task<TelemetrySeriesDto> GetSeriesAsync(Guid aircraftId, DateTime? fromUtc, DateTime? toUtc, string? sensorId, int bucketMinutes, CancellationToken ct);
    Task<JobAcceptedDto> StartImportAsync(UploadedFile file, CancellationToken ct);

    /// <summary>Stores a raw JSON file in MinIO bronze/telemetry for the Spark ETL and tracks it as a Queued import.</summary>
    Task<JobAcceptedDto> StartRawJsonImportAsync(UploadedFile file, CancellationToken ct);

    /// <summary>Re-submits a failed raw JSON import to the ETL (server-side copy; inserts are idempotent).</summary>
    Task<TelemetryImportDto> RetryImportAsync(Guid id, CancellationToken ct);
    Task<PagedResult<TelemetryImportDto>> ListImportsAsync(PageRequest page, CancellationToken ct);
    Task<TelemetryImportDto> GetImportAsync(Guid id, CancellationToken ct);
    Task<PagedResult<RejectedRowDto>> ListRejectedRowsAsync(Guid importId, PageRequest page, CancellationToken ct);
    Task<DataQualityReportDto> GetDataQualityAsync(Guid? aircraftId, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct);
}

public sealed class TelemetryService(
    ITelemetryRepository telemetry,
    IAircraftRepository aircraft,
    IImportFileStore fileStore,
    IRawTelemetryStore rawStore,
    ITelemetryImportQueue queue,
    IActionAuditor auditor,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClock clock,
    IOptions<TelemetryImportOptions> options,
    IOptions<RawTelemetryStorageOptions> rawOptions) : ITelemetryService
{
    public const int MaxSeriesDays = 120;

    public async Task<PagedResult<TelemetryRecordDto>> QueryAsync(TelemetryFilter filter, PageRequest page, CancellationToken ct)
    {
        ValidateRange(filter.FromUtc, filter.ToUtc);
        _ = await aircraft.GetByIdAsync(filter.AircraftId, ct) ?? throw new NotFoundException("Aircraft", filter.AircraftId);
        return (await telemetry.QueryAsync(filter, page.Normalize(1000), ct)).Map(t => t.ToDto());
    }

    public async Task<TelemetrySeriesDto> GetSeriesAsync(
        Guid aircraftId, DateTime? fromUtc, DateTime? toUtc, string? sensorId, int bucketMinutes, CancellationToken ct)
    {
        var plane = await aircraft.GetByIdAsync(aircraftId, ct) ?? throw new NotFoundException("Aircraft", aircraftId);
        ValidateRange(fromUtc, toUtc);
        var to = toUtc ?? plane.LastTelemetryAtUtc ?? clock.UtcNow;
        var from = fromUtc ?? to.AddDays(-7);
        if ((to - from).TotalDays > MaxSeriesDays)
            throw new ValidationException("fromUtc", $"The date range may not exceed {MaxSeriesDays} days.");
        bucketMinutes = Math.Clamp(bucketMinutes <= 0 ? 60 : bucketMinutes, 1, 1440);

        var points = await telemetry.GetPointsAsync(aircraftId, sensorId, from, to, ct);
        var bucketTicks = TimeSpan.FromMinutes(bucketMinutes).Ticks;
        var series = points
            .GroupBy(p => p.RecordedAtUtc.Ticks / bucketTicks)
            .OrderBy(g => g.Key)
            .Select(g => new TelemetrySeriesPointDto(
                new DateTime(g.Key * bucketTicks, DateTimeKind.Utc),
                Average(g.Select(p => p.TemperatureC)),
                Average(g.Select(p => p.PressureKpa)),
                g.Count(),
                // Converted once, here: clients must not re-derive PSI.
                TelemetryRowValidator.KpaToPsi(Average(g.Select(p => p.PressureKpa))),
                Average(g.Select(p => p.VibrationIndex))))
            .ToList();
        return new TelemetrySeriesDto(aircraftId, from, to, bucketMinutes, series);
    }

    public async Task<JobAcceptedDto> StartImportAsync(UploadedFile file, CancellationToken ct)
    {
        var opts = options.Value;
        var fileName = Path.GetFileName(file.FileName ?? string.Empty);
        new Validator()
            .Must(fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase), "file", "Only .csv files can be imported.")
            .Must(file.Length > 0, "file", "The file is empty.")
            .Must(file.Length <= opts.MaxFileBytes, "file", $"The file exceeds the {opts.MaxFileBytes / (1024 * 1024)} MB limit.")
            .MaxLength("file", fileName, 255, "File name")
            .ThrowIfInvalid();

        var importId = Guid.NewGuid();
        var storedPath = await fileStore.SaveAsync(importId, fileName, file.Content, ct);
        string sha256;
        string? header;
        await using (var stream = fileStore.OpenRead(storedPath))
        {
            sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            stream.Position = 0;
            using var reader = new StreamReader(stream, leaveOpen: true);
            header = await reader.ReadLineAsync(ct);
        }

        var (validator, missing) = header is null
            ? (null, TelemetryColumns.Required)
            : TelemetryRowValidator.FromHeader(header, clock.UtcNow);
        if (validator is null)
        {
            fileStore.Delete(storedPath);
            throw new ValidationException("file", $"Missing required column(s): {string.Join(", ", missing)}.");
        }

        var existing = await telemetry.GetImportByHashAsync(sha256, ct);
        if (existing is not null)
        {
            fileStore.Delete(storedPath);
            throw new ConflictException(
                $"This exact file was already imported on {existing.RequestedAtUtc:u} (import {existing.Id}).", "duplicate_file");
        }

        var import = new TelemetryImport
        {
            Id = importId,
            FileName = fileName,
            FileSha256 = sha256,
            StoredPath = storedPath,
            FileSizeBytes = file.Length,
            Status = TelemetryImportStatus.Queued,
            RequestedByUserId = currentUser.RequireUserId(),
            RequestedAtUtc = clock.UtcNow,
        };
        await telemetry.AddImportAsync(import, ct);
        await auditor.RecordAsync("TelemetryImportQueued", nameof(TelemetryImport), import.Id.ToString(),
            new { fileName, sha256, bytes = file.Length }, ct);
        await unitOfWork.SaveChangesAsync(ct);
        await queue.EnqueueAsync(import.Id, ct);
        return new JobAcceptedDto(import.Id, import.Status.ToString(), $"/api/v1/telemetry/imports/{import.Id}");
    }

    public async Task<JobAcceptedDto> StartRawJsonImportAsync(UploadedFile file, CancellationToken ct)
    {
        var opts = rawOptions.Value;
        var fileName = Path.GetFileName(file.FileName ?? string.Empty);
        new Validator()
            .Must(fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase), "file", "Only .json files can be uploaded as raw telemetry.")
            .Must(file.Length > 0, "file", "The file is empty.")
            .Must(file.Length <= opts.MaxFileBytes, "file", $"The file exceeds the {opts.MaxFileBytes / (1024 * 1024)} MB limit.")
            .MaxLength("file", fileName, 255, "File name")
            .ThrowIfInvalid();
        if (!opts.IsConfigured)
            throw new BusinessRuleException("Raw telemetry storage (MinIO) is not configured on the server.", "storage_not_configured");

        await using var buffer = new MemoryStream(capacity: (int)file.Length);
        await file.Content.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        var check = RawTelemetryJson.Check(buffer, opts.RequiredRecordKeys);
        if (!check.IsValid) throw new ValidationException(new Dictionary<string, string[]> { ["file"] = check.Errors.ToArray() });

        buffer.Position = 0;
        var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(buffer, ct));
        if (await telemetry.GetImportByHashAsync(sha256, ct) is { } existing)
            throw new ConflictException(
                $"This exact file was already imported on {existing.RequestedAtUtc:u} (import {existing.Id}).", "duplicate_file");

        var now = clock.UtcNow;
        var import = new TelemetryImport
        {
            FileName = fileName,
            FileSha256 = sha256,
            Source = TelemetryImportSource.RawJson,
            Status = TelemetryImportStatus.Queued,
            FileSizeBytes = file.Length,
            TotalRows = check.RecordCount,
            RequestedByUserId = currentUser.RequireUserId(),
            RequestedAtUtc = now,
        };
        buffer.Position = 0;
        // A fresh, import-specific key: raw objects are never overwritten.
        import.StoredPath = await rawStore.PutAsync($"{now:yyyy}/{now:MM}/{import.Id:N}.json", buffer, buffer.Length, "application/json", ct);
        await telemetry.AddImportAsync(import, ct);
        await auditor.RecordAsync("RawTelemetryUploaded", nameof(TelemetryImport), import.Id.ToString(),
            new { fileName, sha256, bytes = file.Length, records = check.RecordCount, location = import.StoredPath }, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return new JobAcceptedDto(import.Id, import.Status.ToString(), $"/api/v1/telemetry/imports/{import.Id}");
    }

    public async Task<TelemetryImportDto> RetryImportAsync(Guid id, CancellationToken ct)
    {
        var import = await telemetry.GetImportAsync(id, ct) ?? throw new NotFoundException("Telemetry import", id);
        if (import.Source != TelemetryImportSource.RawJson || import.Status != TelemetryImportStatus.Failed)
            throw new BusinessRuleException("Only failed raw JSON imports can be retried.", "invalid_transition");

        var now = clock.UtcNow;
        var previous = import.StoredPath;
        // The ETL tracks files it has seen, so a retry is a new object (server-side copy) linked to the same import.
        import.StoredPath = await rawStore.CopyAsync(previous, $"{now:yyyy}/{now:MM}/{import.Id:N}-retry-{now:yyyyMMddHHmmss}.json", ct);
        import.Status = TelemetryImportStatus.Queued;
        import.ErrorMessage = null;
        import.StartedAtUtc = null;
        import.CompletedAtUtc = null;
        import.AcceptedRows = import.RejectedRows = import.DuplicateRows = import.WarningCount = 0;
        import.Warnings = [];
        await auditor.RecordAsync("RawTelemetryRetried", nameof(TelemetryImport), import.Id.ToString(),
            new { from = previous, to = import.StoredPath }, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return import.ToDto();
    }

    public async Task<PagedResult<TelemetryImportDto>> ListImportsAsync(PageRequest page, CancellationToken ct) =>
        (await telemetry.ListImportsAsync(page.Normalize(), ct)).Map(i => i.ToDto());

    public async Task<TelemetryImportDto> GetImportAsync(Guid id, CancellationToken ct) =>
        (await telemetry.GetImportAsync(id, ct) ?? throw new NotFoundException("Telemetry import", id)).ToDto();

    public async Task<PagedResult<RejectedRowDto>> ListRejectedRowsAsync(Guid importId, PageRequest page, CancellationToken ct)
    {
        _ = await telemetry.GetImportAsync(importId, ct) ?? throw new NotFoundException("Telemetry import", importId);
        return (await telemetry.ListRejectedRowsAsync(importId, page.Normalize(), ct))
            .Map(r => new RejectedRowDto(r.RowNumber, r.RawLine, r.Reasons));
    }

    public async Task<DataQualityReportDto> GetDataQualityAsync(Guid? aircraftId, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct)
    {
        ValidateRange(fromUtc, toUtc);
        var totals = await telemetry.GetQualityTotalsAsync(aircraftId, fromUtc, toUtc, ct);
        var byAircraft = await telemetry.GetQualityByAircraftAsync(aircraftId, fromUtc, toUtc, ct);
        var imports = await telemetry.GetImportTotalsAsync(ct);
        return new DataQualityReportDto(
            totals.Total, totals.Valid, totals.Suspect, totals.MissingTemperature, totals.MissingPressure, totals.First, totals.Last,
            byAircraft.Select(a => new AircraftDataQualityDto(a.AircraftId, a.RegistrationNumber, a.Records, a.Suspect, a.First, a.Last)).ToList(),
            new ImportTotalsDto(imports.Count, imports.Accepted, imports.Rejected, imports.Duplicates));
    }

    private static void ValidateRange(DateTime? from, DateTime? to)
    {
        if (from.HasValue && to.HasValue && from > to) throw new ValidationException("fromUtc", "fromUtc must be before toUtc.");
    }

    private static double? Average(IEnumerable<double?> values)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return present.Count == 0 ? null : Math.Round(present.Average(), 3);
    }
}
