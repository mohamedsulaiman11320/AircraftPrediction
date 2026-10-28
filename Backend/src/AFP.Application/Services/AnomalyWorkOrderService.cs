using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Application.Contracts;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Domain.Rules;
using Microsoft.Extensions.Options;

namespace AFP.Application.Services;

public interface IAnomalyWorkOrderService
{
    /// <summary>
    /// Raises one work order per qualifying anomaly that does not already have one.
    /// Idempotent: re-running over the same anomalies creates nothing further.
    /// </summary>
    Task<AnomalyWorkOrderRunDto> GenerateAsync(CancellationToken ct);

    /// <summary>
    /// Read-only preview of exactly what <see cref="GenerateAsync"/> would create.
    /// <para>
    /// Eligibility is not re-implemented here: the selected set comes from the very same repository query the
    /// generator uses, so the two cannot drift. The remaining anomalies are then explained individually.
    /// This method performs no insert, update or delete and writes no audit entry.
    /// </para>
    /// </summary>
    Task<AnomalyWorkOrderDryRunDto> DryRunAsync(CancellationToken ct);
}

/// <summary>
/// Turns detected anomalies into maintenance work orders.
///
/// <para><b>Idempotency</b> is anchored on <see cref="MaintenanceWorkOrder.AnomalyId"/>: an anomaly that already
/// has a work order is never considered again, so re-scoring, a retried batch or a duplicate trigger cannot
/// produce a second ticket. This is a database-backed identity, not an in-memory guard.</para>
///
/// <para><b>Business rule provenance.</b> The BRD defines dispatcher-raised inspection flags (BHR-06) but does
/// <i>not</i> define automatic work-order creation from anomalies, so the qualifying rule here is deliberately
/// narrow and configurable rather than invented broadly: only anomalies at or above a configured severity
/// (default <see cref="AnomalySeverity.High"/>) that have not been dismissed. Each run is capped so a backlog
/// cannot flood the dispatcher's queue in one go.</para>
///
/// <para>What this never does: it does not assign a technician (assignment stays a dispatcher decision), does
/// not change aircraft operational status, and does not touch maintenance history — a completed maintenance
/// record may only come from the technician completing the work.</para>
/// </summary>
public sealed class AnomalyWorkOrderService(
    IAnomalyRepository anomalies,
    IMaintenanceRepository maintenance,
    IActionAuditor auditor,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClock clock,
    IOptions<AnomalyWorkOrderOptions> options,
    IAircraftRepository aircraftRepo,
    IWorkOrderDiagnosisService diagnosis) : IAnomalyWorkOrderService
{
    private readonly AnomalyWorkOrderOptions _options = options.Value;

    public async Task<AnomalyWorkOrderRunDto> GenerateAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
            return new AnomalyWorkOrderRunDto(0, 0, [], "Automatic work-order creation is disabled.");

        var candidates = await anomalies.ListQualifyingWithoutWorkOrderAsync(
            _options.MinimumSeverity, _options.MaxPerRun, ct, ObservedSince());
        if (candidates.Count == 0)
            return new AnomalyWorkOrderRunDto(0, 0, [], "No qualifying anomaly without a work order.");

        var now = clock.UtcNow;
        var sequence = await maintenance.NextWorkOrderSequenceAsync(now.Year, ct) - 1;
        var created = new List<string>();
        var diagnosisFailures = 0;
        var linked = 0;

        // Forecast anomalies: ONE work order per aircraft per projected UTC day (user decision 2026-10-28). Anomalies of a
        // day that already has an open order are linked to it, and its diagnosis is refreshed while it is still New.
        foreach (var day in candidates.Where(a => a.Source == AnomalySource.ForecastModel)
                     .GroupBy(a => (a.AircraftId, Day: a.ObservedAtUtc.Date)).OrderBy(g => g.Key.Day))
        {
            var group = day.OrderBy(a => a.ObservedAtUtc).ThenBy(a => a.Id).ToList();
            var existing = await anomalies.FindOpenForecastWorkOrderForDayAsync(day.Key.AircraftId, day.Key.Day, ct);
            if (existing is not null)
            {
                foreach (var a in group) a.WorkOrderId = existing.Id;
                if (existing.Status == WorkOrderStatus.Open)
                {
                    var all = (await anomalies.ListByWorkOrderAsync(existing.Id, ct)).Concat(group).DistinctBy(a => a.Id).ToList();
                    if (!await AttachForecastDayDiagnosisAsync(existing, all, ct)) diagnosisFailures++;
                    existing.UpdatedAtUtc = now;
                }
                await auditor.RecordAsync("AnomaliesLinkedToWorkOrder", nameof(MaintenanceWorkOrder), existing.Id.ToString(),
                    new { existing.WorkOrderNumber, anomalyIds = group.Select(a => a.Id).ToList() }, ct);
                linked += group.Count;
                continue;
            }
            sequence++;
            var order = new MaintenanceWorkOrder
            {
                WorkOrderNumber = $"WO-{now.Year}-{sequence:D6}",
                AircraftId = day.Key.AircraftId,
                AnomalyId = group[0].Id,
                Priority = PriorityFor(group.Max(a => a.Severity)),
                Title = $"Forecast operating-limit breach on {day.Key.Day:yyyy-MM-dd} (UTC)",
                Description = $"Raised automatically from {group.Count} forecast anomal{(group.Count == 1 ? "y" : "ies")} " +
                              $"(#{string.Join(", #", group.Select(a => a.Id))}) projected for {day.Key.Day:yyyy-MM-dd} UTC: the 30-day " +
                              "sensor forecast crosses an approved operating limit. Inspect before the forecast breach; see the diagnosis.",
                Status = WorkOrderStatus.Open,
                Source = WorkOrderSource.Automatic,
                AssignedTechnicianId = null,
                CreatedByUserId = currentUser.UserId ?? _options.SystemUserId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            if (!await AttachForecastDayDiagnosisAsync(order, group, ct)) diagnosisFailures++;
            await maintenance.AddWorkOrderAsync(order, ct);
            foreach (var a in group) a.WorkOrderId = order.Id;
            await auditor.RecordAsync("WorkOrderCreatedFromAnomaly", nameof(MaintenanceWorkOrder), order.Id.ToString(),
                new { order.WorkOrderNumber, anomalyId = group[0].Id, anomalyIds = group.Select(a => a.Id).ToList(), day = day.Key.Day }, ct);
            created.Add(order.WorkOrderNumber);
        }

        foreach (var anomaly in candidates.Where(a => a.Source != AnomalySource.ForecastModel))
        {
            sequence++;
            var workOrder = new MaintenanceWorkOrder
            {
                WorkOrderNumber = $"WO-{now.Year}-{sequence:D6}",
                AircraftId = anomaly.AircraftId,
                // Traceability: the ticket points back at the anomaly, and through it to the model run.
                AnomalyId = anomaly.Id,
                Priority = PriorityFor(anomaly.Severity),
                Title = $"{anomaly.Severity} anomaly detected on {anomaly.ObservedAtUtc:yyyy-MM-dd HH:mm} UTC",
                Description =
                    $"Raised automatically from anomaly #{anomaly.Id} (severity {anomaly.Severity}, " +
                    $"score {anomaly.AnomalyScore:0.####}, observed {anomaly.ObservedAtUtc:O}). " +
                    "The anomaly score is a descriptive indicator, not a failure probability. " +
                    "Review the source telemetry before acting.",
                Status = WorkOrderStatus.Open,
                Source = WorkOrderSource.Automatic,
                // Assignment stays a dispatcher decision; nothing is auto-assigned.
                AssignedTechnicianId = null,
                CreatedByUserId = currentUser.UserId ?? _options.SystemUserId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            if (anomaly.Source == AnomalySource.ThresholdRule)
            {
                workOrder.Title = $"Operating limit breach in recorded telemetry - window ending {anomaly.ObservedAtUtc:yyyy-MM-dd HH:mm} UTC";
                workOrder.Description = $"Raised automatically from anomaly #{anomaly.Id}: an approved operating limit was breached in " +
                                        "measured telemetry (see the anomaly evidence and the diagnosis). Not a failure prediction.";
            }
            else if (anomaly.Source == AnomalySource.ForecastModel)
            {
                workOrder.Title = $"Forecast operating-limit breach from {anomaly.ObservedAtUtc:yyyy-MM-dd HH:mm} UTC";
                workOrder.Description = $"Raised automatically from forecast anomaly #{anomaly.Id}: the 30-day sensor forecast crosses an " +
                                        "approved operating limit. Inspect before the forecast breach; see the diagnosis.";
            }
            else if (anomaly.Source == AnomalySource.FailureModel)
            {
                workOrder.Title = $"Validated 30-day failure risk (Critical) - {anomaly.ObservedAtUtc:yyyy-MM-dd} assessment";
                workOrder.Description = $"Raised automatically from anomaly #{anomaly.Id}: a validated failure-model probability in the " +
                                        "Critical tier. Advisory: inspect before acting.";
            }
            if (!await AttachDiagnosisAsync(workOrder, anomaly, ct)) diagnosisFailures++;
            await maintenance.AddWorkOrderAsync(workOrder, ct);
            await auditor.RecordAsync("WorkOrderCreatedFromAnomaly", nameof(MaintenanceWorkOrder),
                workOrder.Id.ToString(),
                new { workOrder.WorkOrderNumber, anomalyId = anomaly.Id, severity = anomaly.Severity.ToString() }, ct);
            created.Add(workOrder.WorkOrderNumber);
        }

        await unitOfWork.SaveChangesAsync(ct);
        return new AnomalyWorkOrderRunDto(candidates.Count, created.Count, created,
            linked > 0 ? $"{linked} forecast anomal{(linked == 1 ? "y" : "ies")} linked to an existing daily work order." : null, diagnosisFailures);
    }

    private DateTime? ObservedSince() => _options.MaxAgeDays > 0 ? clock.UtcNow.AddDays(-_options.MaxAgeDays) : null;

    /// <summary>
    /// Stores the diagnosis snapshot. A failure never loses the ticket: it is recorded on the work order
    /// (DiagnosisStatus Failed + the error) and counted in the run summary.
    /// </summary>
    private async Task<bool> AttachDiagnosisAsync(MaintenanceWorkOrder workOrder, AircraftAnomalyResult anomaly, CancellationToken ct)
    {
        try
        {
            var aircraft = await aircraftRepo.GetByIdAsync(anomaly.AircraftId, ct)
                ?? throw new InvalidOperationException($"aircraft {anomaly.AircraftId} not found");
            ApplyDiagnosis(workOrder, await diagnosis.GenerateAsync(aircraft, anomaly, null, ct), clock.UtcNow);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            workOrder.DiagnosisStatus = DiagnosisStatus.Failed;
            workOrder.DiagnosisError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            workOrder.DiagnosisGeneratedAtUtc = clock.UtcNow;
            return false;
        }
    }

    /// <summary>The day order's diagnosis covers every breach of its anomalies; a failure is recorded, never fatal.</summary>
    private async Task<bool> AttachForecastDayDiagnosisAsync(MaintenanceWorkOrder workOrder, IReadOnlyList<AircraftAnomalyResult> dayAnomalies, CancellationToken ct)
    {
        try
        {
            var aircraft = await aircraftRepo.GetByIdAsync(workOrder.AircraftId, ct)
                ?? throw new InvalidOperationException($"aircraft {workOrder.AircraftId} not found");
            ApplyDiagnosis(workOrder, diagnosis.GenerateForecastDay(aircraft, dayAnomalies), clock.UtcNow);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            workOrder.DiagnosisStatus = DiagnosisStatus.Failed;
            workOrder.DiagnosisError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            workOrder.DiagnosisGeneratedAtUtc = clock.UtcNow;
            return false;
        }
    }

    internal static void ApplyDiagnosis(MaintenanceWorkOrder workOrder, DiagnosisSnapshot snapshot, DateTime nowUtc)
    {
        workOrder.DiagnosisStatus = DiagnosisStatus.Generated;
        workOrder.DiagnosisText = snapshot.Text;
        workOrder.DiagnosisEvidence = snapshot.EvidenceJson;
        workOrder.DiagnosisMethod = snapshot.Method;
        workOrder.DiagnosisRuleVersion = snapshot.RuleVersion;
        workOrder.DiagnosisGeneratedAtUtc = nowUtc;
    }

    public async Task<AnomalyWorkOrderDryRunDto> DryRunAsync(CancellationToken ct)
    {
        // The authoritative selection: identical call to the one GenerateAsync makes.
        var selected = _options.Enabled
            ? await anomalies.ListQualifyingWithoutWorkOrderAsync(_options.MinimumSeverity, _options.MaxPerRun, ct, ObservedSince())
            : [];
        var selectedIds = selected.Select(a => a.Id).ToHashSet();

        var all = await anomalies.ListAnomalyEligibilityAsync(ct);
        var rows = new List<AnomalyDryRunRowDto>(all.Count);

        foreach (var row in all)
        {
            var (eligible, reason) = Classify(row, selectedIds);
            rows.Add(new AnomalyDryRunRowDto(
                row.AnomalyId, row.RegistrationNumber, row.Severity.ToString(), row.ObservedAtUtc,
                row.InvestigationStatus.ToString(), eligible, reason,
                eligible ? PriorityFor(row.Severity).ToString() : null));
        }

        var counts = new AnomalyDryRunCountsDto(
            Total: rows.Count,
            WouldCreate: rows.Count(r => r.WouldCreateWorkOrder),
            AlreadyTicketed: rows.Count(r => r.ExclusionReason == Reasons.AlreadyTicketed),
            BelowThreshold: rows.Count(r => r.ExclusionReason == Reasons.BelowThreshold),
            Dismissed: rows.Count(r => r.ExclusionReason == Reasons.Dismissed),
            NotAnomaly: rows.Count(r => r.ExclusionReason == Reasons.NotAnomaly),
            DeferredByCap: rows.Count(r => r.ExclusionReason == Reasons.DeferredByCap),
            Disabled: rows.Count(r => r.ExclusionReason == Reasons.Disabled),
            TooOld: rows.Count(r => r.ExclusionReason == Reasons.TooOld));

        return new AnomalyWorkOrderDryRunDto(
            _options.Enabled, _options.MinimumSeverity.ToString(), _options.MaxPerRun, counts, rows);
    }

    private (bool Eligible, string? Reason) Classify(AnomalyEligibilityRow row, HashSet<long> selectedIds)
    {
        if (selectedIds.Contains(row.AnomalyId)) return (true, null);
        if (!_options.Enabled) return (false, Reasons.Disabled);
        if (!row.IsAnomaly) return (false, Reasons.NotAnomaly);
        if (row.HasWorkOrder) return (false, Reasons.AlreadyTicketed);
        if (!AnomalyRules.WorkOrderSources.Contains(row.Source)) return (false, Reasons.RecordedTelemetrySource);
        if (row.InvestigationStatus == InvestigationStatus.Dismissed) return (false, Reasons.Dismissed);
        if (row.Severity < _options.MinimumSeverity) return (false, Reasons.BelowThreshold);
        if (ObservedSince() is { } since && row.ObservedAtUtc < since) return (false, Reasons.TooOld);
        // Qualifies on every rule but fell outside this run's cap; the next run picks it up.
        return (false, Reasons.DeferredByCap);
    }

    private static class Reasons
    {
        public const string Disabled = "Automatic work-order creation is disabled.";
        public const string NotAnomaly = "Scored row is not flagged as an anomaly.";
        public const string AlreadyTicketed = "A work order already references this anomaly.";
        public const string Dismissed = "Anomaly investigation was dismissed.";
        public const string BelowThreshold = "Severity is below the configured minimum.";
        public const string DeferredByCap = "Eligible, but beyond this run's cap; a later run will include it.";
        public const string TooOld = "Observed before the automatic-ticket age window (historical backlog).";
        public const string RecordedTelemetrySource = "Found in recorded telemetry; work orders are raised from forecasts only.";
    }

    /// <summary>Severity maps to an existing <see cref="WorkOrderPriority"/>; no new vocabulary is introduced.</summary>
    private static WorkOrderPriority PriorityFor(AnomalySeverity severity) => severity switch
    {
        AnomalySeverity.High => WorkOrderPriority.High,
        AnomalySeverity.Medium => WorkOrderPriority.Medium,
        _ => WorkOrderPriority.Low,
    };
}
