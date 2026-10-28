using System.Text.RegularExpressions;
using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Application.Contracts;
using AFP.Application.Mapping;
using AFP.Domain;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Domain.Rules;

namespace AFP.Application.Services;

public interface IAircraftService
{
    Task<PagedResult<AircraftSummaryDto>> ListAsync(AircraftFilter filter, PageRequest page, CancellationToken ct);
    Task<IReadOnlyList<string>> ListFleetGroupsAsync(CancellationToken ct);
    Task<AircraftDetailDto> GetAsync(Guid id, CancellationToken ct);
    Task<AircraftDetailDto> CreateAsync(UpsertAircraftRequest request, CancellationToken ct);
    Task<AircraftDetailDto> UpdateAsync(Guid id, UpsertAircraftRequest request, CancellationToken ct);
    Task<AircraftDetailDto> ChangeStatusAsync(Guid id, ChangeAircraftStatusRequest request, CancellationToken ct);
    Task<PagedResult<MaintenanceLogDto>> GetMaintenanceHistoryAsync(Guid id, PageRequest page, CancellationToken ct);
    Task<IReadOnlyList<InspectionFlagDto>> ListFlagsAsync(Guid id, bool activeOnly, CancellationToken ct);
    Task<InspectionFlagDto> RaiseFlagAsync(Guid id, RaiseInspectionFlagRequest request, CancellationToken ct);
    Task<InspectionFlagDto> ClearFlagAsync(Guid flagId, ClearInspectionFlagRequest request, CancellationToken ct);
}

public sealed partial class AircraftService(
    IAircraftRepository aircraftRepo,
    IMaintenanceRepository maintenance,
    IAnomalyRepository anomalies,
    IPredictionRepository predictions,
    IActionAuditor auditor,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClock clock,
    IFleetHealthEvaluator healthEvaluator) : IAircraftService
{
    public async Task<PagedResult<AircraftSummaryDto>> ListAsync(AircraftFilter filter, PageRequest page, CancellationToken ct)
    {
        var result = await aircraftRepo.ListAsync(filter, page.Normalize(), ct);
        var ids = result.Items.Select(a => a.Id).ToList();
        var counters = await aircraftRepo.GetCountersAsync(ids, ct);
        var latest = await predictions.GetLatestByAircraftAsync(ct);
        // The "risk indicator" is the descriptive indicator (forecast condition); the model's score has its own column.
        var indicators = await predictions.GetLatestDescriptiveByAircraftAsync(ct);
        var health = await BuildHealthAsync(ids, latest, ct);
        var limitMeasurement = (await aircraftRepo.ListHealthThresholdsAsync(ct)).ToDictionary(t => t.Code, t => t.Measurement);
        var conditions = new Dictionary<Guid, string>();
        foreach (var id in ids)
        {
            var open = await anomalies.ListOpenBySourceAsync(id, AnomalySource.ForecastModel, ct);
            if (open.Count > 0)
                conditions[id] = AnomalyRules.ForecastCondition(open.SelectMany(o => (o.RuleCodes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                    .Select(c => limitMeasurement.GetValueOrDefault(c)).ToHashSet());
        }
        return result.Map(a => a.ToSummaryDto(
            AircraftCountersView.From(counters.GetValueOrDefault(a.Id)), latest.GetValueOrDefault(a.Id),
            health.GetValueOrDefault(a.Id)) with
        {
            ForecastCondition = conditions.GetValueOrDefault(a.Id),
            LatestRisk = (indicators.GetValueOrDefault(a.Id) ?? latest.GetValueOrDefault(a.Id)).ToLatestRisk(),
        });
    }

    /// <summary>The shared health evaluation (see <see cref="IFleetHealthEvaluator"/>).</summary>
    private Task<IReadOnlyDictionary<Guid, AircraftHealthDto>> BuildHealthAsync(
        IReadOnlyList<Guid> ids, IReadOnlyDictionary<Guid, AircraftRiskPrediction> latestPredictions, CancellationToken ct) =>
        healthEvaluator.EvaluateAsync(ids, latestPredictions, ct);

    public Task<IReadOnlyList<string>> ListFleetGroupsAsync(CancellationToken ct) => aircraftRepo.ListFleetGroupsAsync(ct);

    public async Task<AircraftDetailDto> GetAsync(Guid id, CancellationToken ct)
    {
        var aircraft = await aircraftRepo.GetByIdAsync(id, ct) ?? throw new NotFoundException("Aircraft", id);
        return await BuildDetailAsync(aircraft, ct);
    }

    public async Task<AircraftDetailDto> CreateAsync(UpsertAircraftRequest request, CancellationToken ct)
    {
        Validate(request);
        var registration = request.RegistrationNumber!.Trim().ToUpperInvariant();
        if (await aircraftRepo.GetByRegistrationAsync(registration, ct) is not null)
            throw new ConflictException($"Aircraft {registration} already exists.", "duplicate_registration");

        var now = clock.UtcNow;
        var aircraft = new Aircraft
        {
            RegistrationNumber = registration,
            Model = request.Model!.Trim(),
            Manufacturer = request.Manufacturer!.Trim(),
            FleetGroup = request.FleetGroup!.Trim(),
            SerialNumber = string.IsNullOrWhiteSpace(request.SerialNumber) ? null : request.SerialNumber.Trim(),
            // New aircraft start out of service until a technician records them as Available.
            OperationalStatus = OperationalStatus.UnderMaintenance,
            StatusReason = "Newly registered; awaiting entry-into-service check.",
            StatusChangedAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        await aircraftRepo.AddAsync(aircraft, ct);
        await auditor.RecordAsync("AircraftCreated", nameof(Aircraft), aircraft.Id.ToString(), new { registration }, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return await BuildDetailAsync(aircraft, ct);
    }

    public async Task<AircraftDetailDto> UpdateAsync(Guid id, UpsertAircraftRequest request, CancellationToken ct)
    {
        Validate(request);
        var aircraft = await aircraftRepo.GetByIdAsync(id, ct) ?? throw new NotFoundException("Aircraft", id);
        var registration = request.RegistrationNumber!.Trim().ToUpperInvariant();
        if (registration != aircraft.RegistrationNumber && await aircraftRepo.GetByRegistrationAsync(registration, ct) is not null)
            throw new ConflictException($"Aircraft {registration} already exists.", "duplicate_registration");

        var before = new { aircraft.RegistrationNumber, aircraft.Model, aircraft.Manufacturer, aircraft.FleetGroup, aircraft.SerialNumber };
        aircraft.RegistrationNumber = registration;
        aircraft.Model = request.Model!.Trim();
        aircraft.Manufacturer = request.Manufacturer!.Trim();
        aircraft.FleetGroup = request.FleetGroup!.Trim();
        aircraft.SerialNumber = string.IsNullOrWhiteSpace(request.SerialNumber) ? null : request.SerialNumber.Trim();
        aircraft.UpdatedAtUtc = clock.UtcNow;
        await auditor.RecordAsync("AircraftUpdated", nameof(Aircraft), aircraft.Id.ToString(), new { before, after = request }, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return await BuildDetailAsync(aircraft, ct);
    }

    public async Task<AircraftDetailDto> ChangeStatusAsync(Guid id, ChangeAircraftStatusRequest request, CancellationToken ct)
    {
        new Validator()
            .Must(request.Status.HasValue, "status", "Status is required.")
            .Required("reason", request.Reason, "Reason")
            .MaxLength("reason", request.Reason, 500, "Reason")
            .ThrowIfInvalid();

        var aircraft = await aircraftRepo.GetByIdAsync(id, ct) ?? throw new NotFoundException("Aircraft", id);
        var flags = await maintenance.ListFlagsAsync(id, activeOnly: true, ct);
        var openOrders = await maintenance.ListOpenWorkOrdersAsync(id, ct);
        try
        {
            AircraftStatusPolicy.EnsureCanChange(aircraft, request.Status!.Value, request.Reason!, flags, openOrders);
        }
        catch (DomainException ex)
        {
            throw new BusinessRuleException(ex.Message, ex.Code);
        }

        var previous = aircraft.OperationalStatus;
        aircraft.OperationalStatus = request.Status!.Value;
        aircraft.StatusReason = request.Reason!.Trim();
        aircraft.StatusChangedAtUtc = clock.UtcNow;
        aircraft.UpdatedAtUtc = clock.UtcNow;
        await auditor.RecordAsync("AircraftStatusChanged", nameof(Aircraft), aircraft.Id.ToString(),
            new { from = previous.ToString(), to = aircraft.OperationalStatus.ToString(), reason = aircraft.StatusReason }, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return await BuildDetailAsync(aircraft, ct);
    }

    public async Task<PagedResult<MaintenanceLogDto>> GetMaintenanceHistoryAsync(Guid id, PageRequest page, CancellationToken ct)
    {
        _ = await aircraftRepo.GetByIdAsync(id, ct) ?? throw new NotFoundException("Aircraft", id);
        return (await maintenance.ListLogsAsync(new MaintenanceLogFilter(id, null, null, null), page.Normalize(), ct)).Map(l => l.ToDto());
    }

    public async Task<IReadOnlyList<InspectionFlagDto>> ListFlagsAsync(Guid id, bool activeOnly, CancellationToken ct)
    {
        _ = await aircraftRepo.GetByIdAsync(id, ct) ?? throw new NotFoundException("Aircraft", id);
        return (await maintenance.ListFlagsAsync(id, activeOnly, ct)).Select(f => f.ToDto()).ToList();
    }

    public async Task<InspectionFlagDto> RaiseFlagAsync(Guid id, RaiseInspectionFlagRequest request, CancellationToken ct)
    {
        new Validator()
            .Must(request.Severity.HasValue, "severity", "Severity is required.")
            .Required("reason", request.Reason, "Reason")
            .MaxLength("reason", request.Reason, 1000, "Reason")
            .ThrowIfInvalid();

        var aircraft = await aircraftRepo.GetByIdAsync(id, ct) ?? throw new NotFoundException("Aircraft", id);
        if (request.AnomalyId is { } anomalyId && (await anomalies.GetAsync(anomalyId, ct))?.AircraftId != id)
            throw new ValidationException("anomalyId", "Anomaly does not belong to this aircraft.");
        if (request.PredictionId is { } predictionId && (await predictions.GetAsync(predictionId, ct))?.AircraftId != id)
            throw new ValidationException("predictionId", "Prediction does not belong to this aircraft.");
        if (request.WorkOrderId is { } woId && (await maintenance.GetWorkOrderAsync(woId, ct))?.AircraftId != id)
            throw new ValidationException("workOrderId", "Work order does not belong to this aircraft.");

        var now = clock.UtcNow;
        var flag = new InspectionFlag
        {
            AircraftId = id,
            Severity = request.Severity!.Value,
            Reason = request.Reason!.Trim(),
            AnomalyId = request.AnomalyId,
            PredictionId = request.PredictionId,
            WorkOrderId = request.WorkOrderId,
            RaisedByUserId = currentUser.RequireUserId(),
            RaisedAtUtc = now,
            IsActive = true,
        };
        await maintenance.AddFlagAsync(flag, ct);

        if (flag.Severity == InspectionFlagSeverity.Grounding && aircraft.OperationalStatus != OperationalStatus.Grounded)
        {
            aircraft.OperationalStatus = OperationalStatus.Grounded;
            aircraft.StatusReason = $"Grounding inspection flag: {flag.Reason}";
            aircraft.StatusChangedAtUtc = now;
            aircraft.UpdatedAtUtc = now;
        }
        await auditor.RecordAsync("InspectionFlagRaised", nameof(InspectionFlag), flag.Id.ToString(),
            new { aircraftId = id, severity = flag.Severity.ToString(), flag.Reason }, ct);
        await unitOfWork.SaveChangesAsync(ct);

        flag.Aircraft = aircraft;
        return (await maintenance.GetFlagAsync(flag.Id, ct))!.ToDto();
    }

    public async Task<InspectionFlagDto> ClearFlagAsync(Guid flagId, ClearInspectionFlagRequest request, CancellationToken ct)
    {
        new Validator().Required("clearanceNotes", request.ClearanceNotes, "Clearance notes").ThrowIfInvalid();
        var flag = await maintenance.GetFlagAsync(flagId, ct) ?? throw new NotFoundException("Inspection flag", flagId);
        if (!flag.IsActive) throw new BusinessRuleException("Inspection flag is already cleared.", "invalid_transition");

        flag.IsActive = false;
        flag.ClearedByUserId = currentUser.RequireUserId();
        flag.ClearedAtUtc = clock.UtcNow;
        flag.ClearanceNotes = request.ClearanceNotes!.Trim();
        // Clearing a flag never restores availability automatically; a technician must change status explicitly.
        await auditor.RecordAsync("InspectionFlagCleared", nameof(InspectionFlag), flag.Id.ToString(), new { flag.ClearanceNotes }, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return (await maintenance.GetFlagAsync(flag.Id, ct))!.ToDto();
    }

    private async Task<AircraftDetailDto> BuildDetailAsync(Aircraft a, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var counters = AircraftCountersView.From((await aircraftRepo.GetCountersAsync([a.Id], ct)).GetValueOrDefault(a.Id));
        var flags = await maintenance.ListFlagsAsync(a.Id, activeOnly: true, ct);
        var openOrders = await maintenance.ListOpenWorkOrdersAsync(a.Id, ct);
        var recentLogs = await maintenance.ListLogsAsync(new MaintenanceLogFilter(a.Id, null, null, null), new PageRequest(1, 10), ct);
        var recentAnomalies = await anomalies.ListAsync(new AnomalyFilter(a.Id, null, null, null, null), new PageRequest(1, 10), ct);
        var latestPredictions = await predictions.ListForAircraftAsync(a.Id, 5, ct);
        var latest = latestPredictions.FirstOrDefault();

        var advisory = DispatchAdvisoryPolicy.Evaluate(a, flags, openOrders, counters.OpenHighAnomalies, latest?.RiskCategory);
        var health = await BuildHealthAsync(
            [a.Id], latest is null ? new Dictionary<Guid, AircraftRiskPrediction>() : new() { [a.Id] = latest }, ct);

        return new AircraftDetailDto(
            a.Id, a.RegistrationNumber, a.Model, a.Manufacturer, a.FleetGroup, a.OperationalStatus, a.StatusReason,
            a.LastTelemetryAtUtc, a.LastInspectionAtUtc, counters.OpenAnomalies, counters.ActiveFlags, counters.OpenWorkOrders,
            (latestPredictions.FirstOrDefault(p => p.Basis == PredictionBasis.DescriptiveIndicator) ?? latest).ToLatestRisk(),
            a.SerialNumber, a.StatusChangedAtUtc, a.CreatedAtUtc,
            new DispatchAdvisoryDto(advisory.Readiness, advisory.Reasons, advisory.Disclaimer),
            flags.Select(f => f.ToDto()).ToList(),
            recentLogs.Items.Select(l => l.ToDto()).ToList(),
            recentAnomalies.Items.Select(x => x.ToDto()).ToList(),
            latestPredictions.Select(p => p.ToDto()).ToList(),
            openOrders.Select(w => w.ToDto(now, currentUser)).ToList(),
            health.GetValueOrDefault(a.Id),
            FleetStatusRules.Derive(a, counters.WorkOrdersInProgress, counters.ActiveFlags));
    }

    private static void Validate(UpsertAircraftRequest r)
    {
        var v = new Validator()
            .Required("registrationNumber", r.RegistrationNumber, "Registration number")
            .Required("model", r.Model, "Model")
            .Required("manufacturer", r.Manufacturer, "Manufacturer")
            .Required("fleetGroup", r.FleetGroup, "Fleet group")
            .MaxLength("model", r.Model, 64, "Model")
            .MaxLength("manufacturer", r.Manufacturer, 64, "Manufacturer")
            .MaxLength("fleetGroup", r.FleetGroup, 64, "Fleet group")
            .MaxLength("serialNumber", r.SerialNumber, 64, "Serial number");
        if (!string.IsNullOrWhiteSpace(r.RegistrationNumber) && !RegistrationRegex().IsMatch(r.RegistrationNumber.Trim()))
            v.Add("registrationNumber", "Registration must be 2–16 letters, digits or hyphens.");
        v.ThrowIfInvalid();
    }

    [GeneratedRegex("^[A-Za-z0-9-]{2,16}$")]
    private static partial Regex RegistrationRegex();
}
