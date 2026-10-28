using AFP.Domain.Entities;
using AFP.Domain.Enums;

namespace AFP.Domain.Rules;

/// <summary>
/// Presentation state derived from the stored <see cref="OperationalStatus"/> plus the current
/// maintenance workflow. The stored status is never changed by this rule — it only stops two very
/// different situations from being displayed identically.
/// </summary>
public enum DerivedFleetState
{
    /// <summary>Stored status is Available.</summary>
    Available,

    /// <summary>
    /// Registered but never released into service: stored status is UnderMaintenance, yet there is no
    /// open work order and no active inspection flag, and the status reason is the known
    /// auto-registration text. Nobody is working on the aircraft; it is simply awaiting a release decision.
    /// </summary>
    AwaitingRelease,

    /// <summary>Genuinely in maintenance: work has started on a work order, or an active inspection flag exists.</summary>
    UnderMaintenance,

    /// <summary>Stored status is Grounded.</summary>
    Grounded,
}

/// <summary>
/// Derives the fleet presentation state.
/// <para>
/// Why this exists: the ETL auto-registers an unknown tail number as <c>UnderMaintenance</c>
/// (bronze_to_silver_gold_postgres_stream.py) and <see cref="OperationalStatus"/> has no value for
/// "registered, not yet released". Every auto-registered aircraft therefore displayed as if it were
/// actively being worked on, even with zero work orders and zero flags.
/// </para>
/// <para>
/// This rule is presentation only. It never marks an aircraft Available, never infers release
/// eligibility from telemetry, and never bypasses the audited release workflow
/// (<c>POST /aircraft/{id}/status</c>), which remains the only way to change the stored status.
/// </para>
/// </summary>
public static class FleetStatusRules
{
    /// <summary>
    /// Status reasons written when an aircraft is created but not yet released into service.
    /// These are sentinels produced by this system, not user data:
    /// the first by the Spark ETL's auto-registration insert, the second by
    /// <c>AircraftService.CreateAsync</c>. Only an aircraft carrying one of them can be
    /// <see cref="DerivedFleetState.AwaitingRelease"/>, so an aircraft a technician genuinely placed
    /// under maintenance is never relabelled.
    /// </summary>
    /// <summary>
    /// Work-order statuses meaning maintenance has actually started and is not yet accepted as complete:
    /// IN_PROGRESS (InInspection) and REPORTED (Resolved - the technician's report awaits dispatcher acceptance).
    /// A NEW (Open) or ASSIGNED order means maintenance is due, not that it has begun (2026-10-27, Phase 7 rule 9.2).
    /// </summary>
    public static readonly WorkOrderStatus[] MaintenanceStartedStatuses = [WorkOrderStatus.InInspection, WorkOrderStatus.Resolved];

    public static readonly string[] AwaitingReleaseReasons =
    [
        "Auto-registered by the telemetry ETL;",
        "Newly registered; awaiting entry-into-service check.",
    ];

    /// <summary>
    /// Derives the state. <paramref name="workOrdersInProgress"/> counts this aircraft's work orders in
    /// <see cref="MaintenanceStartedStatuses"/>; <paramref name="activeInspectionFlags"/> its active flags.
    /// </summary>
    public static DerivedFleetState Derive(Aircraft aircraft, int workOrdersInProgress, int activeInspectionFlags) =>
        Derive(aircraft.OperationalStatus, aircraft.StatusReason, workOrdersInProgress, activeInspectionFlags);

    public static DerivedFleetState Derive(
        OperationalStatus stored, string? statusReason, int workOrdersInProgress, int activeInspectionFlags) => stored switch
        {
            OperationalStatus.Grounded => DerivedFleetState.Grounded,
            OperationalStatus.Available => DerivedFleetState.Available,
            OperationalStatus.UnderMaintenance when IsAwaitingRelease(statusReason, workOrdersInProgress, activeInspectionFlags)
                => DerivedFleetState.AwaitingRelease,
            _ => DerivedFleetState.UnderMaintenance,
        };

    private static bool IsAwaitingRelease(string? statusReason, int workOrdersInProgress, int activeInspectionFlags) =>
        workOrdersInProgress == 0
        && activeInspectionFlags == 0
        && statusReason is not null
        && AwaitingReleaseReasons.Any(marker => statusReason.StartsWith(marker, StringComparison.Ordinal));
}
