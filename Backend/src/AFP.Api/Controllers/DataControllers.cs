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
[Route("api/v1/telemetry")]
[Authorize(Policy = Policies.DataAccess)]
public sealed class TelemetryController(ITelemetryService telemetry, IImportDeletionService deletion) : ControllerBase
{
    private const long MaxUploadBytes = 210L * 1024 * 1024;

    [HttpGet]
    public Task<PagedResult<TelemetryRecordDto>> Query(
        [FromQuery] Guid aircraftId, [FromQuery] string? sensorId, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc,
        [FromQuery] QualityStatus? qualityStatus, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
        if (aircraftId == Guid.Empty) throw new ValidationException("aircraftId", "aircraftId is required.");
        return telemetry.QueryAsync(new TelemetryFilter(aircraftId, sensorId, fromUtc?.ToUniversalTime(), toUtc?.ToUniversalTime(), qualityStatus),
            new PageRequest(page, pageSize), ct);
    }

    [HttpGet("series")]
    public Task<TelemetrySeriesDto> Series(
        [FromQuery] Guid aircraftId, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] string? sensorId,
        [FromQuery] int bucketMinutes = 60, CancellationToken ct = default)
    {
        if (aircraftId == Guid.Empty) throw new ValidationException("aircraftId", "aircraftId is required.");
        return telemetry.GetSeriesAsync(aircraftId, fromUtc?.ToUniversalTime(), toUtc?.ToUniversalTime(), sensorId, bucketMinutes, ct);
    }

    /// <summary>Uploads a CSV for asynchronous validation and loading. Poll the returned statusUrl.</summary>
    [HttpPost("imports"), Authorize(Policy = Policies.DataEngineer)]
    [RequestSizeLimit(MaxUploadBytes), RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<JobAcceptedDto>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Import(IFormFile? file, CancellationToken ct)
    {
        if (file is null) throw new ValidationException("file", "Attach a CSV file in the 'file' form field.");
        await using var stream = file.OpenReadStream();
        var accepted = await telemetry.StartImportAsync(new UploadedFile(file.FileName, file.Length, stream), ct);
        return Accepted(accepted.StatusUrl, accepted);
    }

    [HttpGet("imports"), Authorize(Policy = Policies.DataEngineer)]
    public Task<PagedResult<TelemetryImportDto>> Imports([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        telemetry.ListImportsAsync(new PageRequest(page, pageSize), ct);

    [HttpGet("imports/{id:guid}"), Authorize(Policy = Policies.DataEngineer)]
    public Task<TelemetryImportDto> ImportStatus(Guid id, CancellationToken ct) => telemetry.GetImportAsync(id, ct);

    [HttpGet("imports/{id:guid}/rejected-rows"), Authorize(Policy = Policies.DataEngineer)]
    public Task<PagedResult<RejectedRowDto>> RejectedRows(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        telemetry.ListRejectedRowsAsync(id, new PageRequest(page, pageSize), ct);

    /// <summary>
    /// Uploads a raw JSON telemetry file to MinIO bronze/telemetry for the Spark ETL. Returns 202: the import is
    /// Queued until the ETL reports Processing/Completed/Failed (poll statusUrl).
    /// </summary>
    [HttpPost("raw-uploads"), Authorize(Policy = Policies.DataEngineer)]
    [RequestSizeLimit(MaxUploadBytes), RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<JobAcceptedDto>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> RawUpload(IFormFile? file, CancellationToken ct)
    {
        if (file is null) throw new ValidationException("file", "Attach a JSON file in the 'file' form field.");
        await using var stream = file.OpenReadStream();
        var accepted = await telemetry.StartRawJsonImportAsync(new UploadedFile(file.FileName, file.Length, stream), ct);
        return Accepted(accepted.StatusUrl, accepted);
    }

    /// <summary>Re-submits a failed raw JSON import to the ETL.</summary>
    [HttpPost("imports/{id:guid}/retry"), Authorize(Policy = Policies.DataEngineer)]
    public Task<TelemetryImportDto> Retry(Guid id, CancellationToken ct) => telemetry.RetryImportAsync(id, ct);

    /// <summary>What deleting this import would remove, and what it keeps. Changes nothing.</summary>
    [HttpGet("imports/{id:guid}/deletion-plan"), Authorize(Policy = Policies.DataEngineer)]
    public Task<ImportDeletionPlanDto> DeletionPlan(Guid id, CancellationToken ct) => deletion.PlanAsync(id, ct);

    /// <summary>
    /// Deletes an import and only the resources provably owned by it. Idempotent: re-running after a
    /// partial failure resumes. Returns <c>completed=false</c> with the remaining work when object storage
    /// could not be fully cleaned - never a bare success.
    /// </summary>
    [HttpDelete("imports/{id:guid}"), Authorize(Policy = Policies.DataEngineer)]
    public Task<ImportDeletionResultDto> DeleteImport(Guid id, CancellationToken ct) => deletion.DeleteAsync(id, ct);

    [HttpGet("data-quality"), Authorize(Policy = Policies.DataEngineer)]
    public Task<DataQualityReportDto> DataQuality([FromQuery] Guid? aircraftId, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, CancellationToken ct) =>
        telemetry.GetDataQualityAsync(aircraftId, fromUtc?.ToUniversalTime(), toUtc?.ToUniversalTime(), ct);
}

[ApiController]
[Route("api/v1/pipelines")]
[Authorize(Policy = Policies.DataEngineer)]
public sealed class PipelinesController(IPipelineService pipelines) : ControllerBase
{
    [HttpGet("runs")]
    public Task<PagedResult<PipelineRunDto>> Runs(
        [FromQuery] PipelineJobType? jobType, [FromQuery] PipelineRunStatus? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        pipelines.ListAsync(jobType, status, new PageRequest(page, pageSize), ct);

    [HttpGet("runs/{id:guid}")]
    public Task<PipelineRunDto> Run(Guid id, CancellationToken ct) => pipelines.GetAsync(id, ct);

    /// <summary>Queues an ML job for the Python worker and returns immediately with a status URL.</summary>
    [HttpPost("jobs")]
    [ProducesResponseType<JobAcceptedDto>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> StartJob(StartJobRequest request, CancellationToken ct)
    {
        var accepted = await pipelines.StartJobAsync(request, ct);
        return Accepted(accepted.StatusUrl, accepted);
    }

    [HttpPost("runs/{id:guid}/cancel")]
    public Task<PipelineRunDto> Cancel(Guid id, CancellationToken ct) => pipelines.CancelAsync(id, ct);

    [HttpGet("dataset-audit")]
    public Task<PipelineRunDto> DatasetAudit(CancellationToken ct) => pipelines.GetLatestDatasetAuditAsync(ct);
}

[ApiController]
[Route("api/v1/models")]
[Authorize(Policy = Policies.DataEngineer)]
public sealed class ModelsController(IModelService models) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<ModelVersionDto>> List([FromQuery] ModelType? modelType, [FromQuery] ModelStatus? status, CancellationToken ct) =>
        models.ListAsync(modelType, status, ct);

    [HttpGet("{id:guid}")]
    public Task<ModelVersionDto> Get(Guid id, CancellationToken ct) => models.GetAsync(id, ct);

    [HttpGet("{id:guid}/evaluation")]
    public Task<ModelEvaluationDto> Evaluation(Guid id, CancellationToken ct) => models.GetEvaluationAsync(id, ct);

    [HttpPost("{id:guid}/register")]
    public Task<ModelVersionDto> Register(Guid id, CancellationToken ct) => models.RegisterAsync(id, ct);

    [HttpPost("{id:guid}/approve"), Authorize(Policy = Policies.ModelApproval)]
    public Task<ModelVersionDto> Approve(Guid id, NotesRequest request, CancellationToken ct) => models.ApproveAsync(id, request, ct);

    [HttpPost("{id:guid}/reject"), Authorize(Policy = Policies.ModelApproval)]
    public Task<ModelVersionDto> Reject(Guid id, NotesRequest request, CancellationToken ct) => models.RejectAsync(id, request, ct);

    [HttpPost("{id:guid}/activate")]
    public Task<ModelVersionDto> Activate(Guid id, CancellationToken ct) => models.ActivateAsync(id, ct);

    [HttpPost("{id:guid}/retire")]
    public Task<ModelVersionDto> Retire(Guid id, CancellationToken ct) => models.RetireAsync(id, ct);
}

/// <summary>
/// The isolated development (synthetic) environment. Read candidates, evaluations, runs and development
/// predictions; queue development jobs. No register/approve/activate exists here by design.
/// </summary>
[ApiController]
[Route("api/v1/development")]
[Authorize(Policy = Policies.DataEngineer)]
public sealed class DevelopmentController(IDevelopmentLabService lab) : ControllerBase
{
    [HttpGet]
    public DevelopmentStatusDto Status() => lab.GetStatus();

    [HttpGet("models")]
    public Task<IReadOnlyList<ModelVersionDto>> Models(CancellationToken ct) => lab.ListModelsAsync(ct);

    [HttpGet("models/{id:guid}/evaluation")]
    public Task<ModelEvaluationDto> Evaluation(Guid id, CancellationToken ct) => lab.GetEvaluationAsync(id, ct);

    [HttpGet("runs")]
    public Task<IReadOnlyList<PipelineRunDto>> Runs(CancellationToken ct) => lab.ListRunsAsync(ct);

    [HttpGet("runs/{id:guid}")]
    public Task<PipelineRunDto> Run(Guid id, CancellationToken ct) => lab.GetRunAsync(id, ct);

    [HttpPost("jobs")]
    public async Task<IActionResult> QueueJob(DevelopmentJobRequest request, CancellationToken ct)
    {
        var accepted = await lab.QueueJobAsync(request, ct);
        return Accepted(accepted.StatusUrl, accepted);
    }

    [HttpGet("predictions")]
    public Task<IReadOnlyList<PredictionDto>> Predictions(CancellationToken ct) => lab.ListPredictionsAsync(ct);
}

[ApiController]
[Route("api/v1/dashboards")]
public sealed class DashboardsController(IDashboardService dashboards) : ControllerBase
{
    [HttpGet("dispatcher"), Authorize(Policy = Policies.Dispatcher)]
    public Task<DispatcherDashboardDto> Dispatcher([FromQuery] string? fleetGroup, CancellationToken ct) =>
        dashboards.GetDispatcherAsync(fleetGroup, ct);

    [HttpGet("technician"), Authorize(Policy = Policies.Technician)]
    public Task<TechnicianDashboardDto> Technician(CancellationToken ct) => dashboards.GetTechnicianAsync(ct);

    [HttpGet("data-engineer"), Authorize(Policy = Policies.DataEngineer)]
    public Task<DataEngineerDashboardDto> DataEngineer(CancellationToken ct) => dashboards.GetDataEngineerAsync(ct);

    [HttpGet("leadership"), Authorize(Policy = Policies.Unassigned)]
    public Task<LeadershipDashboardDto> Leadership(CancellationToken ct) => dashboards.GetLeadershipAsync(ct);
}

[ApiController]
[Route("api/v1/reports")]
public sealed class ReportsController(IReportService reports) : ControllerBase
{
    [HttpGet("fleet-health"), Authorize(Policy = Policies.Dispatcher)]
    public async Task<IActionResult> FleetHealth([FromQuery] string? format, CancellationToken ct) =>
        Respond("fleet-health", await reports.GetFleetHealthAsync(ct), format);

    [HttpGet("maintenance-history"), Authorize(Policy = Policies.Dispatcher)]
    public async Task<IActionResult> MaintenanceHistory([FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, [FromQuery] string? format, CancellationToken ct) =>
        Respond("maintenance-history", await reports.GetMaintenanceHistoryAsync(fromUtc?.ToUniversalTime(), toUtc?.ToUniversalTime(), ct), format);

    [HttpGet("data-quality"), Authorize(Policy = Policies.DataEngineer)]
    public async Task<IActionResult> DataQuality([FromQuery] string? format, CancellationToken ct) =>
        Respond("data-quality", await reports.GetDataQualityAsync(ct), format);

    [HttpGet("model-performance"), Authorize(Policy = Policies.DataEngineer)]
    public async Task<IActionResult> ModelPerformance([FromQuery] string? format, CancellationToken ct) =>
        Respond("model-performance", await reports.GetModelPerformanceAsync(ct), format);

    private IActionResult Respond(string name, object report, string? format)
    {
        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            var file = reports.ToCsv(name, report);
            return File(file.Content, file.ContentType, file.FileName);
        }
        if (format is not null && !string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("format", "format must be 'json' or 'csv'.");
        return Ok(report);
    }
}
