using AFP.Api.Security;
using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Application.Contracts;
using AFP.Application.Services;
using AFP.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AFP.Api.Controllers;

[ApiController]
[Route("api/v1/aircraft")]
[Authorize(Policy = Policies.DataAccess)]
public sealed class AircraftController(IAircraftService aircraft, IForecastService forecasts) : ControllerBase
{
    /// <summary>
    /// Future sensor forecast for one aircraft and horizon (OneDay | SevenDays | ThirtyDays). Returns
    /// <c>status: NotAvailable</c> with the reason when no forecasting model supplies it - never recorded telemetry.
    /// </summary>
    [HttpGet("{id:guid}/sensor-forecast")]
    public Task<SensorForecastDto> SensorForecast(Guid id, [FromQuery] SensorForecastHorizon? horizon, CancellationToken ct) =>
        forecasts.GetSensorForecastAsync(id, horizon, ct);

    [HttpGet]
    public Task<PagedResult<AircraftSummaryDto>> List(
        [FromQuery] string? search, [FromQuery] OperationalStatus? status, [FromQuery] string? fleetGroup, [FromQuery] string? model,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        aircraft.ListAsync(new AircraftFilter(search, status, fleetGroup, model), new PageRequest(page, pageSize), ct);

    [HttpGet("fleet-groups")]
    public Task<IReadOnlyList<string>> FleetGroups(CancellationToken ct) => aircraft.ListFleetGroupsAsync(ct);

    [HttpGet("{id:guid}"), Authorize(Policy = Policies.Dispatcher)]
    public Task<AircraftDetailDto> Get(Guid id, CancellationToken ct) => aircraft.GetAsync(id, ct);

    [HttpPost, Authorize(Policy = Policies.DataEngineer)]
    [ProducesResponseType<AircraftDetailDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(UpsertAircraftRequest request, CancellationToken ct)
    {
        var created = await aircraft.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}"), Authorize(Policy = Policies.DataEngineer)]
    public Task<AircraftDetailDto> Update(Guid id, UpsertAircraftRequest request, CancellationToken ct) => aircraft.UpdateAsync(id, request, ct);

    [HttpPost("{id:guid}/status"), Authorize(Policy = Policies.Dispatcher)]
    public Task<AircraftDetailDto> ChangeStatus(Guid id, ChangeAircraftStatusRequest request, CancellationToken ct) =>
        aircraft.ChangeStatusAsync(id, request, ct);

    [HttpGet("{id:guid}/maintenance-history"), Authorize(Policy = Policies.Dispatcher)]
    public Task<PagedResult<MaintenanceLogDto>> MaintenanceHistory(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        aircraft.GetMaintenanceHistoryAsync(id, new PageRequest(page, pageSize), ct);

    [HttpGet("{id:guid}/inspection-flags"), Authorize(Policy = Policies.Dispatcher)]
    public Task<IReadOnlyList<InspectionFlagDto>> Flags(Guid id, [FromQuery] bool activeOnly = true, CancellationToken ct = default) =>
        aircraft.ListFlagsAsync(id, activeOnly, ct);

    [HttpPost("{id:guid}/inspection-flags"), Authorize(Policy = Policies.Dispatcher)]
    [ProducesResponseType<InspectionFlagDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> RaiseFlag(Guid id, RaiseInspectionFlagRequest request, CancellationToken ct)
    {
        var flag = await aircraft.RaiseFlagAsync(id, request, ct);
        return Created($"/api/v1/aircraft/{id}/inspection-flags", flag);
    }
}

[ApiController]
[Route("api/v1/inspection-flags")]
[Authorize(Policy = Policies.Dispatcher)]
public sealed class InspectionFlagsController(IAircraftService aircraft) : ControllerBase
{
    [HttpPost("{flagId:guid}/clear")]
    public Task<InspectionFlagDto> Clear(Guid flagId, ClearInspectionFlagRequest request, CancellationToken ct) =>
        aircraft.ClearFlagAsync(flagId, request, ct);
}

[ApiController]
[Route("api/v1/anomalies")]
[Authorize(Policy = Policies.DataAccess)]
public sealed class AnomaliesController(IAnomalyService anomalies, IAnomalyWorkOrderService anomalyWorkOrders) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<AnomalyDto>> List(
        [FromQuery] Guid? aircraftId, [FromQuery] InvestigationStatus? status, [FromQuery] AnomalySeverity? severity,
        [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, [FromQuery] AnomalySource? source = null, CancellationToken ct = default) =>
        anomalies.ListAsync(new AnomalyFilter(aircraftId, status, severity, fromUtc?.ToUniversalTime(), toUtc?.ToUniversalTime(), source),
            new PageRequest(page, pageSize), ct);

    [HttpGet("summary")]
    public Task<AnomalySummaryDto> Summary([FromQuery] int days = 30, CancellationToken ct = default) => anomalies.GetSummaryAsync(days, ct);

    [HttpGet("{id:long}")]
    public Task<AnomalyDetailDto> Get(long id, CancellationToken ct) => anomalies.GetAsync(id, ct);

    /// <summary>
    /// Read-only preview of the work orders automatic generation would raise, with a reason for every
    /// anomaly it would skip. Performs no write of any kind.
    /// </summary>
    [HttpGet("work-order-dry-run"), Authorize(Policy = Policies.Dispatcher)]
    public Task<AnomalyWorkOrderDryRunDto> WorkOrderDryRun(CancellationToken ct) =>
        anomalyWorkOrders.DryRunAsync(ct);

    [HttpPost("{id:long}/acknowledge"), Authorize(Policy = Policies.Dispatcher)]
    public Task<AnomalyDto> Acknowledge(long id, AcknowledgeAnomalyRequest request, CancellationToken ct) => anomalies.AcknowledgeAsync(id, request, ct);

    [HttpPost("{id:long}/investigation"), Authorize(Policy = Policies.Dispatcher)]
    public Task<AnomalyDto> Investigate(long id, InvestigateAnomalyRequest request, CancellationToken ct) => anomalies.InvestigateAsync(id, request, ct);
}

[ApiController]
[Route("api/v1/predictions")]
[Authorize(Policy = Policies.DataEngineer)]
public sealed class PredictionsController(IPredictionService predictions) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<PredictionDto>> List(
        [FromQuery] Guid? aircraftId, [FromQuery] string? riskCategory, [FromQuery] PredictionBasis? basis,
        [FromQuery] ApprovalStatus? approvalStatus, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        predictions.ListAsync(new PredictionFilter(aircraftId, riskCategory, basis, approvalStatus), new PageRequest(page, pageSize), ct);

    [HttpGet("aircraft/{aircraftId:guid}")]
    public Task<IReadOnlyList<PredictionDto>> ForAircraft(Guid aircraftId, CancellationToken ct) => predictions.ListForAircraftAsync(aircraftId, ct);

    [HttpGet("metadata")]
    public Task<PredictionMetadataDto> Metadata(CancellationToken ct) => predictions.GetMetadataAsync(ct);

    [HttpPost("{id:guid}/review"), Authorize(Policy = Policies.Unassigned)]
    public Task<PredictionDto> Review(Guid id, ReviewPredictionRequest request, CancellationToken ct) => predictions.ReviewAsync(id, request, ct);

    [HttpPost("{id:guid}/outcome"), Authorize(Policy = Policies.Unassigned)]
    public Task<PredictionDto> Outcome(Guid id, RecordOutcomeRequest request, CancellationToken ct) => predictions.RecordOutcomeAsync(id, request, ct);
}

[ApiController]
[Route("api/v1/work-orders")]
[Authorize(Policy = Policies.DispatcherOrTechnician)]
public sealed class WorkOrdersController(IWorkOrderService workOrders, ICurrentUser currentUser) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<WorkOrderDto>> List(
        [FromQuery] Guid? aircraftId, [FromQuery] WorkOrderStatus? status, [FromQuery] WorkOrderPriority? priority,
        [FromQuery] bool assignedToMe = false, [FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        [FromQuery] WorkOrderSource? source = null, [FromQuery] long? anomalyId = null, CancellationToken ct = default) =>
        workOrders.ListAsync(new WorkOrderFilter(aircraftId, status, priority, assignedToMe ? currentUser.UserId : null, Source: source,
            AnomalyId: anomalyId),
            new PageRequest(page, pageSize), ct);

    [HttpGet("{id:guid}")]
    public Task<WorkOrderDetailDto> Get(Guid id, CancellationToken ct) => workOrders.GetAsync(id, ct);

    [HttpPost, Authorize(Policy = Policies.Dispatcher)]
    [ProducesResponseType<WorkOrderDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreateWorkOrderRequest request, CancellationToken ct)
    {
        var created = await workOrders.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:guid}"), Authorize(Policy = Policies.Dispatcher)]
    public Task<WorkOrderDto> Update(Guid id, UpdateWorkOrderRequest request, CancellationToken ct) => workOrders.UpdateAsync(id, request, ct);

    [HttpPost("{id:guid}/assign"), Authorize(Policy = Policies.Dispatcher)]
    public Task<WorkOrderDto> Assign(Guid id, AssignWorkOrderRequest request, CancellationToken ct) => workOrders.AssignAsync(id, request, ct);

    [HttpPost("{id:guid}/start-inspection")]
    public Task<WorkOrderDto> StartInspection(Guid id, CancellationToken ct) => workOrders.StartInspectionAsync(id, ct);

    [HttpPost("{id:guid}/inspection")]
    public Task<WorkOrderDto> RecordInspection(Guid id, RecordInspectionRequest request, CancellationToken ct) =>
        workOrders.RecordInspectionAsync(id, request, ct);

    [HttpPost("{id:guid}/resolve")]
    public Task<WorkOrderDto> Resolve(Guid id, ResolveWorkOrderRequest request, CancellationToken ct) => workOrders.ResolveAsync(id, request, ct);

    [HttpPost("{id:guid}/reopen"), Authorize(Policy = Policies.Dispatcher)]
    public Task<WorkOrderDto> Reopen(Guid id, ReasonRequest request, CancellationToken ct) => workOrders.ReopenAsync(id, request, ct);

    [HttpPost("{id:guid}/close"), Authorize(Policy = Policies.Dispatcher)]
    public Task<WorkOrderDto> Close(Guid id, CloseWorkOrderRequest request, CancellationToken ct) => workOrders.CloseAsync(id, request, ct);

    [HttpPost("{id:guid}/cancel"), Authorize(Policy = Policies.Dispatcher)]
    public Task<WorkOrderDto> Cancel(Guid id, ReasonRequest request, CancellationToken ct) => workOrders.CancelAsync(id, request, ct);
}

[ApiController]
[Route("api/v1/maintenance-logs")]
[Authorize(Policy = Policies.DispatcherOrTechnician)]
public sealed class MaintenanceLogsController(IMaintenanceLogService logs) : ControllerBase
{
    [HttpGet, Authorize(Policy = Policies.Dispatcher)]
    public Task<PagedResult<MaintenanceLogDto>> List(
        [FromQuery] Guid? aircraftId, [FromQuery] MaintenanceEventType? eventType, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        logs.ListAsync(new MaintenanceLogFilter(aircraftId, eventType, fromUtc?.ToUniversalTime(), toUtc?.ToUniversalTime()),
            new PageRequest(page, pageSize), ct);

    [HttpPost]
    [ProducesResponseType<MaintenanceLogDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreateMaintenanceLogRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await logs.CreateAsync(request, ct));
}
