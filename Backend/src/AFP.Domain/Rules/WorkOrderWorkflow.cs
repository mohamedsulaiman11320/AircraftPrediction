using AFP.Domain.Entities;
using AFP.Domain.Enums;

namespace AFP.Domain.Rules;

/// <summary>Named transitions exposed to clients and the status machine that validates them.</summary>
public static class WorkOrderWorkflow
{
    public const string Assign = "assign";
    public const string StartInspection = "start-inspection";
    public const string RecordInspection = "inspection";
    public const string Resolve = "resolve";
    public const string Reopen = "reopen";
    public const string Close = "close";
    public const string Cancel = "cancel";

    private static readonly Dictionary<string, (WorkOrderStatus[] From, WorkOrderStatus To)> Transitions = new()
    {
        [Assign] = ([WorkOrderStatus.Open, WorkOrderStatus.Assigned], WorkOrderStatus.Assigned),
        [StartInspection] = ([WorkOrderStatus.Assigned], WorkOrderStatus.InInspection),
        [RecordInspection] = ([WorkOrderStatus.InInspection], WorkOrderStatus.InInspection),
        [Resolve] = ([WorkOrderStatus.InInspection], WorkOrderStatus.Resolved),
        // Dispatcher sends REPORTED work back for more: it returns to ASSIGNED (the technician must
        // start again), and the earlier report is kept as an immutable WorkOrderReport, never overwritten.
        [Reopen] = ([WorkOrderStatus.Resolved], WorkOrderStatus.Assigned),
        [Close] = ([WorkOrderStatus.Resolved], WorkOrderStatus.Closed),
        [Cancel] = ([WorkOrderStatus.Open, WorkOrderStatus.Assigned], WorkOrderStatus.Cancelled),
    };

    /// <summary>Transitions a Flight Dispatcher may perform (planning and closure).</summary>
    public static readonly IReadOnlySet<string> DispatcherTransitions = new HashSet<string> { Assign, Reopen, Close, Cancel };

    /// <summary>Transitions only the technician assigned to the work order may perform (the hands-on work).</summary>
    public static readonly IReadOnlySet<string> TechnicianTransitions = new HashSet<string> { StartInspection, RecordInspection, Resolve };

    public static IReadOnlyList<string> AllowedTransitions(WorkOrderStatus status) =>
        Transitions.Where(t => t.Value.From.Contains(status)).Select(t => t.Key).ToList();

    /// <summary>Transitions valid for the status AND permitted to the caller.</summary>
    public static IReadOnlyList<string> AllowedTransitions(WorkOrderStatus status, bool isDispatcher, bool isAssignedTechnician) =>
        AllowedTransitions(status)
            .Where(t => (isDispatcher && DispatcherTransitions.Contains(t)) || (isAssignedTechnician && TechnicianTransitions.Contains(t)))
            .ToList();

    /// <summary>Validates the transition and returns the resulting status.</summary>
    public static WorkOrderStatus Apply(MaintenanceWorkOrder workOrder, string transition)
    {
        if (!Transitions.TryGetValue(transition, out var rule))
            throw new DomainException("invalid_transition", $"Unknown work-order transition '{transition}'.");
        if (!rule.From.Contains(workOrder.Status))
            throw new DomainException("invalid_transition",
                $"Cannot {transition} a work order in status {workOrder.Status}.");

        switch (transition)
        {
            case StartInspection when workOrder.AssignedTechnicianId is null:
                throw new DomainException("business_rule_violation", "A technician must be assigned before inspection starts.");
            case Resolve when string.IsNullOrWhiteSpace(workOrder.InspectionFindings):
                throw new DomainException("business_rule_violation", "Inspection findings must be recorded before resolving.");
        }

        workOrder.Status = rule.To;
        return rule.To;
    }
}
