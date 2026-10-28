using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace AFP.Infrastructure.Persistence.Repositories;

public sealed class AircraftRepository(AppDbContext db) : IAircraftRepository
{
    private static readonly InvestigationStatus[] OpenInvestigation =
        [InvestigationStatus.New, InvestigationStatus.Acknowledged, InvestigationStatus.Investigating];

    private static readonly WorkOrderStatus[] ClosedStatuses = [WorkOrderStatus.Closed, WorkOrderStatus.Cancelled];

    public Task<Aircraft?> GetByIdAsync(Guid id, CancellationToken ct) => db.Aircraft.FirstOrDefaultAsync(a => a.Id == id, ct);

    public Task<Aircraft?> GetByRegistrationAsync(string registrationNumber, CancellationToken ct) =>
        db.Aircraft.FirstOrDefaultAsync(a => a.RegistrationNumber == registrationNumber, ct);

    public async Task<IReadOnlyDictionary<string, Guid>> GetRegistrationMapAsync(CancellationToken ct) =>
        await db.Aircraft.AsNoTracking().ToDictionaryAsync(a => a.RegistrationNumber, a => a.Id, StringComparer.OrdinalIgnoreCase, ct);

    public Task<PagedResult<Aircraft>> ListAsync(AircraftFilter f, PageRequest page, CancellationToken ct)
    {
        var q = db.Aircraft.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var s = QueryExtensions.Pattern(f.Search);
            q = q.Where(a => a.RegistrationNumber.ToLower().Contains(s) || a.Model.ToLower().Contains(s)
                             || a.Manufacturer.ToLower().Contains(s) || a.FleetGroup.ToLower().Contains(s));
        }
        if (f.Status is { } status) q = q.Where(a => a.OperationalStatus == status);
        if (!string.IsNullOrWhiteSpace(f.FleetGroup)) q = q.Where(a => a.FleetGroup == f.FleetGroup);
        if (!string.IsNullOrWhiteSpace(f.Model)) q = q.Where(a => a.Model == f.Model);
        return q.OrderBy(a => a.RegistrationNumber).ToPagedAsync(page, ct);
    }

    public async Task<IReadOnlyList<Aircraft>> ListAllAsync(CancellationToken ct) =>
        await db.Aircraft.AsNoTracking().OrderBy(a => a.RegistrationNumber).ToListAsync(ct);

    public async Task<IReadOnlyList<string>> ListFleetGroupsAsync(CancellationToken ct) =>
        await db.Aircraft.AsNoTracking().Select(a => a.FleetGroup).Distinct().OrderBy(g => g).ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, AircraftCounters>> GetCountersAsync(IReadOnlyCollection<Guid> aircraftIds, CancellationToken ct)
    {
        var ids = aircraftIds.ToList();
        var anomalies = await db.AnomalyResults.AsNoTracking()
            .Where(a => ids.Contains(a.AircraftId) && OpenInvestigation.Contains(a.InvestigationStatus))
            .GroupBy(a => a.AircraftId)
            .Select(g => new { g.Key, Open = g.Count(), High = g.Count(a => a.Severity == AnomalySeverity.High) })
            .ToDictionaryAsync(x => x.Key, ct);
        var flags = await db.InspectionFlags.AsNoTracking()
            .Where(f => ids.Contains(f.AircraftId) && f.IsActive)
            .GroupBy(f => f.AircraftId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var orders = await db.WorkOrders.AsNoTracking()
            .Where(w => ids.Contains(w.AircraftId) && !ClosedStatuses.Contains(w.Status))
            .GroupBy(w => w.AircraftId)
            .Select(g => new { g.Key, Count = g.Count(), InProgress = g.Count(w => FleetStatusRules.MaintenanceStartedStatuses.Contains(w.Status)) })
            .ToDictionaryAsync(x => x.Key, ct);

        return ids.ToDictionary(id => id, id => new AircraftCounters(
            id,
            anomalies.TryGetValue(id, out var a) ? a.Open : 0,
            anomalies.TryGetValue(id, out var h) ? h.High : 0,
            flags.GetValueOrDefault(id),
            orders.TryGetValue(id, out var o) ? o.Count : 0,
            orders.TryGetValue(id, out var p) ? p.InProgress : 0));
    }

    public async Task<IReadOnlyDictionary<OperationalStatus, int>> CountByStatusAsync(CancellationToken ct) =>
        await db.Aircraft.AsNoTracking().GroupBy(a => a.OperationalStatus)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    public async Task AddAsync(Aircraft aircraft, CancellationToken ct) => await db.Aircraft.AddAsync(aircraft, ct);

    public async Task UpdateLastTelemetryAsync(IReadOnlyDictionary<Guid, DateTime> latestByAircraft, CancellationToken ct)
    {
        if (latestByAircraft.Count == 0) return;
        var ids = latestByAircraft.Keys.ToList();
        foreach (var aircraft in await db.Aircraft.Where(a => ids.Contains(a.Id)).ToListAsync(ct))
        {
            var latest = latestByAircraft[aircraft.Id];
            if (aircraft.LastTelemetryAtUtc is null || aircraft.LastTelemetryAtUtc < latest) aircraft.LastTelemetryAtUtc = latest;
        }
    }

    public async Task<IReadOnlyList<HealthThreshold>> ListHealthThresholdsAsync(CancellationToken ct) =>
        await db.HealthThresholds.AsNoTracking()
            .Where(t => t.StatusId == LifecycleStatus.Active)
            .OrderBy(t => t.DisplayOrder)
            .ToListAsync(ct);
}

public sealed class MaintenanceRepository(AppDbContext db) : IMaintenanceRepository
{
    private static readonly WorkOrderStatus[] ClosedStatuses = [WorkOrderStatus.Closed, WorkOrderStatus.Cancelled];

    private IQueryable<MaintenanceWorkOrder> WorkOrders => db.WorkOrders
        .Include(w => w.Aircraft).Include(w => w.AssignedTechnician).Include(w => w.CreatedBy);

    public Task<MaintenanceWorkOrder?> GetWorkOrderAsync(Guid id, CancellationToken ct) =>
        WorkOrders.Include(w => w.Anomaly).Include(w => w.Prediction).Include(w => w.Reports).ThenInclude(r => r.Technician)
            .FirstOrDefaultAsync(w => w.Id == id, ct);

    public Task<PagedResult<MaintenanceWorkOrder>> ListWorkOrdersAsync(WorkOrderFilter f, PageRequest page, CancellationToken ct)
    {
        var q = WorkOrders.AsNoTracking();
        if (f.AircraftId is { } aircraftId) q = q.Where(w => w.AircraftId == aircraftId);
        if (f.Status is { } status) q = q.Where(w => w.Status == status);
        if (f.Priority is { } priority) q = q.Where(w => w.Priority == priority);
        if (f.AssignedTechnicianId is { } techId) q = q.Where(w => w.AssignedTechnicianId == techId);
        if (f.OpenOnly) q = q.Where(w => !ClosedStatuses.Contains(w.Status));
        if (f.Source is { } source) q = q.Where(w => w.Source == source);
        if (f.AnomalyId is { } anomalyId)
            q = q.Where(w => w.AnomalyId == anomalyId || db.AnomalyResults.Any(a => a.Id == anomalyId && a.WorkOrderId == w.Id));
        return q.OrderByDescending(w => w.Priority).ThenByDescending(w => w.CreatedAtUtc).ToPagedAsync(page, ct);
    }

    public async Task<IReadOnlyDictionary<WorkOrderStatus, int>> CountWorkOrdersByStatusAsync(
        IReadOnlyCollection<Guid> aircraftIds, CancellationToken ct)
    {
        var ids = aircraftIds.ToList();
        return await WorkOrders.AsNoTracking().Where(w => ids.Contains(w.AircraftId))
            .GroupBy(w => w.Status).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    }

    public async Task<IReadOnlyList<MaintenanceWorkOrder>> ListOpenWorkOrdersAsync(Guid? aircraftId, CancellationToken ct)
    {
        var q = WorkOrders.AsNoTracking().Where(w => !ClosedStatuses.Contains(w.Status));
        if (aircraftId is { } id) q = q.Where(w => w.AircraftId == id);
        return await q.OrderByDescending(w => w.Priority).ThenBy(w => w.CreatedAtUtc).ToListAsync(ct);
    }

    private static readonly string[] WorkOrderCreationActions = ["WorkOrderCreated", "WorkOrderCreatedFromAnomaly"];

    public async Task<int> NextWorkOrderSequenceAsync(int year, CancellationToken ct)
    {
        var prefix = $"WO-{year}-";
        var numbers = await db.WorkOrders.Where(w => w.WorkOrderNumber.StartsWith(prefix)).Select(w => w.WorkOrderNumber).ToListAsync(ct);
        var audited = await db.ActionAudits.Where(a => WorkOrderCreationActions.Contains(a.Action) && a.Details != null)
            .Select(a => a.Details!).ToListAsync(ct);
        foreach (var details in audited)
            if (Json.ParseOptional(details) is { } d && Json.Find(d, "workOrderNumber") is { ValueKind: System.Text.Json.JsonValueKind.String } n)
                numbers.Add(n.GetString()!);
        var highest = numbers.Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Select(n => int.TryParse(n[prefix.Length..], out var v) ? v : 0).DefaultIfEmpty(0).Max();
        return highest + 1;
    }

    public Task<int> CountWorkOrdersCreatedBetweenAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct) =>
        db.WorkOrders.CountAsync(w => w.CreatedAtUtc >= fromUtc && w.CreatedAtUtc < toUtc, ct);

    public async Task<IReadOnlyList<MaintenanceWorkOrder>> ListClosedWorkOrdersAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct) =>
        await db.WorkOrders.AsNoTracking()
            .Where(w => w.Status == WorkOrderStatus.Closed && w.ClosedAtUtc >= fromUtc && w.ClosedAtUtc <= toUtc).ToListAsync(ct);

    public async Task AddWorkOrderAsync(MaintenanceWorkOrder workOrder, CancellationToken ct) => await db.WorkOrders.AddAsync(workOrder, ct);

    public void SetOriginalConcurrencyStamp(MaintenanceWorkOrder workOrder, Guid stamp) =>
        db.Entry(workOrder).Property(w => w.ConcurrencyStamp).OriginalValue = stamp;

    public async Task<IReadOnlyList<MaintenanceActionCode>> ListActionCodesAsync(CancellationToken ct) =>
        await db.MaintenanceActionCodes.AsNoTracking().Where(c => c.StatusId == LifecycleStatus.Active)
            .OrderBy(c => c.DisplayOrder).ToListAsync(ct);

    public Task<PagedResult<MaintenanceLog>> ListLogsAsync(MaintenanceLogFilter f, PageRequest page, CancellationToken ct)
    {
        var q = db.MaintenanceLogs.AsNoTracking().Include(l => l.Aircraft).AsQueryable();
        if (f.AircraftId is { } aircraftId) q = q.Where(l => l.AircraftId == aircraftId);
        if (f.EventType is { } type) q = q.Where(l => l.EventType == type);
        if (f.FromUtc is { } from) q = q.Where(l => l.OccurredAtUtc >= from);
        if (f.ToUtc is { } to) q = q.Where(l => l.OccurredAtUtc <= to);
        return q.OrderByDescending(l => l.OccurredAtUtc).ToPagedAsync(page, ct);
    }

    public async Task<IReadOnlyList<MaintenanceLog>> ListLogsSinceAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct) =>
        await db.MaintenanceLogs.AsNoTracking().Where(l => l.OccurredAtUtc >= fromUtc && l.OccurredAtUtc <= toUtc).ToListAsync(ct);

    public async Task AddLogAsync(MaintenanceLog log, CancellationToken ct) => await db.MaintenanceLogs.AddAsync(log, ct);

    public Task<InspectionFlag?> GetFlagAsync(Guid id, CancellationToken ct) =>
        db.InspectionFlags.Include(f => f.Aircraft).Include(f => f.RaisedBy).Include(f => f.ClearedBy).FirstOrDefaultAsync(f => f.Id == id, ct);

    public async Task<IReadOnlyList<InspectionFlag>> ListFlagsAsync(Guid? aircraftId, bool activeOnly, CancellationToken ct)
    {
        var q = db.InspectionFlags.AsNoTracking().Include(f => f.Aircraft).Include(f => f.RaisedBy).Include(f => f.ClearedBy).AsQueryable();
        if (aircraftId is { } id) q = q.Where(f => f.AircraftId == id);
        if (activeOnly) q = q.Where(f => f.IsActive);
        return await q.OrderByDescending(f => f.Severity).ThenByDescending(f => f.RaisedAtUtc).ToListAsync(ct);
    }

    public async Task AddFlagAsync(InspectionFlag flag, CancellationToken ct) => await db.InspectionFlags.AddAsync(flag, ct);
}
