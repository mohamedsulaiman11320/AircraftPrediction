using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Domain.Rules;

namespace AFP.UnitTests;

/// <summary>
/// The derived state must separate "registered but never released" from "actually being worked on",
/// without ever changing the stored status or inventing an Available aircraft.
/// </summary>
public class FleetStatusRulesTests
{
    private const string EtlReason = "Auto-registered by the telemetry ETL; complete model/manufacturer and release via the application.";
    private const string AppReason = "Newly registered; awaiting entry-into-service check.";

    private static Aircraft Aircraft(OperationalStatus status, string? reason) =>
        new() { RegistrationNumber = "N120UA", OperationalStatus = status, StatusReason = reason };

    [Theory]
    [InlineData(EtlReason)]
    [InlineData(AppReason)]
    public void An_auto_registered_aircraft_with_no_open_work_is_awaiting_release(string reason) =>
        Assert.Equal(DerivedFleetState.AwaitingRelease,
            FleetStatusRules.Derive(Aircraft(OperationalStatus.UnderMaintenance, reason), workOrdersInProgress: 0, activeInspectionFlags: 0));

    /// <summary>
    /// Rule 9.2 (2026-10-27): creating or assigning a work order does not start maintenance. Replaces the earlier
    /// "any open work order means maintenance" assertion, which made every aircraft with a NEW automatic ticket
    /// look as if it were being worked on.
    /// </summary>
    [Fact]
    public void Work_in_progress_means_genuine_maintenance_even_for_an_auto_registered_aircraft() =>
        Assert.Equal(DerivedFleetState.UnderMaintenance,
            FleetStatusRules.Derive(Aircraft(OperationalStatus.UnderMaintenance, EtlReason), workOrdersInProgress: 1, activeInspectionFlags: 0));

    [Theory]
    [InlineData(WorkOrderStatus.InInspection, true)]   // IN_PROGRESS: work has started
    [InlineData(WorkOrderStatus.Resolved, true)]       // REPORTED: awaiting dispatcher acceptance, not complete
    [InlineData(WorkOrderStatus.Open, false)]          // NEW: due, not started
    [InlineData(WorkOrderStatus.Assigned, false)]      // ASSIGNED: a technician is named, nothing started
    [InlineData(WorkOrderStatus.Closed, false)]        // COMPLETED
    [InlineData(WorkOrderStatus.Cancelled, false)]
    public void Only_started_and_unaccepted_work_counts_as_maintenance(WorkOrderStatus status, bool started) =>
        Assert.Equal(started, FleetStatusRules.MaintenanceStartedStatuses.Contains(status));

    [Fact]
    public void An_active_inspection_flag_means_genuine_maintenance() =>
        Assert.Equal(DerivedFleetState.UnderMaintenance,
            FleetStatusRules.Derive(Aircraft(OperationalStatus.UnderMaintenance, EtlReason), workOrdersInProgress: 0, activeInspectionFlags: 1));

    /// <summary>The safety case: a technician's own reason must never be relabelled as awaiting release.</summary>
    [Theory]
    [InlineData("Engine vibration under investigation.")]
    [InlineData(null)]
    [InlineData("")]
    public void An_aircraft_a_human_placed_under_maintenance_stays_under_maintenance(string? reason) =>
        Assert.Equal(DerivedFleetState.UnderMaintenance,
            FleetStatusRules.Derive(Aircraft(OperationalStatus.UnderMaintenance, reason), workOrdersInProgress: 0, activeInspectionFlags: 0));

    [Fact]
    public void Grounded_is_never_softened_by_the_absence_of_work_orders() =>
        Assert.Equal(DerivedFleetState.Grounded,
            FleetStatusRules.Derive(Aircraft(OperationalStatus.Grounded, EtlReason), workOrdersInProgress: 0, activeInspectionFlags: 0));

    [Fact]
    public void Available_is_reported_as_available() =>
        Assert.Equal(DerivedFleetState.Available,
            FleetStatusRules.Derive(Aircraft(OperationalStatus.Available, null), workOrdersInProgress: 0, activeInspectionFlags: 0));

    /// <summary>The rule is presentation only: an awaiting-release aircraft is never promoted to Available.</summary>
    [Fact]
    public void Awaiting_release_is_not_available()
    {
        var derived = FleetStatusRules.Derive(Aircraft(OperationalStatus.UnderMaintenance, EtlReason), 0, 0);

        Assert.NotEqual(DerivedFleetState.Available, derived);
    }

    [Fact]
    public void The_stored_status_is_never_mutated()
    {
        var aircraft = Aircraft(OperationalStatus.UnderMaintenance, EtlReason);

        FleetStatusRules.Derive(aircraft, 0, 0);

        Assert.Equal(OperationalStatus.UnderMaintenance, aircraft.OperationalStatus);
        Assert.Equal(EtlReason, aircraft.StatusReason);
    }
}
