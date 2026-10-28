using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Application.Contracts;
using AFP.Application.Mapping;
using AFP.Domain;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Domain.Rules;

namespace AFP.Application.Services;

public interface IWorkOrderService
{
    Task<PagedResult<WorkOrderDto>> ListAsync(WorkOrderFilter filter, PageRequest page, CancellationToken ct);
    Task<WorkOrderDetailDto> GetAsync(Guid id, CancellationToken ct);
    Task<WorkOrderDto> CreateAsync(CreateWorkOrderRequest request, CancellationToken ct);
    Task<WorkOrderDto> UpdateAsync(Guid id, UpdateWorkOrderRequest request, CancellationToken ct);
    Task<WorkOrderDto> AssignAsync(Guid id, AssignWorkOrderRequest request, CancellationToken ct);
    Task<WorkOrderDto> StartInspectionAsync(Guid id, CancellationToken ct);
    Task<WorkOrderDto> RecordInspectionAsync(Guid id, RecordInspectionRequest request, CancellationToken ct);
    Task<WorkOrderDto> ResolveAsync(Guid id, ResolveWorkOrderRequest request, CancellationToken ct);
    Task<WorkOrderDto> ReopenAsync(Guid id, ReasonRequest request, CancellationToken ct);
    Task<WorkOrderDto> CloseAsync(Guid id, CloseWorkOrderRequest request, CancellationToken ct);
    /// <summary>Approved maintenance action codes a technician may report (seeded reference data).</summary>
    Task<IReadOnlyList<MaintenanceActionCodeDto>> ListActionCodesAsync(CancellationToken ct);
    Task<WorkOrderDto> CancelAsync(Guid id, ReasonRequest request, CancellationToken ct);
}

/// <summary>Holds constants shared by the work-order rules.</summary>
public static class WorkOrderRules
{
    /// <summary>Technician maintenance notes must carry at least this much substance.</summary>
    public const int MinimumMaintenanceNotesLength = 10;
}

public sealed class WorkOrderService(
    IMaintenanceRepository maintenance,
    IAircraftRepository aircraftRepo,
    IAnomalyRepository anomalies,
    IPredictionRepository predictions,
    IUserRepository users,
    IAuditRepository auditRepo,
    IActionAuditor auditor,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClock clock,
    IWorkOrderDiagnosisService diagnosis) : IWorkOrderService
{
    private const string Entity = nameof(MaintenanceWorkOrder);

    public async Task<PagedResult<WorkOrderDto>> ListAsync(WorkOrderFilter filter, PageRequest page, CancellationToken ct)
    {
        var now = clock.UtcNow;
        // Technicians only ever see the tickets assigned to them.
        if (!currentUser.IsDispatcher) filter = filter with { AssignedTechnicianId = currentUser.RequireUserId() };
        return (await maintenance.ListWorkOrdersAsync(filter, page.Normalize(), ct)).Map(w => w.ToDto(now, currentUser));
    }

    public async Task<WorkOrderDetailDto> GetAsync(Guid id, CancellationToken ct)
    {
        var wo = await Load(id, ct);
        var history = await auditRepo.ListActionsAsync(new ActionAuditFilter(Entity, id.ToString(), null, null, null), new PageRequest(1, 200), ct);
        // Embed the aircraft summary and AI diagnosis so technicians need no access to fleet/anomaly/prediction screens.
        var counters = AircraftCountersView.From((await aircraftRepo.GetCountersAsync([wo.AircraftId], ct)).GetValueOrDefault(wo.AircraftId));
        var latest = (await predictions.ListForAircraftAsync(wo.AircraftId, 1, ct)).FirstOrDefault();
        var aircraft = (await aircraftRepo.GetByIdAsync(wo.AircraftId, ct))!.ToSummaryDto(counters, latest);
        var anomaly = wo.AnomalyId is { } anomalyId ? (await anomalies.GetAsync(anomalyId, ct))?.ToDto() : null;
        var prediction = wo.PredictionId is { } predictionId ? (await predictions.GetAsync(predictionId, ct))?.ToDto() : null;
        return new WorkOrderDetailDto(wo.ToDto(clock.UtcNow, currentUser), history.Items.Select(h => h.ToDto()).ToList(), aircraft, anomaly, prediction,
            wo.Reports.OrderBy(r => r.Sequence).Select(r => r.ToDto()).ToList(), Json.ParseOptional(wo.DiagnosisEvidence));
    }

    public async Task<WorkOrderDto> CreateAsync(CreateWorkOrderRequest r, CancellationToken ct)
    {
        new Validator()
            .Must(r.AircraftId.HasValue, "aircraftId", "Aircraft is required.")
            .Must(r.Priority.HasValue, "priority", "Priority is required.")
            .Required("title", r.Title, "Title").MaxLength("title", r.Title, 200, "Title")
            .Required("description", r.Description, "Description").MaxLength("description", r.Description, 4000, "Description")
            .MaxLength("componentId", r.ComponentId, 64, "Component")
            .Must(r.DueAtUtc is null || r.DueAtUtc > clock.UtcNow, "dueAtUtc", "Due date must be in the future.")
            .ThrowIfInvalid();

        var aircraft = await aircraftRepo.GetByIdAsync(r.AircraftId!.Value, ct) ?? throw new NotFoundException("Aircraft", r.AircraftId);
        if (r.AnomalyId is { } anomalyId)
        {
            var anomaly = await anomalies.GetAsync(anomalyId, ct) ?? throw new NotFoundException("Anomaly", anomalyId);
            if (anomaly.AircraftId != aircraft.Id) throw new ValidationException("anomalyId", "Anomaly belongs to a different aircraft.");
        }
        if (r.PredictionId is { } predictionId)
        {
            var prediction = await predictions.GetAsync(predictionId, ct) ?? throw new NotFoundException("Prediction", predictionId);
            if (prediction.AircraftId != aircraft.Id) throw new ValidationException("predictionId", "Prediction belongs to a different aircraft.");
        }
        if (r.AssignedTechnicianId is { } techId) await EnsureTechnicianAsync(techId, ct);

        var now = clock.UtcNow;
        var sequence = await maintenance.NextWorkOrderSequenceAsync(now.Year, ct);
        var wo = new MaintenanceWorkOrder
        {
            WorkOrderNumber = $"WO-{now.Year}-{sequence:D6}",
            AircraftId = aircraft.Id,
            ComponentId = Clean(r.ComponentId),
            AnomalyId = r.AnomalyId,
            PredictionId = r.PredictionId,
            Priority = r.Priority!.Value,
            Title = r.Title!.Trim(),
            Description = r.Description!.Trim(),
            Status = r.AssignedTechnicianId.HasValue ? WorkOrderStatus.Assigned : WorkOrderStatus.Open,
            AssignedTechnicianId = r.AssignedTechnicianId,
            CreatedByUserId = currentUser.RequireUserId(),
            CreatedAtUtc = now,
            DueAtUtc = r.DueAtUtc?.ToUniversalTime(),
            UpdatedAtUtc = now,
            // Raised by a person, not by anomaly processing - even when an anomaly is referenced for context.
            Source = WorkOrderSource.Manual,
        };
        // The diagnosis snapshot is stored with the ticket; a generation failure never loses the ticket.
        try
        {
            var anomalyContext = r.AnomalyId is { } aid ? await anomalies.GetAsync(aid, ct) : null;
            AnomalyWorkOrderService.ApplyDiagnosis(wo, await diagnosis.GenerateAsync(aircraft, anomalyContext, wo.Description, ct), now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            wo.DiagnosisStatus = DiagnosisStatus.Failed;
            wo.DiagnosisError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            wo.DiagnosisGeneratedAtUtc = now;
        }
        await maintenance.AddWorkOrderAsync(wo, ct);
        await auditor.RecordAsync("WorkOrderCreated", Entity, wo.Id.ToString(),
            new { wo.WorkOrderNumber, aircraft = aircraft.RegistrationNumber, priority = wo.Priority.ToString(), wo.Title, wo.AnomalyId, wo.PredictionId }, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return (await Load(wo.Id, ct)).ToDto(now, currentUser);
    }

    public async Task<WorkOrderDto> UpdateAsync(Guid id, UpdateWorkOrderRequest r, CancellationToken ct)
    {
        new Validator()
            .Must(r.ConcurrencyStamp.HasValue, "concurrencyStamp", "concurrencyStamp is required.")
            .MaxLength("title", r.Title, 200, "Title")
            .MaxLength("description", r.Description, 4000, "Description")
            .MaxLength("componentId", r.ComponentId, 64, "Component")
            .Must(r.Title is null || r.Title.Trim().Length > 0, "title", "Title cannot be empty.")
            .ThrowIfInvalid();
        var wo = await Load(id, ct);
        if (wo.IsTerminal) throw new BusinessRuleException($"A {wo.Status} work order cannot be edited.", "invalid_transition");
        if (wo.ConcurrencyStamp != r.ConcurrencyStamp)
            throw new ConflictException("The work order was changed by someone else. Reload and try again.", "concurrency_conflict");

        var changes = new Dictionary<string, object?>();
        if (r.Title is not null && r.Title.Trim() != wo.Title) { changes["title"] = r.Title.Trim(); wo.Title = r.Title.Trim(); }
        if (r.Description is not null && r.Description.Trim() != wo.Description) { changes["description"] = "updated"; wo.Description = r.Description.Trim(); }
        if (r.Priority is { } p && p != wo.Priority) { changes["priority"] = new { from = wo.Priority.ToString(), to = p.ToString() }; wo.Priority = p; }
        if (r.DueAtUtc is { } due && due.ToUniversalTime() != wo.DueAtUtc) { changes["dueAtUtc"] = due.ToUniversalTime(); wo.DueAtUtc = due.ToUniversalTime(); }
        if (r.ComponentId is not null && Clean(r.ComponentId) != wo.ComponentId) { changes["componentId"] = Clean(r.ComponentId); wo.ComponentId = Clean(r.ComponentId); }
        if (changes.Count == 0) return wo.ToDto(clock.UtcNow, currentUser);

        await auditor.RecordAsync("WorkOrderUpdated", Entity, wo.Id.ToString(), changes, ct);
        return await SaveAsync(wo, r.ConcurrencyStamp!.Value, ct);
    }

    public async Task<WorkOrderDto> AssignAsync(Guid id, AssignWorkOrderRequest r, CancellationToken ct)
    {
        new Validator().Must(r.TechnicianId.HasValue, "technicianId", "Technician is required.").ThrowIfInvalid();
        var tech = await EnsureTechnicianAsync(r.TechnicianId!.Value, ct);
        var wo = await Load(id, ct);
        var stamp = wo.ConcurrencyStamp;
        Transition(wo, WorkOrderWorkflow.Assign, () => wo.AssignedTechnicianId = tech.Id);
        await auditor.RecordAsync("WorkOrderAssigned", Entity, wo.Id.ToString(), new { technician = tech.DisplayName, technicianId = tech.Id }, ct);
        return await SaveAsync(wo, stamp, ct);
    }

    public async Task<WorkOrderDto> StartInspectionAsync(Guid id, CancellationToken ct)
    {
        var wo = await Load(id, ct);
        var stamp = wo.ConcurrencyStamp;
        Transition(wo, WorkOrderWorkflow.StartInspection);
        await auditor.RecordAsync("WorkOrderInspectionStarted", Entity, wo.Id.ToString(), null, ct);
        return await SaveAsync(wo, stamp, ct);
    }

    public async Task<WorkOrderDto> RecordInspectionAsync(Guid id, RecordInspectionRequest r, CancellationToken ct)
    {
        new Validator()
            .Required("inspectionFindings", r.InspectionFindings, "Inspection findings")
            .MaxLength("inspectionFindings", r.InspectionFindings, 4000, "Inspection findings")
            .ThrowIfInvalid();
        var wo = await Load(id, ct);
        var stamp = wo.ConcurrencyStamp;
        Transition(wo, WorkOrderWorkflow.RecordInspection, () => wo.InspectionFindings = r.InspectionFindings!.Trim());

        if (wo.Aircraft is { } aircraft)
        {
            aircraft.LastInspectionAtUtc = clock.UtcNow;
            aircraft.UpdatedAtUtc = clock.UtcNow;
        }
        await auditor.RecordAsync("WorkOrderInspectionRecorded", Entity, wo.Id.ToString(), new { findings = wo.InspectionFindings }, ct);
        return await SaveAsync(wo, stamp, ct);
    }

    public async Task<WorkOrderDto> ResolveAsync(Guid id, ResolveWorkOrderRequest r, CancellationToken ct)
    {
        new Validator()
            .Required("correctiveAction", r.CorrectiveAction, "Corrective action")
            .MaxLength("correctiveAction", r.CorrectiveAction, 4000, "Corrective action")
            .Must(r.ActualFailureConfirmed.HasValue, "actualFailureConfirmed", "Record whether an actual failure was confirmed.")
            .Required("resolutionNotes", r.ResolutionNotes, "Resolution notes")
            .MaxLength("resolutionNotes", r.ResolutionNotes, 4000, "Resolution notes")
            .Required("actionCode", r.ActionCode, "Maintenance action code")
            .Must((r.ResolutionNotes?.Trim().Length ?? 0) >= WorkOrderRules.MinimumMaintenanceNotesLength, "resolutionNotes",
                $"Maintenance notes must be at least {WorkOrderRules.MinimumMaintenanceNotesLength} characters.")
            .ThrowIfInvalid();
        var actionCode = r.ActionCode!.Trim().ToUpperInvariant();
        if ((await maintenance.ListActionCodesAsync(ct)).All(c => c.Code != actionCode))
            throw new ValidationException("actionCode", $"'{actionCode}' is not an approved maintenance action code.");
        var wo = await Load(id, ct);
        var stamp = wo.ConcurrencyStamp;
        var now = clock.UtcNow;
        Transition(wo, WorkOrderWorkflow.Resolve, () =>
        {
            wo.CorrectiveAction = r.CorrectiveAction!.Trim();
            wo.ActualFailureConfirmed = r.ActualFailureConfirmed;
            wo.ResolutionNotes = r.ResolutionNotes!.Trim();
            wo.ResolvedAtUtc = now;
            // Append, never overwrite: a ticket sent back for more work is reported again, and every
            // submission is kept. The identity is the authenticated user, never a client-supplied one.
            wo.Reports.Add(new WorkOrderReport
            {
                WorkOrderId = wo.Id,
                Sequence = wo.Reports.Count + 1,
                TechnicianUserId = currentUser.RequireUserId(),
                InspectionFindings = wo.InspectionFindings,
                CorrectiveAction = wo.CorrectiveAction,
                ResolutionNotes = wo.ResolutionNotes,
                ActualFailureConfirmed = r.ActualFailureConfirmed!.Value,
                SubmittedAtUtc = now,
                ActionCode = actionCode,
            });
        });
        await auditor.RecordAsync("WorkOrderResolved", Entity, wo.Id.ToString(),
            new { wo.CorrectiveAction, wo.ActualFailureConfirmed }, ct);
        return await SaveAsync(wo, stamp, ct);
    }

    public async Task<WorkOrderDto> ReopenAsync(Guid id, ReasonRequest r, CancellationToken ct)
    {
        new Validator().Required("reason", r.Reason, "Reason").MaxLength("reason", r.Reason, 1000, "Reason").ThrowIfInvalid();
        var wo = await Load(id, ct);
        var stamp = wo.ConcurrencyStamp;
        Transition(wo, WorkOrderWorkflow.Reopen, () => wo.ResolvedAtUtc = null);
        await auditor.RecordAsync("WorkOrderReopened", Entity, wo.Id.ToString(), new { reason = r.Reason }, ct);
        return await SaveAsync(wo, stamp, ct);
    }

    public async Task<IReadOnlyList<MaintenanceActionCodeDto>> ListActionCodesAsync(CancellationToken ct) =>
        (await maintenance.ListActionCodesAsync(ct)).Select(c => new MaintenanceActionCodeDto(c.Code, c.Description)).ToList();

    public async Task<WorkOrderDto> CloseAsync(Guid id, CloseWorkOrderRequest r, CancellationToken ct)
    {
        new Validator().MaxLength("resolutionNotes", r.ResolutionNotes, 4000, "Resolution notes").ThrowIfInvalid();
        var wo = await Load(id, ct);
        var stamp = wo.ConcurrencyStamp;
        var now = clock.UtcNow;
        Transition(wo, WorkOrderWorkflow.Close, () =>
        {
            // The dispatcher's review is stored on its own; the technician's report is never modified.
            if (!string.IsNullOrWhiteSpace(r.ResolutionNotes)) wo.DispatcherReviewNotes = r.ResolutionNotes.Trim();
            wo.ClosedAtUtc = now;
        });

        var failure = wo.ActualFailureConfirmed == true;
        // The verified outcome becomes part of the maintenance history used as labels for future model training.
        await maintenance.AddLogAsync(new MaintenanceLog
        {
            AircraftId = wo.AircraftId,
            ComponentId = wo.ComponentId,
            EventType = failure ? MaintenanceEventType.Unscheduled : MaintenanceEventType.Inspection,
            OccurredAtUtc = wo.ResolvedAtUtc ?? now,
            Description = $"{wo.WorkOrderNumber}: {wo.Title}. Findings: {wo.InspectionFindings}. Action: {wo.CorrectiveAction}" +
                          (wo.Reports.OrderBy(x => x.Sequence).LastOrDefault()?.ActionCode is { } code ? $" [{code}]" : ""),
            ActualFailureConfirmed = wo.ActualFailureConfirmed,
            IsVerified = true,
            Source = MaintenanceLogSource.WorkOrder,
            WorkOrderId = wo.Id,
            RecordedByUserId = currentUser.UserId,
            CreatedAtUtc = now,
        }, ct);

        if (wo.Prediction is { ActualOutcome: MaintenanceOutcome.Unknown } prediction && failure)
        {
            prediction.ActualOutcome = MaintenanceOutcome.UnscheduledMaintenance;
            prediction.OutcomeRecordedAtUtc = now;
            prediction.OutcomeWorkOrderId = wo.Id;
        }
        if (wo.Anomaly is { } anomaly && anomaly.InvestigationStatus is InvestigationStatus.New or InvestigationStatus.Acknowledged or InvestigationStatus.Investigating)
        {
            anomaly.InvestigationStatus = failure ? InvestigationStatus.ConfirmedIssue : InvestigationStatus.Dismissed;
            anomaly.InvestigationNotes = $"Closed via {wo.WorkOrderNumber}: {wo.CorrectiveAction}";
            anomaly.UpdatedAtUtc = now;
        }
        await auditor.RecordAsync("WorkOrderClosed", Entity, wo.Id.ToString(), new { actualFailureConfirmed = wo.ActualFailureConfirmed }, ct);
        return await SaveAsync(wo, stamp, ct);
    }

    public async Task<WorkOrderDto> CancelAsync(Guid id, ReasonRequest r, CancellationToken ct)
    {
        new Validator().Required("reason", r.Reason, "Reason").MaxLength("reason", r.Reason, 1000, "Reason").ThrowIfInvalid();
        var wo = await Load(id, ct);
        var stamp = wo.ConcurrencyStamp;
        Transition(wo, WorkOrderWorkflow.Cancel, () => wo.ResolutionNotes = $"Cancelled: {r.Reason!.Trim()}");
        await auditor.RecordAsync("WorkOrderCancelled", Entity, wo.Id.ToString(), new { reason = r.Reason }, ct);
        return await SaveAsync(wo, stamp, ct);
    }

    private void Transition(MaintenanceWorkOrder wo, string transition, Action? beforeApply = null)
    {
        // Dispatchers plan and close; only the technician the ticket is assigned to performs the work.
        if (WorkOrderWorkflow.TechnicianTransitions.Contains(transition) && !Mappers.IsAssignedTechnician(wo, currentUser))
            throw new ForbiddenException("Only the technician assigned to this work order can do that.");
        if (WorkOrderWorkflow.DispatcherTransitions.Contains(transition) && !currentUser.IsDispatcher)
            throw new ForbiddenException("Only a flight dispatcher can do that.");

        // A failed transition throws before SaveChanges, so partially applied field changes are discarded with the request scope.
        try
        {
            beforeApply?.Invoke();
            WorkOrderWorkflow.Apply(wo, transition);
        }
        catch (DomainException ex)
        {
            throw new BusinessRuleException(ex.Message, ex.Code);
        }
        wo.UpdatedAtUtc = clock.UtcNow;
    }

    private async Task<WorkOrderDto> SaveAsync(MaintenanceWorkOrder wo, Guid expectedStamp, CancellationToken ct)
    {
        maintenance.SetOriginalConcurrencyStamp(wo, expectedStamp);
        wo.ConcurrencyStamp = Guid.NewGuid();
        await unitOfWork.SaveChangesAsync(ct);
        return (await Load(wo.Id, ct)).ToDto(clock.UtcNow, currentUser);
    }

    private async Task<AppUser> EnsureTechnicianAsync(long id, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(id, ct);
        if (user is null || user.Status != UserStatus.Active || !user.HasRole(UserRole.MaintenanceTechnician))
            throw new ValidationException("technicianId", "Select an active maintenance technician.");
        return user;
    }

    /// <summary>Loads a work order visible to the caller: technicians get 404 for tickets not assigned to them.</summary>
    private async Task<MaintenanceWorkOrder> Load(Guid id, CancellationToken ct)
    {
        var wo = await maintenance.GetWorkOrderAsync(id, ct);
        if (wo is null || (!currentUser.IsDispatcher && wo.AssignedTechnicianId != currentUser.UserId))
            throw new NotFoundException("Work order", id);
        return wo;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public interface IMaintenanceLogService
{
    Task<PagedResult<MaintenanceLogDto>> ListAsync(MaintenanceLogFilter filter, PageRequest page, CancellationToken ct);
    Task<MaintenanceLogDto> CreateAsync(CreateMaintenanceLogRequest request, CancellationToken ct);
}

public sealed class MaintenanceLogService(
    IMaintenanceRepository maintenance, IAircraftRepository aircraftRepo, IActionAuditor auditor, IUnitOfWork unitOfWork,
    ICurrentUser currentUser, IClock clock) : IMaintenanceLogService
{
    public async Task<PagedResult<MaintenanceLogDto>> ListAsync(MaintenanceLogFilter filter, PageRequest page, CancellationToken ct) =>
        (await maintenance.ListLogsAsync(filter, page.Normalize(), ct)).Map(l => l.ToDto());

    public async Task<MaintenanceLogDto> CreateAsync(CreateMaintenanceLogRequest r, CancellationToken ct)
    {
        new Validator()
            .Must(r.AircraftId.HasValue, "aircraftId", "Aircraft is required.")
            .Must(r.EventType.HasValue, "eventType", "Event type is required.")
            .Must(r.OccurredAtUtc.HasValue, "occurredAtUtc", "Occurrence time is required.")
            .Must(r.OccurredAtUtc is null || r.OccurredAtUtc.Value.ToUniversalTime() <= clock.UtcNow.AddMinutes(5), "occurredAtUtc", "Occurrence time cannot be in the future.")
            .Required("description", r.Description, "Description").MaxLength("description", r.Description, 4000, "Description")
            .MaxLength("componentId", r.ComponentId, 64, "Component")
            .ThrowIfInvalid();
        var aircraft = await aircraftRepo.GetByIdAsync(r.AircraftId!.Value, ct) ?? throw new NotFoundException("Aircraft", r.AircraftId);
        if (!currentUser.IsDispatcher)
        {
            // Technicians may only log work for aircraft on one of their own open tickets.
            var me = currentUser.RequireUserId();
            if (!(await maintenance.ListOpenWorkOrdersAsync(aircraft.Id, ct)).Any(w => w.AssignedTechnicianId == me))
                throw new ForbiddenException("You can only log maintenance for aircraft on a work order assigned to you.");
        }
        var log = new MaintenanceLog
        {
            AircraftId = aircraft.Id,
            ComponentId = string.IsNullOrWhiteSpace(r.ComponentId) ? null : r.ComponentId.Trim(),
            EventType = r.EventType!.Value,
            OccurredAtUtc = r.OccurredAtUtc!.Value.ToUniversalTime(),
            Description = r.Description!.Trim(),
            ActualFailureConfirmed = r.ActualFailureConfirmed,
            IsVerified = r.IsVerified,
            Source = MaintenanceLogSource.Manual,
            RecordedByUserId = currentUser.UserId,
            CreatedAtUtc = clock.UtcNow,
        };
        if (log.EventType == MaintenanceEventType.Inspection &&
            (aircraft.LastInspectionAtUtc is null || aircraft.LastInspectionAtUtc < log.OccurredAtUtc))
            aircraft.LastInspectionAtUtc = log.OccurredAtUtc;

        await maintenance.AddLogAsync(log, ct);
        await auditor.RecordAsync("MaintenanceLogRecorded", nameof(MaintenanceLog), log.Id.ToString(),
            new { aircraft = aircraft.RegistrationNumber, eventType = log.EventType.ToString(), log.IsVerified, log.ActualFailureConfirmed }, ct);
        await unitOfWork.SaveChangesAsync(ct);
        log.Aircraft = aircraft;
        return log.ToDto();
    }
}
