using AFP.Application.Common;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Domain.Rules;

namespace AFP.Application.Abstractions;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct);

    /// <summary>Runs <paramref name="work"/> inside a database transaction, committing on success.</summary>
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct);
}

// ---- Filters ---------------------------------------------------------------------------------

public sealed record UserFilter(UserStatus? Status, UserRole? Role, string? Search);
public sealed record AircraftFilter(string? Search, OperationalStatus? Status, string? FleetGroup, string? Model);
public sealed record TelemetryFilter(Guid AircraftId, string? SensorId, DateTime? FromUtc, DateTime? ToUtc, QualityStatus? QualityStatus);
public sealed record AnomalyFilter(Guid? AircraftId, InvestigationStatus? Status, AnomalySeverity? Severity, DateTime? FromUtc, DateTime? ToUtc,
    AnomalySource? Source = null);
public sealed record PredictionFilter(Guid? AircraftId, string? RiskCategory, PredictionBasis? Basis, ApprovalStatus? ApprovalStatus);
public sealed record WorkOrderFilter(Guid? AircraftId, WorkOrderStatus? Status, WorkOrderPriority? Priority, long? AssignedTechnicianId, bool OpenOnly = false,
    WorkOrderSource? Source = null, long? AnomalyId = null);
public sealed record MaintenanceLogFilter(Guid? AircraftId, MaintenanceEventType? EventType, DateTime? FromUtc, DateTime? ToUtc);
public sealed record ActionAuditFilter(string? EntityType, string? EntityId, long? UserId, DateTime? FromUtc, DateTime? ToUtc);
public sealed record AuthAuditFilter(string? Email, string? EventType, DateTime? FromUtc, DateTime? ToUtc);

// ---- Projections -----------------------------------------------------------------------------

public interface ISensorForecastRepository
{
    /// <summary>The newest stored forecast (by data cutoff, then generation time) for an aircraft, model and horizon.</summary>
    Task<SensorForecast?> GetLatestAsync(Guid aircraftId, Guid modelVersionId, SensorForecastHorizon horizon, CancellationToken ct);
}

public sealed record AircraftCounters(Guid AircraftId, int OpenAnomalies, int OpenHighAnomalies, int ActiveFlags, int OpenWorkOrders,
    int WorkOrdersInProgress = 0);
public sealed record TelemetryPoint(DateTime RecordedAtUtc, double? TemperatureC, double? PressureKpa, double? VibrationIndex);
public sealed record TelemetryQualityTotals(int Total, int Valid, int Suspect, int MissingTemperature, int MissingPressure, DateTime? First, DateTime? Last);
public sealed record AircraftTelemetryQuality(Guid AircraftId, string RegistrationNumber, int Records, int Suspect, DateTime First, DateTime Last);
public sealed record TelemetryKey(Guid AircraftId, string? SensorId, DateTime RecordedAtUtc);

// ---- Repositories ----------------------------------------------------------------------------

public interface IUserRepository
{
    /// <summary>Loads the user with role assignments.</summary>
    Task<AppUser?> GetByIdAsync(long id, CancellationToken ct);
    Task<AppUser?> GetByUsernameAsync(string normalizedUsername, CancellationToken ct);
    Task<AppUser?> GetByEmailAsync(string normalizedEmail, CancellationToken ct);
    Task<bool> AnyActiveUserWithRoleAsync(UserRole role, CancellationToken ct);
    Task<PagedResult<AppUser>> ListAsync(UserFilter filter, PageRequest page, CancellationToken ct);
    Task<IReadOnlyList<AppUser>> ListActiveByRoleAsync(UserRole role, CancellationToken ct);
    Task<IReadOnlyDictionary<long, string>> GetDisplayNamesAsync(IEnumerable<long> ids, CancellationToken ct);
    Task AddAsync(AppUser user, CancellationToken ct);

    Task<IReadOnlyList<Role>> ListActiveRolesAsync(CancellationToken ct);

    Task AddResetTokenAsync(PasswordResetToken token, CancellationToken ct);
    Task<PasswordResetToken?> GetResetTokenByHashAsync(string tokenHash, CancellationToken ct);
    Task<IReadOnlyList<PasswordResetToken>> GetUsableResetTokensAsync(long userId, DateTime nowUtc, CancellationToken ct);

    Task AddRevokedTokenAsync(RevokedToken token, CancellationToken ct);
    Task<bool> IsTokenRevokedAsync(string jti, CancellationToken ct);

    Task AddRefreshTokenAsync(RefreshToken token, CancellationToken ct);
    Task<RefreshToken?> GetRefreshTokenByHashAsync(string tokenHash, CancellationToken ct);
    /// <summary>Tracked, not-yet-revoked tokens of a family (or of a user when <paramref name="familyId"/> is null).</summary>
    Task<IReadOnlyList<RefreshToken>> ListUnrevokedRefreshTokensAsync(long userId, Guid? familyId, CancellationToken ct);
}

public interface IAircraftRepository
{
    Task<Aircraft?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Aircraft?> GetByRegistrationAsync(string registrationNumber, CancellationToken ct);
    Task<IReadOnlyDictionary<string, Guid>> GetRegistrationMapAsync(CancellationToken ct);
    Task<PagedResult<Aircraft>> ListAsync(AircraftFilter filter, PageRequest page, CancellationToken ct);
    Task<IReadOnlyList<Aircraft>> ListAllAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> ListFleetGroupsAsync(CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, AircraftCounters>> GetCountersAsync(IReadOnlyCollection<Guid> aircraftIds, CancellationToken ct);
    Task<IReadOnlyDictionary<OperationalStatus, int>> CountByStatusAsync(CancellationToken ct);
    Task AddAsync(Aircraft aircraft, CancellationToken ct);
    Task UpdateLastTelemetryAsync(IReadOnlyDictionary<Guid, DateTime> latestByAircraft, CancellationToken ct);

    /// <summary>Active engine-health business thresholds (seeded reference data; see health_threshold.csv).</summary>
    Task<IReadOnlyList<HealthThreshold>> ListHealthThresholdsAsync(CancellationToken ct);
}

public interface ITelemetryRepository
{
    Task<PagedResult<TelemetryRecord>> QueryAsync(TelemetryFilter filter, PageRequest page, CancellationToken ct);
    Task<IReadOnlyList<TelemetryPoint>> GetPointsAsync(Guid aircraftId, string? sensorId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);
    Task<TelemetryRecord?> GetByIdAsync(long id, CancellationToken ct);
    Task<HashSet<TelemetryKey>> GetExistingKeysAsync(IReadOnlyCollection<Guid> aircraftIds, DateTime fromUtc, DateTime toUtc, CancellationToken ct);
    Task AddRangeAsync(IReadOnlyCollection<TelemetryRecord> records, CancellationToken ct);
    Task<TelemetryQualityTotals> GetQualityTotalsAsync(Guid? aircraftId, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct);
    Task<IReadOnlyList<AircraftTelemetryQuality>> GetQualityByAircraftAsync(Guid? aircraftId, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct);

    Task<TelemetryImport?> GetImportAsync(Guid id, CancellationToken ct);
    Task<TelemetryImport?> GetImportByHashAsync(string sha256, CancellationToken ct);
    Task<PagedResult<TelemetryImport>> ListImportsAsync(PageRequest page, CancellationToken ct);
    Task<IReadOnlyList<TelemetryImport>> ListImportsByStatusAsync(TelemetryImportStatus status, CancellationToken ct);
    Task AddImportAsync(TelemetryImport import, CancellationToken ct);
    Task AddRejectedRowsAsync(IReadOnlyCollection<TelemetryRejectedRow> rows, CancellationToken ct);
    Task<PagedResult<TelemetryRejectedRow>> ListRejectedRowsAsync(Guid importId, PageRequest page, CancellationToken ct);
    Task<(int Count, int Accepted, int Rejected, int Duplicates)> GetImportTotalsAsync(CancellationToken ct);

    /// <summary>
    /// Latest reading plus the extremes over each aircraft's own evaluation window - the <paramref name="window"/>
    /// ending at ITS latest reading - used to apply the recorded engine-health thresholds. Telemetry arrives in a
    /// daily batch, so a window ending at "now" would shrink through the day; how old the data is, is reported
    /// separately as staleness. Aircraft with no reading at all still get a row, with <c>ReadingsInWindow = 0</c>,
    /// so callers can tell "no data" apart from "within limits".
    /// </summary>
    Task<IReadOnlyDictionary<Guid, AircraftHealthSnapshot>> GetHealthSnapshotsAsync(
        IReadOnlyCollection<Guid> aircraftIds, TimeSpan window, CancellationToken ct);

    // --- Per-import ownership and deletion (see ImportDeletionService) ---
    Task<int> CountRecordsForImportAsync(Guid importId, CancellationToken ct);
    Task<int> CountRejectedRowsForImportAsync(Guid importId, CancellationToken ct);
    /// <summary>How many imports were produced by the same Spark micro-batch (&gt;1 means a shared lake partition).</summary>
    Task<int> CountImportsForLakeBatchAsync(long lakeBatchId, CancellationToken ct);
    Task<int> DeleteRecordsForImportAsync(Guid importId, CancellationToken ct);
    Task<int> DeleteRejectedRowsForImportAsync(Guid importId, CancellationToken ct);
    Task DeleteImportAsync(Guid importId, CancellationToken ct);

    /// <summary>Detaches tracked telemetry rows so that large imports keep memory bounded.</summary>
    void ClearTracking();
}

public interface IMaintenanceRepository
{
    Task<MaintenanceWorkOrder?> GetWorkOrderAsync(Guid id, CancellationToken ct);
    Task<PagedResult<MaintenanceWorkOrder>> ListWorkOrdersAsync(WorkOrderFilter filter, PageRequest page, CancellationToken ct);
    Task<IReadOnlyList<MaintenanceWorkOrder>> ListOpenWorkOrdersAsync(Guid? aircraftId, CancellationToken ct);
    /// <summary>Work orders per stored status for the given aircraft (the same rows the work-order list shows).</summary>
    Task<IReadOnlyDictionary<WorkOrderStatus, int>> CountWorkOrdersByStatusAsync(IReadOnlyCollection<Guid> aircraftIds, CancellationToken ct);
    /// <summary>
    /// Next free sequence for WO-{year}-NNNNNN: one past the highest number ever issued that year - on existing work
    /// orders or recorded in the creation audit - so a deleted ticket's number is never reused.
    /// </summary>
    Task<int> NextWorkOrderSequenceAsync(int year, CancellationToken ct);
    Task AddWorkOrderAsync(MaintenanceWorkOrder workOrder, CancellationToken ct);
    void SetOriginalConcurrencyStamp(MaintenanceWorkOrder workOrder, Guid stamp);

    Task<PagedResult<MaintenanceLog>> ListLogsAsync(MaintenanceLogFilter filter, PageRequest page, CancellationToken ct);
    /// <summary>Active approved maintenance action codes (seeded reference data), in display order.</summary>
    Task<IReadOnlyList<MaintenanceActionCode>> ListActionCodesAsync(CancellationToken ct);
    Task<IReadOnlyList<MaintenanceLog>> ListLogsSinceAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct);
    Task AddLogAsync(MaintenanceLog log, CancellationToken ct);

    Task<InspectionFlag?> GetFlagAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<InspectionFlag>> ListFlagsAsync(Guid? aircraftId, bool activeOnly, CancellationToken ct);
    Task AddFlagAsync(InspectionFlag flag, CancellationToken ct);
    Task<IReadOnlyList<MaintenanceWorkOrder>> ListClosedWorkOrdersAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct);
    Task<int> CountWorkOrdersCreatedBetweenAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct);
}

public interface IAnomalyRepository
{
    Task<AircraftAnomalyResult?> GetAsync(long id, CancellationToken ct);
    Task<PagedResult<AircraftAnomalyResult>> ListAsync(AnomalyFilter filter, PageRequest page, CancellationToken ct);
    Task<IReadOnlyList<AircraftAnomalyResult>> ListOpenHighPriorityAsync(int take, CancellationToken ct);
    Task<IReadOnlyList<(DateTime ObservedAtUtc, AnomalySeverity Severity, InvestigationStatus Status)>> GetSinceAsync(DateTime fromUtc, CancellationToken ct);
    Task<IReadOnlyDictionary<InvestigationStatus, int>> CountByInvestigationStatusAsync(CancellationToken ct);

    /// <summary>
    /// Anomalies at or above <paramref name="minimumSeverity"/> that have no work order yet and are still
    /// unresolved. Ordered oldest first so a capped run always makes forward progress.
    /// </summary>
    Task<IReadOnlyList<AircraftAnomalyResult>> ListQualifyingWithoutWorkOrderAsync(
        AnomalySeverity minimumSeverity, int take, CancellationToken ct, DateTime? observedSinceUtc = null);

    /// <summary>The newest still-open (New/Acknowledged/Investigating) anomaly of this source for the aircraft.</summary>
    Task<AircraftAnomalyResult?> GetOpenBySourceAsync(Guid aircraftId, AnomalySource source, CancellationToken ct);
    /// <summary>All open (New / Acknowledged / Investigating) anomalies of a source for an aircraft, earliest first.</summary>
    Task<IReadOnlyList<AircraftAnomalyResult>> ListOpenBySourceAsync(Guid aircraftId, AnomalySource source, CancellationToken ct);
    /// <summary>Anomalies handled under a work order (its <c>AnomalyId</c> or linked through <c>WorkOrderId</c>), earliest first.</summary>
    Task<IReadOnlyList<AircraftAnomalyResult>> ListByWorkOrderAsync(Guid workOrderId, CancellationToken ct);
    /// <summary>The not-closed, not-cancelled work order already handling forecast anomalies projected for that UTC day.</summary>
    Task<MaintenanceWorkOrder?> FindOpenForecastWorkOrderForDayAsync(Guid aircraftId, DateTime dayUtc, CancellationToken ct);

    /// <summary>True when an anomaly with this deduplication key already exists (processed before).</summary>
    Task<bool> DedupKeyExistsAsync(string dedupKey, CancellationToken ct);

    Task AddAsync(AircraftAnomalyResult anomaly, CancellationToken ct);

    /// <summary>
    /// Every anomaly with its registration and whether a work order already references it.
    /// Read-only; used to explain why each candidate was or was not selected.
    /// </summary>
    Task<IReadOnlyList<AnomalyEligibilityRow>> ListAnomalyEligibilityAsync(CancellationToken ct);
}

/// <summary>Read-only projection for the work-order dry run.</summary>
public sealed record AnomalyEligibilityRow(
    long AnomalyId, Guid AircraftId, string RegistrationNumber, AnomalySeverity Severity,
    bool IsAnomaly, InvestigationStatus InvestigationStatus, DateTime ObservedAtUtc, bool HasWorkOrder,
    AnomalySource Source = AnomalySource.IsolationForest);

public interface IPredictionRepository
{
    Task<AircraftRiskPrediction?> GetAsync(Guid id, CancellationToken ct);
    Task<PagedResult<AircraftRiskPrediction>> ListAsync(PredictionFilter filter, PageRequest page, CancellationToken ct);
    Task<IReadOnlyList<AircraftRiskPrediction>> ListForAircraftAsync(Guid aircraftId, int take, CancellationToken ct);

    /// <summary>The most recent prediction per aircraft (any basis).</summary>
    Task<IReadOnlyDictionary<Guid, AircraftRiskPrediction>> GetLatestByAircraftAsync(CancellationToken ct);
    /// <summary>Each aircraft's latest descriptive indicator (the fleet "risk indicator"; model rows excluded).</summary>
    Task<IReadOnlyDictionary<Guid, AircraftRiskPrediction>> GetLatestDescriptiveByAircraftAsync(CancellationToken ct);
    Task<IReadOnlyList<RiskStatus>> ListRiskStatusesAsync(CancellationToken ct);
    Task<IReadOnlyDictionary<MaintenanceOutcome, int>> CountByOutcomeAsync(CancellationToken ct);
}

public interface IModelVersionRepository
{
    Task<ModelVersion?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<ModelVersion>> ListAsync(ModelType? modelType, ModelStatus? status, CancellationToken ct);
    Task<ModelVersion?> GetActiveAsync(ModelType modelType, CancellationToken ct);
}

public interface IPipelineRunRepository
{
    Task<PipelineRun?> GetAsync(Guid id, CancellationToken ct);
    Task<PagedResult<PipelineRun>> ListAsync(PipelineJobType? jobType, PipelineRunStatus? status, PageRequest page, CancellationToken ct);
    Task<PipelineRun?> GetLatestAsync(PipelineJobType jobType, PipelineRunStatus? status, CancellationToken ct);
    Task<bool> HasPendingAsync(PipelineJobType jobType, CancellationToken ct);
    /// <summary>ETL runs whose <c>parameters-&gt;&gt;'importId'</c> is this import (its own execution history).</summary>
    Task<IReadOnlyList<Guid>> ListIdsForImportAsync(Guid importId, CancellationToken ct);
    Task<int> DeleteForImportAsync(Guid importId, CancellationToken ct);
    Task AddAsync(PipelineRun run, CancellationToken ct);
}

public interface IAuditRepository
{
    Task AddActionAsync(ActionAudit entry, CancellationToken ct);
    Task AddAuthAsync(AuthAudit entry, CancellationToken ct);
    Task<PagedResult<ActionAudit>> ListActionsAsync(ActionAuditFilter filter, PageRequest page, CancellationToken ct);
    Task<PagedResult<AuthAudit>> ListAuthAsync(AuthAuditFilter filter, PageRequest page, CancellationToken ct);
}

/// <summary>
/// The isolated development (synthetic) database. A separate connection, never the operational one; it holds no
/// users, so nothing written through it may reference <c>app_user</c>.
/// </summary>
public interface IDevelopmentDataSource
{
    bool IsConfigured { get; }
    string? DatabaseName { get; }
    Task<IReadOnlyList<ModelVersion>> ListModelsAsync(CancellationToken ct);
    Task<ModelVersion?> GetModelAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<PipelineRun>> ListRunsAsync(int limit, CancellationToken ct);
    Task<PipelineRun?> GetRunAsync(Guid id, CancellationToken ct);
    Task<bool> HasPendingAsync(PipelineJobType jobType, CancellationToken ct);
    Task AddRunAsync(PipelineRun run, CancellationToken ct);
    /// <summary>Latest model-basis prediction per aircraft (with aircraft and model version loaded).</summary>
    Task<IReadOnlyList<AircraftRiskPrediction>> ListLatestModelPredictionsAsync(CancellationToken ct);
}
