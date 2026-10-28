using AFP.Domain;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Domain.Rules;

namespace AFP.UnitTests;

public class WorkOrderWorkflowTests
{
    private static MaintenanceWorkOrder Order(WorkOrderStatus status, long? tech = null, string? findings = null) =>
        new() { Status = status, AssignedTechnicianId = tech, InspectionFindings = findings, WorkOrderNumber = "WO-1" };

    [Theory]
    [InlineData(WorkOrderStatus.Open, new[] { "assign", "cancel" })]
    [InlineData(WorkOrderStatus.Assigned, new[] { "assign", "start-inspection", "cancel" })]
    [InlineData(WorkOrderStatus.InInspection, new[] { "inspection", "resolve" })]
    [InlineData(WorkOrderStatus.Resolved, new[] { "reopen", "close" })]
    [InlineData(WorkOrderStatus.Closed, new string[0])]
    [InlineData(WorkOrderStatus.Cancelled, new string[0])]
    public void AllowedTransitions_match_status_machine(WorkOrderStatus status, string[] expected) =>
        Assert.Equal(expected.OrderBy(x => x), WorkOrderWorkflow.AllowedTransitions(status).OrderBy(x => x));

    [Fact]
    public void Happy_path_reaches_closed()
    {
        var wo = Order(WorkOrderStatus.Open, tech: 7);
        WorkOrderWorkflow.Apply(wo, WorkOrderWorkflow.Assign);
        WorkOrderWorkflow.Apply(wo, WorkOrderWorkflow.StartInspection);
        wo.InspectionFindings = "Seal wear found";
        WorkOrderWorkflow.Apply(wo, WorkOrderWorkflow.Resolve);
        WorkOrderWorkflow.Apply(wo, WorkOrderWorkflow.Close);
        Assert.Equal(WorkOrderStatus.Closed, wo.Status);
    }

    [Fact]
    public void Cannot_close_open_work_order()
    {
        var ex = Assert.Throws<DomainException>(() => WorkOrderWorkflow.Apply(Order(WorkOrderStatus.Open), WorkOrderWorkflow.Close));
        Assert.Equal("invalid_transition", ex.Code);
    }

    [Fact]
    public void Start_inspection_requires_technician()
    {
        var ex = Assert.Throws<DomainException>(() => WorkOrderWorkflow.Apply(Order(WorkOrderStatus.Assigned), WorkOrderWorkflow.StartInspection));
        Assert.Equal("business_rule_violation", ex.Code);
    }

    [Fact]
    public void Resolve_requires_findings()
    {
        Assert.Throws<DomainException>(() => WorkOrderWorkflow.Apply(Order(WorkOrderStatus.InInspection, 7), WorkOrderWorkflow.Resolve));
    }

    [Fact]
    public void Overdue_only_for_active_orders()
    {
        var now = DateTime.UtcNow;
        Assert.True(new MaintenanceWorkOrder { Status = WorkOrderStatus.Open, DueAtUtc = now.AddHours(-1) }.IsOverdue(now));
        Assert.False(new MaintenanceWorkOrder { Status = WorkOrderStatus.Closed, DueAtUtc = now.AddHours(-1) }.IsOverdue(now));
    }
}

public class AircraftRulesTests
{
    private static readonly Aircraft Plane = new() { RegistrationNumber = "DEMO-A01", OperationalStatus = OperationalStatus.Available };

    [Fact]
    public void Available_refused_while_grounding_flag_active()
    {
        var flags = new[] { new InspectionFlag { Severity = InspectionFlagSeverity.Grounding, Reason = "Leak" } };
        Assert.Throws<DomainException>(() => AircraftStatusPolicy.EnsureCanChange(Plane, OperationalStatus.Available, "Back in service", flags, []));
    }

    [Fact]
    public void Available_refused_while_critical_work_order_open()
    {
        var orders = new[] { new MaintenanceWorkOrder { Priority = WorkOrderPriority.Critical, Status = WorkOrderStatus.Open } };
        Assert.Throws<DomainException>(() => AircraftStatusPolicy.EnsureCanChange(Plane, OperationalStatus.Available, "Back", [], orders));
    }

    [Fact]
    public void Reason_is_required()
    {
        Assert.Throws<DomainException>(() => AircraftStatusPolicy.EnsureCanChange(Plane, OperationalStatus.Grounded, " ", [], []));
    }

    [Fact]
    public void Grounding_is_always_allowed_with_reason()
    {
        AircraftStatusPolicy.EnsureCanChange(Plane, OperationalStatus.Grounded, "Bird strike inspection", [], []);
    }

    [Fact]
    public void Advisory_never_declares_aircraft_safe()
    {
        var advisory = DispatchAdvisoryPolicy.Evaluate(Plane, [], [], 0, "Low");
        Assert.Equal(DispatchReadiness.NoKnownRestrictions, advisory.Readiness);
        Assert.DoesNotContain("safe", advisory.Readiness.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not certify airworthiness", advisory.Disclaimer);
    }

    [Fact]
    public void Advisory_restricted_by_high_risk_or_anomalies()
    {
        Assert.Equal(DispatchReadiness.Restricted, DispatchAdvisoryPolicy.Evaluate(Plane, [], [], 1, null).Readiness);
        Assert.Equal(DispatchReadiness.Restricted, DispatchAdvisoryPolicy.Evaluate(Plane, [], [], 0, "High").Readiness);
    }

    [Fact]
    public void Advisory_not_available_when_grounded()
    {
        var grounded = new Aircraft { OperationalStatus = OperationalStatus.Grounded, StatusReason = "AOG" };
        var advisory = DispatchAdvisoryPolicy.Evaluate(grounded, [], [], 0, "Low");
        Assert.Equal(DispatchReadiness.NotAvailable, advisory.Readiness);
    }
}

public class ModelLifecycleTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    [Fact]
    public void Full_lifecycle_and_previous_active_is_retired()
    {
        var previous = new ModelVersion { Status = ModelStatus.Active };
        var model = new ModelVersion { Status = ModelStatus.Trained, ArtifactSha256 = new string('a', 64) };
        ModelLifecycle.Register(model, Now);
        ModelLifecycle.Approve(model, 42, Now);
        ModelLifecycle.Activate(model, previous, Now);
        Assert.Equal(ModelStatus.Active, model.Status);
        Assert.Equal(ModelStatus.Retired, previous.Status);
    }

    [Fact]
    public void Cannot_activate_unapproved_model()
    {
        var model = new ModelVersion { Status = ModelStatus.Registered };
        Assert.Throws<DomainException>(() => ModelLifecycle.Activate(model, null, Now));
    }

    [Fact]
    public void Register_requires_artifact_hash()
    {
        Assert.Throws<DomainException>(() => ModelLifecycle.Register(new ModelVersion { Status = ModelStatus.Trained }, Now));
    }
}
