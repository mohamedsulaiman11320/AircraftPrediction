using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Application.Contracts;
using AFP.Domain;
using AFP.Domain.Enums;
using Microsoft.Extensions.Options;

namespace AFP.Application.Services;

public interface IImportDeletionService
{
    /// <summary>What a deletion would remove and what it would preserve, without changing anything.</summary>
    Task<ImportDeletionPlanDto> PlanAsync(Guid importId, CancellationToken ct);

    /// <summary>Executes the plan. Idempotent and retryable; reports partial completion honestly.</summary>
    Task<ImportDeletionResultDto> DeleteAsync(Guid importId, CancellationToken ct);
}

/// <summary>
/// Deletes one telemetry import and only the resources that provably belong to it.
///
/// <para><b>Ownership model</b> (see the lineage trace in CLAUDE.md):</para>
/// <list type="bullet">
/// <item>Bronze object — one per import, from the persisted <c>stored_path</c>. Exclusive.</item>
/// <item><c>telemetry_record</c> / <c>telemetry_rejected_row</c> — joined by <c>import_id</c>. Exclusive.</item>
/// <item>ETL <c>pipeline_run</c> — matched by <c>parameters-&gt;&gt;'importId'</c>. Exclusive.</item>
/// <item>Silver/Gold partition — keyed by Spark <c>batch_id</c>. A batch can carry several imports, so the
/// partition is removed <b>only</b> when this import is the sole owner of that batch. A null
/// <c>lake_batch_id</c> means unknown, which is treated as shared and therefore preserved.</item>
/// <item>Aircraft, anomalies, predictions, maintenance, users, audit — never removed here.</item>
/// </list>
///
/// <para><b>Consistency</b>: PostgreSQL and MinIO cannot share a transaction, so there is no atomicity to
/// claim. The database half runs in one transaction; object removal is idempotent and runs afterwards.
/// A failure leaves the import marked <see cref="TelemetryImportStatus.Failed"/> with the remaining work
/// described, and re-running the delete resumes safely.</para>
/// </summary>
public sealed class ImportDeletionService(
    ITelemetryRepository telemetry,
    IPipelineRunRepository pipelineRuns,
    IRawTelemetryStore rawStore,
    IActionAuditor auditor,
    IUnitOfWork unitOfWork,
    IOptions<RawTelemetryStorageOptions> storageOptions) : IImportDeletionService
{
    private readonly RawTelemetryStorageOptions _storage = storageOptions.Value;

    public async Task<ImportDeletionPlanDto> PlanAsync(Guid importId, CancellationToken ct) =>
        (await BuildPlanAsync(importId, ct)).Plan;

    private async Task<(ImportDeletionPlanDto Plan, Domain.Entities.TelemetryImport Import)> BuildPlanAsync(
        Guid importId, CancellationToken ct)
    {
        var import = await telemetry.GetImportAsync(importId, ct)
                     ?? throw new NotFoundException("Import", importId);

        var telemetryRows = await telemetry.CountRecordsForImportAsync(importId, ct);
        var rejectedRows = await telemetry.CountRejectedRowsForImportAsync(importId, ct);
        var runIds = await pipelineRuns.ListIdsForImportAsync(importId, ct);

        // A lake partition is only ours when no other import shares the batch that produced it.
        var preserved = new List<string>();
        string? silverPrefix = null;
        string? goldPrefix = null;
        if (import.LakeBatchId is { } batchId)
        {
            var sharers = await telemetry.CountImportsForLakeBatchAsync(batchId, ct);
            if (sharers <= 1)
            {
                silverPrefix = $"{_storage.SilverPrefix}/batch_id={batchId}/";
                goldPrefix = $"{_storage.GoldPrefix}/batch_id={batchId}/";
            }
            else
            {
                preserved.Add($"Silver/Gold batch {batchId} is shared with {sharers - 1} other import(s) and is kept.");
            }
        }
        else
        {
            preserved.Add("Silver/Gold partition is not recorded for this import (processed before batch lineage " +
                          "existed), so ownership cannot be proven and the lake data is kept.");
        }

        preserved.Add("Aircraft, anomalies, predictions, maintenance records, users and audit history are never " +
                      "removed by an import deletion.");

        var plan = new ImportDeletionPlanDto(
            import.Id, import.FileName, import.Status,
            import.StoredPath is { Length: > 0 } ? 1 : 0,
            telemetryRows, rejectedRows, runIds.Count,
            silverPrefix is null ? 0 : 1, goldPrefix is null ? 0 : 1,
            preserved);

        return (plan, import);
    }

    public async Task<ImportDeletionResultDto> DeleteAsync(Guid importId, CancellationToken ct)
    {
        var (plan, import) = await BuildPlanAsync(importId, ct);

        if (import.Status is TelemetryImportStatus.Queued or TelemetryImportStatus.Processing)
            throw new BusinessRuleException(
                "This import is still being processed. Wait for it to finish before deleting it.",
                "import_in_progress");

        var storedPath = import.StoredPath;
        var batchId = import.LakeBatchId;
        var removesLake = plan.SilverPartitionsRemoved > 0;

        // 1) Object storage FIRST, deliberately.
        //
        // If the database row went first, a partial failure would leave the import row already gone, so a
        // retried DELETE would 404 instead of resuming the storage cleanup — the operation would claim to be
        // retryable while being unrecoverable. Doing storage first inverts the failure mode: the import row
        // survives, the caller sees completed=false, and retrying re-runs an idempotent object delete (a no-op
        // for anything already removed) before finally removing the row.
        var failures = new List<string>();
        var objectsRemoved = 0;
        if (!string.IsNullOrWhiteSpace(storedPath))
            objectsRemoved += await TryRemove(() => rawStore.DeleteAsync(storedPath, ct), storedPath, failures);

        if (removesLake && batchId is { } lakeBatch)
        {
            objectsRemoved += await TryRemovePrefix(
                _storage.SilverBucket, $"{_storage.SilverPrefix}/batch_id={lakeBatch}/", failures, ct);
            objectsRemoved += await TryRemovePrefix(
                _storage.GoldBucket, $"{_storage.GoldPrefix}/batch_id={lakeBatch}/", failures, ct);
        }

        if (failures.Count > 0)
            // Nothing in the database has changed, so the import is still here to retry against.
            return new ImportDeletionResultDto(
                importId, Completed: false, DatabaseRecordsRemoved: 0, ObjectsRemoved: objectsRemoved,
                Preserved: plan.Preserved, RemainingWork: failures);

        // 2) Database half, in one transaction and in foreign-key order.
        await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            await telemetry.DeleteRejectedRowsForImportAsync(importId, token);
            // import_id is ON DELETE SET NULL, so owned rows must go explicitly or they would be orphaned.
            await telemetry.DeleteRecordsForImportAsync(importId, token);
            await pipelineRuns.DeleteForImportAsync(importId, token);
            await telemetry.DeleteImportAsync(importId, token);
            await auditor.RecordAsync("TelemetryImportDeleted", "TelemetryImport", importId.ToString(),
                new
                {
                    plan.FileName,
                    plan.TelemetryRecords,
                    plan.RejectedRows,
                    plan.PipelineRuns,
                    lakeBatchId = batchId,
                    lakeRemoved = removesLake,
                }, token);
            await unitOfWork.SaveChangesAsync(token);
            return true;
        }, ct);

        return new ImportDeletionResultDto(
            importId,
            Completed: true,
            DatabaseRecordsRemoved: plan.TelemetryRecords + plan.RejectedRows + plan.PipelineRuns + 1,
            ObjectsRemoved: objectsRemoved,
            Preserved: plan.Preserved,
            RemainingWork: []);
    }

    private static async Task<int> TryRemove(Func<Task> action, string what, List<string> failures)
    {
        try
        {
            await action();
            return 1;
        }
        catch (Exception ex)
        {
            failures.Add($"{what}: {ex.Message}");
            return 0;
        }
    }

    private async Task<int> TryRemovePrefix(string bucket, string prefix, List<string> failures, CancellationToken ct)
    {
        try
        {
            return await rawStore.DeletePrefixAsync(bucket, prefix, ct);
        }
        catch (Exception ex)
        {
            failures.Add($"s3://{bucket}/{prefix}: {ex.Message}");
            return 0;
        }
    }
}
