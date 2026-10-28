using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Application.Telemetry;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace AFP.Infrastructure.Persistence.Repositories;

public sealed class TelemetryRepository(AppDbContext db) : ITelemetryRepository
{
    public Task<PagedResult<TelemetryRecord>> QueryAsync(TelemetryFilter f, PageRequest page, CancellationToken ct) =>
        Filter(db.TelemetryRecords.AsNoTracking(), f.AircraftId, f.FromUtc, f.ToUtc, f.SensorId, f.QualityStatus)
            .OrderByDescending(t => t.RecordedAtUtc).ThenBy(t => t.SensorId).ToPagedAsync(page, ct);

    public async Task<IReadOnlyList<TelemetryPoint>> GetPointsAsync(Guid aircraftId, string? sensorId, DateTime fromUtc, DateTime toUtc, CancellationToken ct) =>
        await Filter(db.TelemetryRecords.AsNoTracking(), aircraftId, fromUtc, toUtc, sensorId, null)
            .OrderBy(t => t.RecordedAtUtc)
            .Select(t => new TelemetryPoint(t.RecordedAtUtc, t.TemperatureC, t.PressureKpa, t.VibrationIndex))
            .ToListAsync(ct);

    public Task<TelemetryRecord?> GetByIdAsync(long id, CancellationToken ct) =>
        db.TelemetryRecords.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<HashSet<TelemetryKey>> GetExistingKeysAsync(IReadOnlyCollection<Guid> aircraftIds, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var ids = aircraftIds.ToList();
        var keys = await db.TelemetryRecords.AsNoTracking()
            .Where(t => ids.Contains(t.AircraftId) && t.RecordedAtUtc >= fromUtc && t.RecordedAtUtc <= toUtc)
            .Select(t => new { t.AircraftId, t.SensorId, t.RecordedAtUtc })
            .ToListAsync(ct);
        return keys.Select(k => new TelemetryKey(k.AircraftId, k.SensorId, k.RecordedAtUtc)).ToHashSet();
    }

    public Task AddRangeAsync(IReadOnlyCollection<TelemetryRecord> records, CancellationToken ct) => db.TelemetryRecords.AddRangeAsync(records, ct);

    public async Task<TelemetryQualityTotals> GetQualityTotalsAsync(Guid? aircraftId, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct)
    {
        var totals = await Filter(db.TelemetryRecords.AsNoTracking(), aircraftId, fromUtc, toUtc, null, null)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Valid = g.Count(t => t.QualityStatus == QualityStatus.Valid),
                Suspect = g.Count(t => t.QualityStatus == QualityStatus.Suspect),
                MissingTemperature = g.Count(t => t.TemperatureValue == null),
                MissingPressure = g.Count(t => t.PressureValue == null),
                First = g.Min(t => (DateTime?)t.RecordedAtUtc),
                Last = g.Max(t => (DateTime?)t.RecordedAtUtc),
            })
            .FirstOrDefaultAsync(ct);
        return totals is null
            ? new TelemetryQualityTotals(0, 0, 0, 0, 0, null, null)
            : new TelemetryQualityTotals(totals.Total, totals.Valid, totals.Suspect, totals.MissingTemperature, totals.MissingPressure, totals.First, totals.Last);
    }

    public async Task<IReadOnlyList<AircraftTelemetryQuality>> GetQualityByAircraftAsync(Guid? aircraftId, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct)
    {
        var rows = await Filter(db.TelemetryRecords.AsNoTracking(), aircraftId, fromUtc, toUtc, null, null)
            .GroupBy(t => t.AircraftId)
            .Select(g => new
            {
                AircraftId = g.Key,
                Records = g.Count(),
                Suspect = g.Count(t => t.QualityStatus == QualityStatus.Suspect),
                First = g.Min(t => t.RecordedAtUtc),
                Last = g.Max(t => t.RecordedAtUtc),
            })
            .ToListAsync(ct);
        var ids = rows.Select(r => r.AircraftId).ToList();
        var names = await db.Aircraft.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.RegistrationNumber, ct);
        return rows.Select(r => new AircraftTelemetryQuality(r.AircraftId, names.GetValueOrDefault(r.AircraftId, "Unknown"), r.Records, r.Suspect, r.First, r.Last))
            .OrderBy(r => r.RegistrationNumber).ToList();
    }

    public Task<TelemetryImport?> GetImportAsync(Guid id, CancellationToken ct) =>
        db.TelemetryImports.Include(i => i.RequestedBy).FirstOrDefaultAsync(i => i.Id == id, ct);

    public Task<TelemetryImport?> GetImportByHashAsync(string sha256, CancellationToken ct) =>
        db.TelemetryImports.AsNoTracking().FirstOrDefaultAsync(i => i.FileSha256 == sha256, ct);

    public Task<PagedResult<TelemetryImport>> ListImportsAsync(PageRequest page, CancellationToken ct) =>
        db.TelemetryImports.AsNoTracking().Include(i => i.RequestedBy).OrderByDescending(i => i.RequestedAtUtc).ToPagedAsync(page, ct);

    public async Task<IReadOnlyList<TelemetryImport>> ListImportsByStatusAsync(TelemetryImportStatus status, CancellationToken ct) =>
        await db.TelemetryImports.AsNoTracking().Where(i => i.Status == status).OrderBy(i => i.RequestedAtUtc).ToListAsync(ct);

    public async Task AddImportAsync(TelemetryImport import, CancellationToken ct) => await db.TelemetryImports.AddAsync(import, ct);

    public Task AddRejectedRowsAsync(IReadOnlyCollection<TelemetryRejectedRow> rows, CancellationToken ct) => db.TelemetryRejectedRows.AddRangeAsync(rows, ct);

    public Task<PagedResult<TelemetryRejectedRow>> ListRejectedRowsAsync(Guid importId, PageRequest page, CancellationToken ct) =>
        db.TelemetryRejectedRows.AsNoTracking().Where(r => r.ImportId == importId).OrderBy(r => r.RowNumber).ToPagedAsync(page, ct);

    public async Task<(int Count, int Accepted, int Rejected, int Duplicates)> GetImportTotalsAsync(CancellationToken ct)
    {
        var totals = await db.TelemetryImports.AsNoTracking().Where(i => i.Status == TelemetryImportStatus.Completed)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Accepted = g.Sum(i => i.AcceptedRows), Rejected = g.Sum(i => i.RejectedRows), Duplicates = g.Sum(i => i.DuplicateRows) })
            .FirstOrDefaultAsync(ct);
        return totals is null ? (0, 0, 0, 0) : (totals.Count, totals.Accepted, totals.Rejected, totals.Duplicates);
    }

    public async Task<IReadOnlyDictionary<Guid, AircraftHealthSnapshot>> GetHealthSnapshotsAsync(
        IReadOnlyCollection<Guid> aircraftIds, TimeSpan window, CancellationToken ct)
    {
        if (aircraftIds.Count == 0) return new Dictionary<Guid, AircraftHealthSnapshot>();
        var ids = aircraftIds.Distinct().ToList();

        // Each aircraft's window ends at ITS latest reading (telemetry arrives in a daily batch); staleness of that
        // reading is reported separately by the caller.
        var latestTimes = await db.TelemetryRecords.AsNoTracking()
            .Where(r => ids.Contains(r.AircraftId))
            .GroupBy(r => r.AircraftId)
            .Select(g => new { AircraftId = g.Key, LatestAtUtc = g.Max(r => r.RecordedAtUtc) })
            .ToDictionaryAsync(t => t.AircraftId, t => t.LatestAtUtc, ct);

        var result = new Dictionary<Guid, AircraftHealthSnapshot>();
        foreach (var id in ids)
        {
            if (!latestTimes.TryGetValue(id, out var latestAt))
            {
                result[id] = new AircraftHealthSnapshot(id, null, null, null, null, null, null, null, 0);
                continue;
            }
            var from = latestAt - window;
            // Extremes over the window drive the threshold comparison; a single breach anywhere in it counts.
            // One small query per aircraft keeps this provider-agnostic; fine for a fleet of this size.
            var inWindow = db.TelemetryRecords.AsNoTracking().Where(r => r.AircraftId == id && r.RecordedAtUtc > from);
            var readings = await inWindow.CountAsync(ct);
            var minPressureKpa = await inWindow.MinAsync(r => r.PressureKpa, ct);
            var maxTemperatureC = await inWindow.MaxAsync(r => r.TemperatureC, ct);
            var latest = await db.TelemetryRecords.AsNoTracking()
                .Where(r => r.AircraftId == id && r.RecordedAtUtc == latestAt)
                .OrderBy(r => r.Id).FirstAsync(ct);
            result[id] = new AircraftHealthSnapshot(
                id, latest.RecordedAtUtc, latest.TemperatureC, TelemetryRowValidator.KpaToPsi(latest.PressureKpa),
                latest.VibrationIndex, TelemetryRowValidator.KpaToPsi(minPressureKpa), maxTemperatureC, from, readings);
        }
        return result;
    }

    public Task<int> CountRecordsForImportAsync(Guid importId, CancellationToken ct) =>
        db.TelemetryRecords.CountAsync(r => r.ImportId == importId, ct);

    public Task<int> CountRejectedRowsForImportAsync(Guid importId, CancellationToken ct) =>
        db.TelemetryRejectedRows.CountAsync(r => r.ImportId == importId, ct);

    public Task<int> CountImportsForLakeBatchAsync(long lakeBatchId, CancellationToken ct) =>
        db.TelemetryImports.CountAsync(i => i.LakeBatchId == lakeBatchId, ct);

    public async Task<int> DeleteRecordsForImportAsync(Guid importId, CancellationToken ct)
    {
        var rows = await db.TelemetryRecords.Where(r => r.ImportId == importId).ToListAsync(ct);
        db.TelemetryRecords.RemoveRange(rows);
        return rows.Count;
    }

    public async Task<int> DeleteRejectedRowsForImportAsync(Guid importId, CancellationToken ct)
    {
        var rows = await db.TelemetryRejectedRows.Where(r => r.ImportId == importId).ToListAsync(ct);
        db.TelemetryRejectedRows.RemoveRange(rows);
        return rows.Count;
    }

    public async Task DeleteImportAsync(Guid importId, CancellationToken ct)
    {
        var import = await db.TelemetryImports.FirstOrDefaultAsync(i => i.Id == importId, ct);
        if (import is not null) db.TelemetryImports.Remove(import);
    }

    public void ClearTracking() => db.ChangeTracker.Clear();

    private static IQueryable<TelemetryRecord> Filter(
        IQueryable<TelemetryRecord> q, Guid? aircraftId, DateTime? from, DateTime? to, string? sensorId, QualityStatus? quality)
    {
        if (aircraftId is { } id) q = q.Where(t => t.AircraftId == id);
        if (from is { } f) q = q.Where(t => t.RecordedAtUtc >= f);
        if (to is { } u) q = q.Where(t => t.RecordedAtUtc <= u);
        if (!string.IsNullOrWhiteSpace(sensorId)) q = q.Where(t => t.SensorId == sensorId);
        if (quality is { } qs) q = q.Where(t => t.QualityStatus == qs);
        return q;
    }
}

public sealed class AnomalyRepository(AppDbContext db) : IAnomalyRepository
{
    private static readonly InvestigationStatus[] Open =
        [InvestigationStatus.New, InvestigationStatus.Acknowledged, InvestigationStatus.Investigating];

    private IQueryable<AircraftAnomalyResult> Query => db.AnomalyResults
        .Include(a => a.Aircraft).Include(a => a.ModelVersion).Include(a => a.AcknowledgedBy);

    public Task<AircraftAnomalyResult?> GetAsync(long id, CancellationToken ct) => Query.FirstOrDefaultAsync(a => a.Id == id, ct);

    public Task<PagedResult<AircraftAnomalyResult>> ListAsync(AnomalyFilter f, PageRequest page, CancellationToken ct)
    {
        var q = Query.AsNoTracking().Where(a => a.IsAnomaly);
        if (f.AircraftId is { } aircraftId) q = q.Where(a => a.AircraftId == aircraftId);
        if (f.Status is { } status) q = q.Where(a => a.InvestigationStatus == status);
        if (f.Severity is { } severity) q = q.Where(a => a.Severity == severity);
        if (f.FromUtc is { } from) q = q.Where(a => a.ObservedAtUtc >= from);
        if (f.ToUtc is { } to) q = q.Where(a => a.ObservedAtUtc <= to);
        if (f.Source is { } source) q = q.Where(a => a.Source == source);
        return q.OrderByDescending(a => a.ObservedAtUtc).ThenByDescending(a => a.AnomalyScore).ToPagedAsync(page, ct);
    }

    public async Task<IReadOnlyList<AircraftAnomalyResult>> ListOpenHighPriorityAsync(int take, CancellationToken ct) =>
        await Query.AsNoTracking()
            .Where(a => a.IsAnomaly && Open.Contains(a.InvestigationStatus) && a.Severity != AnomalySeverity.Low)
            .OrderByDescending(a => a.Severity).ThenByDescending(a => a.ObservedAtUtc).Take(take).ToListAsync(ct);

    public async Task<IReadOnlyList<(DateTime ObservedAtUtc, AnomalySeverity Severity, InvestigationStatus Status)>> GetSinceAsync(DateTime fromUtc, CancellationToken ct)
    {
        var rows = await db.AnomalyResults.AsNoTracking().Where(a => a.IsAnomaly && a.ObservedAtUtc >= fromUtc)
            .Select(a => new { a.ObservedAtUtc, a.Severity, a.InvestigationStatus }).ToListAsync(ct);
        return rows.Select(r => (r.ObservedAtUtc, r.Severity, r.InvestigationStatus)).ToList();
    }

    public async Task<IReadOnlyDictionary<InvestigationStatus, int>> CountByInvestigationStatusAsync(CancellationToken ct) =>
        await db.AnomalyResults.AsNoTracking().Where(a => a.IsAnomaly).GroupBy(a => a.InvestigationStatus)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    private static readonly InvestigationStatus[] OpenInvestigation =
        [InvestigationStatus.New, InvestigationStatus.Acknowledged, InvestigationStatus.Investigating];

    public Task<AircraftAnomalyResult?> GetOpenBySourceAsync(Guid aircraftId, AnomalySource source, CancellationToken ct) =>
        db.AnomalyResults
            .Where(a => a.AircraftId == aircraftId && a.Source == source && OpenInvestigation.Contains(a.InvestigationStatus))
            .OrderByDescending(a => a.ObservedAtUtc).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<AircraftAnomalyResult>> ListOpenBySourceAsync(Guid aircraftId, AnomalySource source, CancellationToken ct) =>
        await db.AnomalyResults
            .Where(a => a.AircraftId == aircraftId && a.Source == source && OpenInvestigation.Contains(a.InvestigationStatus))
            .OrderBy(a => a.ObservedAtUtc).ThenBy(a => a.Id).ToListAsync(ct);

    public async Task<IReadOnlyList<AircraftAnomalyResult>> ListByWorkOrderAsync(Guid workOrderId, CancellationToken ct) =>
        await db.AnomalyResults
            .Where(a => a.WorkOrderId == workOrderId || db.WorkOrders.Any(w => w.Id == workOrderId && w.AnomalyId == a.Id))
            .OrderBy(a => a.ObservedAtUtc).ThenBy(a => a.Id).ToListAsync(ct);

    public async Task<MaintenanceWorkOrder?> FindOpenForecastWorkOrderForDayAsync(Guid aircraftId, DateTime dayUtc, CancellationToken ct)
    {
        var from = DateTime.SpecifyKind(dayUtc.Date, DateTimeKind.Utc);
        var to = from.AddDays(1);
        var orderIds = db.AnomalyResults
            .Where(a => a.AircraftId == aircraftId && a.Source == AnomalySource.ForecastModel && a.WorkOrderId != null
                        && a.ObservedAtUtc >= from && a.ObservedAtUtc < to)
            .Select(a => a.WorkOrderId);
        return await db.WorkOrders
            .Where(w => orderIds.Contains(w.Id) && w.Status != WorkOrderStatus.Closed && w.Status != WorkOrderStatus.Cancelled)
            .OrderBy(w => w.CreatedAtUtc).FirstOrDefaultAsync(ct);
    }

    public Task<bool> DedupKeyExistsAsync(string dedupKey, CancellationToken ct) =>
        db.AnomalyResults.AnyAsync(a => a.DedupKey == dedupKey, ct);

    public async Task AddAsync(AircraftAnomalyResult anomaly, CancellationToken ct) => await db.AnomalyResults.AddAsync(anomaly, ct);

    public async Task<IReadOnlyList<AircraftAnomalyResult>> ListQualifyingWithoutWorkOrderAsync(
        AnomalySeverity minimumSeverity, int take, CancellationToken ct, DateTime? observedSinceUtc = null)
    {
        // Enums persist as PascalCase STRINGS (AppDbContext.ConfigureConventions), so `a.Severity >= minimum`
        // would translate to an alphabetical SQL comparison - and 'Low' and 'Medium' both sort after 'High',
        // which silently matched every severity. Compare against an explicit set instead: it translates to
        // `IN (...)` and is correct whatever the storage representation.
        var allowed = Enum.GetValues<AnomalySeverity>().Where(s => s >= minimumSeverity).ToArray();
        var sources = AnomalyRules.WorkOrderSources;
        return await db.AnomalyResults
            .Where(a => a.IsAnomaly
                        && allowed.Contains(a.Severity)
                        // Forward-looking sources only (forecast / validated failure model) - AnomalyRules.
                        && sources.Contains(a.Source)
                        // Dismissed anomalies were judged not to need work; resolved ones already had it.
                        && a.InvestigationStatus != InvestigationStatus.Dismissed
                        // The anomaly link is what makes creation idempotent: an anomaly is handled by one work order,
                        // as its AnomalyId or linked to a daily forecast order through WorkOrderId.
                        && a.WorkOrderId == null
                        && !db.WorkOrders.Any(w => w.AnomalyId == a.Id)
                        // An old backlog (e.g. months of historical scoring) is not raised as new work.
                        && (observedSinceUtc == null || a.ObservedAtUtc >= observedSinceUtc))
            .OrderBy(a => a.ObservedAtUtc).ThenBy(a => a.Id)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AnomalyEligibilityRow>> ListAnomalyEligibilityAsync(CancellationToken ct) =>
        await db.AnomalyResults.AsNoTracking()
            .OrderBy(a => a.ObservedAtUtc).ThenBy(a => a.Id)
            .Select(a => new AnomalyEligibilityRow(
                a.Id,
                a.AircraftId,
                a.Aircraft!.RegistrationNumber,
                a.Severity,
                a.IsAnomaly,
                a.InvestigationStatus,
                a.ObservedAtUtc,
                a.WorkOrderId != null || db.WorkOrders.Any(w => w.AnomalyId == a.Id),
                a.Source))
            .ToListAsync(ct);
}

public sealed class PredictionRepository(AppDbContext db) : IPredictionRepository
{
    private IQueryable<AircraftRiskPrediction> Query => db.RiskPredictions
        .Include(p => p.Aircraft).Include(p => p.ModelVersion).Include(p => p.ReviewedBy);

    public Task<AircraftRiskPrediction?> GetAsync(Guid id, CancellationToken ct) => Query.FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<PagedResult<AircraftRiskPrediction>> ListAsync(PredictionFilter f, PageRequest page, CancellationToken ct)
    {
        var q = Query.AsNoTracking();
        if (f.AircraftId is { } aircraftId) q = q.Where(p => p.AircraftId == aircraftId);
        if (!string.IsNullOrWhiteSpace(f.RiskCategory)) q = q.Where(p => p.RiskCategory == f.RiskCategory);
        if (f.Basis is { } basis) q = q.Where(p => p.Basis == basis);
        if (f.ApprovalStatus is { } approval) q = q.Where(p => p.ApprovalStatus == approval);
        return q.OrderByDescending(p => p.PredictionTimestampUtc).ToPagedAsync(page, ct);
    }

    public async Task<IReadOnlyList<AircraftRiskPrediction>> ListForAircraftAsync(Guid aircraftId, int take, CancellationToken ct) =>
        await Query.AsNoTracking().Where(p => p.AircraftId == aircraftId)
            .OrderByDescending(p => p.PredictionTimestampUtc).Take(take).ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, AircraftRiskPrediction>> GetLatestByAircraftAsync(CancellationToken ct)
    {
        var latest = await db.RiskPredictions.AsNoTracking().Include(p => p.ModelVersion)
            .Where(p => p.PredictionTimestampUtc == db.RiskPredictions
                .Where(q => q.AircraftId == p.AircraftId).Max(q => q.PredictionTimestampUtc))
            .ToListAsync(ct);
        // Each aircraft's latest MODEL prediction wins over a newer descriptive indicator. Model rows are de-duplicated
        // per feature cutoff, descriptive rows are not, so a second scoring run on the same UTC day used to leave only
        // a newer descriptive row on top and the model assessment vanished (seen live 2026-10-28).
        var latestModel = await db.RiskPredictions.AsNoTracking().Include(p => p.ModelVersion)
            .Where(p => p.Basis == PredictionBasis.Model && p.PredictionTimestampUtc == db.RiskPredictions
                .Where(q => q.AircraftId == p.AircraftId && q.Basis == PredictionBasis.Model).Max(q => q.PredictionTimestampUtc))
            .ToListAsync(ct);
        var result = latest.GroupBy(p => p.AircraftId)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Basis == PredictionBasis.Model ? 0 : 1).First());
        foreach (var m in latestModel.GroupBy(p => p.AircraftId)) result[m.Key] = m.First();
        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, AircraftRiskPrediction>> GetLatestDescriptiveByAircraftAsync(CancellationToken ct)
    {
        var rows = await db.RiskPredictions.AsNoTracking()
            .Where(p => p.Basis == PredictionBasis.DescriptiveIndicator && p.PredictionTimestampUtc == db.RiskPredictions
                .Where(q => q.AircraftId == p.AircraftId && q.Basis == PredictionBasis.DescriptiveIndicator).Max(q => q.PredictionTimestampUtc))
            .ToListAsync(ct);
        return rows.GroupBy(p => p.AircraftId).ToDictionary(g => g.Key, g => g.First());
    }

    public async Task<IReadOnlyList<RiskStatus>> ListRiskStatusesAsync(CancellationToken ct) =>
        await db.RiskStatuses.AsNoTracking().OrderBy(r => r.DisplayOrder).ToListAsync(ct);

    public async Task<IReadOnlyDictionary<MaintenanceOutcome, int>> CountByOutcomeAsync(CancellationToken ct) =>
        await db.RiskPredictions.AsNoTracking().GroupBy(p => p.ActualOutcome)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
}

public sealed class ModelVersionRepository(AppDbContext db) : IModelVersionRepository
{
    public Task<ModelVersion?> GetAsync(Guid id, CancellationToken ct) =>
        db.ModelVersions.Include(m => m.ApprovedBy).FirstOrDefaultAsync(m => m.Id == id, ct);

    public async Task<IReadOnlyList<ModelVersion>> ListAsync(ModelType? modelType, ModelStatus? status, CancellationToken ct)
    {
        var q = db.ModelVersions.AsNoTracking().Include(m => m.ApprovedBy).AsQueryable();
        if (modelType is { } type) q = q.Where(m => m.ModelType == type);
        if (status is { } s) q = q.Where(m => m.Status == s);
        return await q.OrderByDescending(m => m.CreatedAtUtc).ToListAsync(ct);
    }

    public Task<ModelVersion?> GetActiveAsync(ModelType modelType, CancellationToken ct) =>
        db.ModelVersions.Include(m => m.ApprovedBy).FirstOrDefaultAsync(m => m.ModelType == modelType && m.Status == ModelStatus.Active, ct);
}

public sealed class PipelineRunRepository(AppDbContext db) : IPipelineRunRepository
{
    public Task<PipelineRun?> GetAsync(Guid id, CancellationToken ct) =>
        db.PipelineRuns.Include(r => r.RequestedBy).FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<PagedResult<PipelineRun>> ListAsync(PipelineJobType? jobType, PipelineRunStatus? status, PageRequest page, CancellationToken ct)
    {
        var q = db.PipelineRuns.AsNoTracking().Include(r => r.RequestedBy).AsQueryable();
        if (jobType is { } type) q = q.Where(r => r.JobType == type);
        if (status is { } s) q = q.Where(r => r.Status == s);
        return q.OrderByDescending(r => r.RequestedAtUtc).ToPagedAsync(page, ct);
    }

    public Task<PipelineRun?> GetLatestAsync(PipelineJobType jobType, PipelineRunStatus? status, CancellationToken ct)
    {
        var q = db.PipelineRuns.AsNoTracking().Include(r => r.RequestedBy).Where(r => r.JobType == jobType);
        if (status is { } s) q = q.Where(r => r.Status == s);
        return q.OrderByDescending(r => r.RequestedAtUtc).FirstOrDefaultAsync(ct);
    }

    public Task<bool> HasPendingAsync(PipelineJobType jobType, CancellationToken ct) =>
        db.PipelineRuns.AnyAsync(r => r.JobType == jobType && (r.Status == PipelineRunStatus.Queued || r.Status == PipelineRunStatus.Running), ct);

    public async Task AddAsync(PipelineRun run, CancellationToken ct) => await db.PipelineRuns.AddAsync(run, ct);

    /// <summary>
    /// The ETL stamps `parameters` with `{"importId": ...}` when it starts a run for an uploaded file.
    /// Matching is done in memory on the small set of telemetry-import runs so the query stays provider
    /// agnostic (the in-memory provider used by the tests cannot translate jsonb operators).
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ListIdsForImportAsync(Guid importId, CancellationToken ct)
    {
        var candidates = await db.PipelineRuns
            .Where(r => r.JobType == PipelineJobType.TelemetryImport)
            .Select(r => new { r.Id, r.Parameters })
            .ToListAsync(ct);
        return candidates.Where(r => MentionsImport(r.Parameters, importId)).Select(r => r.Id).ToList();
    }

    public async Task<int> DeleteForImportAsync(Guid importId, CancellationToken ct)
    {
        var ids = await ListIdsForImportAsync(importId, ct);
        if (ids.Count == 0) return 0;
        var runs = await db.PipelineRuns.Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        db.PipelineRuns.RemoveRange(runs);
        return runs.Count;
    }

    private static bool MentionsImport(string? parameters, Guid importId) =>
        !string.IsNullOrWhiteSpace(parameters)
        && parameters.Contains(importId.ToString(), StringComparison.OrdinalIgnoreCase);
}

public sealed class SensorForecastRepository(AppDbContext db) : ISensorForecastRepository
{
    public Task<SensorForecast?> GetLatestAsync(Guid aircraftId, Guid modelVersionId, SensorForecastHorizon horizon, CancellationToken ct) =>
        db.SensorForecasts.AsNoTracking()
            .Where(f => f.AircraftId == aircraftId && f.ModelVersionId == modelVersionId && f.Horizon == horizon)
            .OrderByDescending(f => f.DataAsOfUtc).ThenByDescending(f => f.GeneratedAtUtc)
            .FirstOrDefaultAsync(ct);
}
