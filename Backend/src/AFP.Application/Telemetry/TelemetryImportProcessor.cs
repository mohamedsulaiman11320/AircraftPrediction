using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AFP.Application.Telemetry;

public interface ITelemetryImportProcessor
{
    Task ProcessAsync(Guid importId, CancellationToken ct);
}

/// <summary>
/// Validates and loads a queued CSV import. All accepted rows are inserted in one transaction, so a
/// failed import leaves no partial telemetry behind. The original file is never modified.
/// </summary>
public sealed class TelemetryImportProcessor(
    ITelemetryRepository telemetry,
    IAircraftRepository aircraftRepo,
    IPipelineRunRepository pipelineRuns,
    IImportFileStore fileStore,
    IUnitOfWork unitOfWork,
    IClock clock,
    IOptions<TelemetryImportOptions> options,
    ILogger<TelemetryImportProcessor> logger) : ITelemetryImportProcessor
{
    private const int MaxRawLineLength = 2000;

    public async Task ProcessAsync(Guid importId, CancellationToken ct)
    {
        var import = await telemetry.GetImportAsync(importId, ct);
        // Raw JSON imports are processed by the Spark ETL, not by this CSV processor.
        if (import is null || import.Status != TelemetryImportStatus.Queued || import.Source != TelemetryImportSource.Csv) return;

        var run = new PipelineRun
        {
            JobType = PipelineJobType.TelemetryImport,
            Status = PipelineRunStatus.Running,
            Trigger = PipelineTrigger.Api,
            RequestedByUserId = import.RequestedByUserId,
            RequestedAtUtc = import.RequestedAtUtc,
            StartedAtUtc = clock.UtcNow,
            Parameters = Json.Serialize(new { importId, fileName = import.FileName, fileSha256 = import.FileSha256 }),
            WorkerHost = Environment.MachineName,
        };
        import.Status = TelemetryImportStatus.Processing;
        import.StartedAtUtc = clock.UtcNow;
        await pipelineRuns.AddAsync(run, ct);
        await unitOfWork.SaveChangesAsync(ct);
        var runId = run.Id;

        try
        {
            var stats = await unitOfWork.ExecuteInTransactionAsync(innerCt => LoadAsync(import, innerCt), ct);
            telemetry.ClearTracking();

            var final = (await telemetry.GetImportAsync(importId, ct))!;
            final.Status = TelemetryImportStatus.Completed;
            final.TotalRows = stats.Total;
            final.AcceptedRows = stats.Accepted;
            final.RejectedRows = stats.Rejected;
            final.DuplicateRows = stats.Duplicates;
            final.Warnings = stats.Warnings;
            final.WarningCount = stats.Warnings.Count;
            final.CompletedAtUtc = clock.UtcNow;

            var finalRun = (await pipelineRuns.GetAsync(runId, ct))!;
            finalRun.Status = PipelineRunStatus.Succeeded;
            finalRun.CompletedAtUtc = clock.UtcNow;
            finalRun.RecordsProcessed = stats.Total;
            finalRun.ResultSummary = Json.Serialize(new
            {
                total_rows = stats.Total, accepted_rows = stats.Accepted, rejected_rows = stats.Rejected,
                duplicate_rows = stats.Duplicates, warnings = stats.Warnings,
            });
            await unitOfWork.SaveChangesAsync(ct);
            logger.LogInformation("Telemetry import {ImportId} completed: {Accepted}/{Total} accepted", importId, stats.Accepted, stats.Total);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Telemetry import {ImportId} failed", importId);
            telemetry.ClearTracking();
            var failed = (await telemetry.GetImportAsync(importId, CancellationToken.None))!;
            failed.Status = TelemetryImportStatus.Failed;
            failed.ErrorMessage = Truncate(ex.Message, 1000);
            failed.CompletedAtUtc = clock.UtcNow;
            var failedRun = await pipelineRuns.GetAsync(runId, CancellationToken.None);
            if (failedRun is not null)
            {
                failedRun.Status = PipelineRunStatus.Failed;
                failedRun.ErrorMessage = failed.ErrorMessage;
                failedRun.CompletedAtUtc = clock.UtcNow;
            }
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
        }
    }

    private sealed record LoadStats(int Total, int Accepted, int Rejected, int Duplicates, List<string> Warnings);

    private async Task<LoadStats> LoadAsync(TelemetryImport import, CancellationToken ct)
    {
        var opts = options.Value;
        var registrations = await aircraftRepo.GetRegistrationMapAsync(ct);
        var seenKeys = new HashSet<TelemetryKey>();
        var latestByAircraft = new Dictionary<Guid, DateTime>();
        var unknownRegistrations = new SortedSet<string>(StringComparer.Ordinal);
        var batch = new List<(ParsedTelemetryRow Row, Guid AircraftId)>(opts.BatchSize);
        var rejectedBuffer = new List<TelemetryRejectedRow>();
        int total = 0, accepted = 0, rejected = 0, duplicates = 0, storedRejected = 0;
        int missingTemp = 0, missingPressure = 0, convertedTemp = 0, convertedPressure = 0;

        await using var stream = fileStore.OpenRead(import.StoredPath);
        using var reader = new StreamReader(stream);
        var header = await reader.ReadLineAsync(ct) ?? throw new InvalidOperationException("The file has no header row.");
        var (validator, missing) = TelemetryRowValidator.FromHeader(
            header, clock.UtcNow.AddMinutes(opts.MaxFutureSkewMinutes));
        if (validator is null) throw new InvalidOperationException($"Missing required column(s): {string.Join(", ", missing)}.");

        var lineNumber = 1;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            total++;

            var result = validator.Validate(lineNumber, line);
            List<string>? reasons = result.Row is null ? [.. result.Errors] : null;
            Guid aircraftId = default;
            if (result.Row is { } row)
            {
                if (!registrations.TryGetValue(row.Registration, out aircraftId))
                {
                    unknownRegistrations.Add(row.Registration);
                    reasons = [$"unknown aircraft registration '{row.Registration}'"];
                }
                else if (!seenKeys.Add(new TelemetryKey(aircraftId, row.SensorId, row.RecordedAtUtc)))
                {
                    duplicates++;
                    reasons = ["duplicate of an earlier row in this file"];
                }
            }

            if (reasons is not null)
            {
                rejected++;
                if (storedRejected < opts.MaxStoredRejectedRows)
                {
                    storedRejected++;
                    rejectedBuffer.Add(new TelemetryRejectedRow
                    {
                        ImportId = import.Id, RowNumber = lineNumber,
                        RawLine = Truncate(line, MaxRawLineLength), Reasons = Truncate(string.Join("; ", reasons), 1000),
                    });
                }
                continue;
            }

            batch.Add((result.Row!, aircraftId));
            if (batch.Count >= opts.BatchSize)
            {
                var (inserted, dup) = await FlushAsync(import, batch, rejectedBuffer, latestByAircraft, () => storedRejected++, () => storedRejected < opts.MaxStoredRejectedRows, ct);
                accepted += inserted.Count; duplicates += dup; rejected += dup;
                CountFlags(inserted);
                batch.Clear();
            }
        }

        if (batch.Count > 0 || rejectedBuffer.Count > 0)
        {
            var (inserted, dup) = await FlushAsync(import, batch, rejectedBuffer, latestByAircraft, () => storedRejected++, () => storedRejected < opts.MaxStoredRejectedRows, ct);
            accepted += inserted.Count; duplicates += dup; rejected += dup;
            CountFlags(inserted);
        }

        await aircraftRepo.UpdateLastTelemetryAsync(latestByAircraft, ct);
        await unitOfWork.SaveChangesAsync(ct);

        var warnings = new List<string>();
        if (total == 0) warnings.Add("The file contains a header but no data rows.");
        if (missingTemp > 0) warnings.Add($"{missingTemp} accepted row(s) have no temperature value (quality: Suspect).");
        if (missingPressure > 0) warnings.Add($"{missingPressure} accepted row(s) have no pressure value (quality: Suspect).");
        if (duplicates > 0) warnings.Add($"{duplicates} duplicate row(s) were skipped.");
        if (unknownRegistrations.Count > 0)
            warnings.Add($"Rows for unregistered aircraft were rejected: {string.Join(", ", unknownRegistrations.Take(20))}" +
                         (unknownRegistrations.Count > 20 ? " …" : "") + ". Register the aircraft and re-import.");
        if (convertedTemp > 0) warnings.Add($"{convertedTemp} temperature value(s) were converted to °C (originals preserved).");
        if (convertedPressure > 0) warnings.Add($"{convertedPressure} pressure value(s) were converted to kPa (originals preserved).");
        if (rejected > storedRejected) warnings.Add($"Only the first {storedRejected} rejected rows are stored for review.");
        if (total > 0 && rejected * 10 > total) warnings.Add($"High rejection rate: {rejected} of {total} rows ({rejected * 100.0 / total:F1}%).");

        return new LoadStats(total, accepted, rejected, duplicates, warnings);

        void CountFlags(List<ParsedTelemetryRow> rows)
        {
            foreach (var r in rows)
            {
                if (r.QualityFlags.Contains(QualityFlags.MissingTemperature)) missingTemp++;
                if (r.QualityFlags.Contains(QualityFlags.MissingPressure)) missingPressure++;
                if (r.TemperatureUnit is not null and not "C") convertedTemp++;
                if (r.PressureUnit is not null and not "kPa") convertedPressure++;
            }
        }
    }

    /// <summary>Inserts a batch, skipping rows that already exist in the database. Returns (inserted rows, dbDuplicates).</summary>
    private async Task<(List<ParsedTelemetryRow> Inserted, int Duplicates)> FlushAsync(
        TelemetryImport import, List<(ParsedTelemetryRow Row, Guid AircraftId)> batch, List<TelemetryRejectedRow> rejectedBuffer,
        Dictionary<Guid, DateTime> latestByAircraft, Action onStoredRejected, Func<bool> canStoreRejected, CancellationToken ct)
    {
        var inserted = new List<ParsedTelemetryRow>(batch.Count);
        var duplicates = 0;
        if (batch.Count > 0)
        {
            var aircraftIds = batch.Select(b => b.AircraftId).Distinct().ToList();
            var existing = await telemetry.GetExistingKeysAsync(
                aircraftIds, batch.Min(b => b.Row.RecordedAtUtc), batch.Max(b => b.Row.RecordedAtUtc), ct);
            var now = clock.UtcNow;
            var records = new List<TelemetryRecord>(batch.Count);
            foreach (var (row, aircraftId) in batch)
            {
                if (existing.Contains(new TelemetryKey(aircraftId, row.SensorId, row.RecordedAtUtc)))
                {
                    duplicates++;
                    if (canStoreRejected())
                    {
                        onStoredRejected();
                        rejectedBuffer.Add(new TelemetryRejectedRow
                        {
                            ImportId = import.Id, RowNumber = row.RowNumber,
                            RawLine = $"{row.Registration},{row.SensorId},{row.RecordedAtUtc:O}",
                            Reasons = "duplicate of telemetry already stored",
                        });
                    }
                    continue;
                }
                records.Add(new TelemetryRecord
                {
                    AircraftId = aircraftId,
                    SensorId = row.SensorId,
                    RecordedAtUtc = row.RecordedAtUtc,
                    TemperatureValue = row.TemperatureValue,
                    TemperatureUnit = row.TemperatureUnit,
                    TemperatureC = row.TemperatureC is { } t ? Math.Round(t, 4) : null,
                    PressureValue = row.PressureValue,
                    PressureUnit = row.PressureUnit,
                    PressureKpa = row.PressureKpa is { } p ? Math.Round(p, 4) : null,
                    DataSource = row.DataSource,
                    QualityStatus = row.QualityFlags.Count > 0 ? QualityStatus.Suspect : QualityStatus.Valid,
                    QualityFlags = row.QualityFlags.Count > 0 ? string.Join(',', row.QualityFlags) : null,
                    IngestedAtUtc = now,
                    ImportId = import.Id,
                    SourceRowNumber = row.RowNumber,
                });
                if (!latestByAircraft.TryGetValue(aircraftId, out var latest) || row.RecordedAtUtc > latest)
                    latestByAircraft[aircraftId] = row.RecordedAtUtc;
                inserted.Add(row);
            }
            await telemetry.AddRangeAsync(records, ct);
        }
        if (rejectedBuffer.Count > 0)
        {
            await telemetry.AddRejectedRowsAsync(rejectedBuffer.ToList(), ct);
            rejectedBuffer.Clear();
        }
        await unitOfWork.SaveChangesAsync(ct);
        telemetry.ClearTracking();
        return (inserted, duplicates);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
