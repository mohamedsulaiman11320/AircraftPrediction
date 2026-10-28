using AFP.Domain.Entities;
using AFP.Domain.Enums;

namespace AFP.Domain.Rules;

/// <summary>
/// Business rules for operational status. Status changes are always human decisions;
/// ML results can only add information, never make an aircraft dispatchable.
/// </summary>
public static class AircraftStatusPolicy
{
    public static void EnsureCanChange(
        Aircraft aircraft, OperationalStatus target, string reason,
        IReadOnlyCollection<InspectionFlag> activeFlags, IReadOnlyCollection<MaintenanceWorkOrder> openWorkOrders)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("business_rule_violation", "A reason is required to change operational status.");

        if (target != OperationalStatus.Available) return;

        if (activeFlags.Any(f => f.Severity == InspectionFlagSeverity.Grounding))
            throw new DomainException("business_rule_violation",
                "Aircraft cannot be made Available while a Grounding inspection flag is active.");
        if (openWorkOrders.Any(w => w.Priority == WorkOrderPriority.Critical))
            throw new DomainException("business_rule_violation",
                "Aircraft cannot be made Available while a Critical work order is open.");
    }
}

public sealed record DispatchAdvisory(DispatchReadiness Readiness, IReadOnlyList<string> Reasons, string Disclaimer);

/// <summary>Summarises known restrictions for dispatchers. It never declares an aircraft safe.</summary>
public static class DispatchAdvisoryPolicy
{
    public const string Disclaimer =
        "Advisory only. This summary lists known recorded restrictions; it does not certify airworthiness. " +
        "Dispatch decisions remain with authorised personnel under the approved maintenance programme. " +
        "Machine-learning outputs never make an aircraft dispatchable.";

    public static DispatchAdvisory Evaluate(
        Aircraft aircraft, IReadOnlyCollection<InspectionFlag> activeFlags,
        IReadOnlyCollection<MaintenanceWorkOrder> openWorkOrders, int openHighSeverityAnomalies,
        string? latestRiskCategory)
    {
        var reasons = new List<string>();
        if (aircraft.OperationalStatus != OperationalStatus.Available)
        {
            reasons.Add($"Operational status is {aircraft.OperationalStatus}" +
                        (string.IsNullOrWhiteSpace(aircraft.StatusReason) ? "." : $": {aircraft.StatusReason}"));
            return new DispatchAdvisory(DispatchReadiness.NotAvailable, reasons, Disclaimer);
        }

        foreach (var flag in activeFlags.Where(f => f.Severity != InspectionFlagSeverity.Advisory))
            reasons.Add($"{flag.Severity} inspection flag: {flag.Reason}");
        foreach (var wo in openWorkOrders.Where(w => w.Priority is WorkOrderPriority.High or WorkOrderPriority.Critical))
            reasons.Add($"Open {wo.Priority} work order {wo.WorkOrderNumber}: {wo.Title}");
        if (openHighSeverityAnomalies > 0)
            reasons.Add($"{openHighSeverityAnomalies} uninvestigated high-severity anomaly result(s).");
        if (latestRiskCategory == "High")
            reasons.Add("Latest risk indicator is High (requires engineering review).");

        var advisories = activeFlags.Where(f => f.Severity == InspectionFlagSeverity.Advisory).ToList();
        var readiness = reasons.Count > 0 ? DispatchReadiness.Restricted : DispatchReadiness.NoKnownRestrictions;
        reasons.AddRange(advisories.Select(f => $"Advisory: {f.Reason}"));
        return new DispatchAdvisory(readiness, reasons, Disclaimer);
    }
}
