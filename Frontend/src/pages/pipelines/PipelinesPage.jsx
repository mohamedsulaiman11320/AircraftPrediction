import { useState } from 'react';
import { pipelinesApi } from '../../api/pipelines.js';
import { useAsync } from '../../hooks/useAsync.js';
import { useListQuery } from '../../hooks/useListQuery.js';
import { PageHeader } from '../../components/PageHeader.jsx';
import { SearchFilterBar } from '../../components/SearchFilterBar.jsx';
import { DataTable } from '../../components/DataTable.jsx';
import { Pagination } from '../../components/Pagination.jsx';
import { StatusBadge } from '../../components/StatusBadge.jsx';
import { ErrorState } from '../../components/States.jsx';
import { SkeletonTable } from '../../components/Skeleton.jsx';
import { Alert, JsonView } from '../../components/Misc.jsx';
import { Modal } from '../../components/Modal.jsx';
import { AuditReadinessPanel } from '../../components/AuditReadiness.jsx';
import { runColumns } from '../shared/columns.jsx';
import { JOB_TYPES, RUN_STATUS, options } from '../../utils/enums.js';
import { formatDateTime, formatNumber, humanize } from '../../utils/format.js';

/**
 * Per-stage run metadata the backend does not record. PipelineRunDto (docs/API.md) carries only
 * id, jobType, status, trigger, requestedBy, requestedAtUtc, startedAtUtc, completedAtUtc,
 * parameters, resultSummary, errorMessage, modelVersionId and recordsProcessed — there is no stage,
 * retry or per-row counter column — so anything below that a run's own resultSummary does not
 * report is shown as "Unknown" rather than as 0 or a guess.
 */
export const UNKNOWN = 'Unknown';
export const RUN_METADATA_NOTE =
  'Per-stage run metadata (inserted, updated, rejected, already-stored and retry counts, failure stage and the associated import name) is not recorded for pipeline runs. Columns read "Unknown" unless the run itself reported the value in its result summary — no value is inferred.';

/** Reads a counter from the run's own result summary (snake_case as written by the ETL, or camelCase). */
export function runSummaryValue(run, ...keys) {
  const summary = run?.resultSummary;
  if (!summary || typeof summary !== 'object') return UNKNOWN;
  for (const key of keys) {
    const value = summary[key];
    if (value === null || value === undefined) continue;
    return typeof value === 'number' ? formatNumber(value) : String(value);
  }
  return UNKNOWN;
}

const unknownCell = () => <span className="muted">{UNKNOWN}</span>;

function metadataCell(run, ...keys) {
  const value = runSummaryValue(run, ...keys);
  return value === UNKNOWN ? unknownCell() : <span className="nowrap">{value}</span>;
}

export function RunDetail({ run }) {
  return (
    <div className="stack">
      <dl className="dl">
        <div>
          <dt>Job</dt>
          <dd>{humanize(run.jobType)}</dd>
        </div>
        <div>
          <dt>Status</dt>
          <dd>
            <StatusBadge value={run.status} />
          </dd>
        </div>
        <div>
          <dt>Trigger</dt>
          <dd>{run.trigger}</dd>
        </div>
        <div>
          <dt>Requested by</dt>
          <dd>{run.requestedBy || '—'}</dd>
        </div>
        <div>
          <dt>Requested</dt>
          <dd>{formatDateTime(run.requestedAtUtc)}</dd>
        </div>
        <div>
          <dt>Started</dt>
          <dd>{formatDateTime(run.startedAtUtc)}</dd>
        </div>
        <div>
          <dt>Completed</dt>
          <dd>{formatDateTime(run.completedAtUtc)}</dd>
        </div>
        <div>
          <dt>Records processed</dt>
          <dd>{formatNumber(run.recordsProcessed)}</dd>
        </div>
        <div>
          <dt>Run ID</dt>
          <dd className="mono" style={{ fontSize: 11 }}>
            {run.id}
          </dd>
        </div>
      </dl>
      {run.errorMessage ? (
        <Alert tone={run.status === 'Blocked' ? 'warning' : 'danger'} title={run.status === 'Blocked' ? 'Blocked' : 'Error'}>
          <p style={{ whiteSpace: 'pre-wrap' }}>{run.errorMessage}</p>
        </Alert>
      ) : null}
      <AuditReadinessPanel resultSummary={run.resultSummary} />
      <div>
        <h3 style={{ fontSize: 'var(--fs-sm)' }}>Parameters</h3>
        <JsonView value={run.parameters && Object.keys(run.parameters).length ? run.parameters : null} label="Run parameters" />
      </div>
      <div>
        <h3 style={{ fontSize: 'var(--fs-sm)' }}>Result summary</h3>
        <JsonView value={run.resultSummary} label="Run result summary" />
      </div>
    </div>
  );
}

/** Run history is the whole page: the ETL and ML runs the backend has recorded, filtered and paged. */
export function PipelinesPage() {
  const { values, page, pageSize, setFilters, setPage, setPageSize, reset } = useListQuery();
  const [selected, setSelected] = useState(null);

  const runs = useAsync(
    () => pipelinesApi.runs({ jobType: values.jobType || undefined, status: values.status || undefined, page, pageSize }),
    [values.jobType, values.status, page, pageSize],
  );

  // The shared run columns carry job, status, trigger, requested, completed and records; the
  // requester ("By") is not shown here. "Started" slots in beside the other timestamps.
  const columns = [...runColumns({ onSelect: setSelected, showRequestedBy: false })];
  columns.splice(Math.max(0, columns.findIndex((c) => c.key === 'completedAtUtc')), 0, {
    key: 'startedAtUtc',
    header: 'Started',
    sortable: true,
    render: (r) => <span className="nowrap">{formatDateTime(r.startedAtUtc)}</span>,
  });
  columns.push(
    { key: 'insertedRows', header: 'Inserted', numeric: true, render: (r) => metadataCell(r, 'accepted_rows', 'acceptedRows', 'inserted_rows', 'insertedRows') },
    { key: 'updatedRows', header: 'Updated', numeric: true, render: (r) => metadataCell(r, 'updated_rows', 'updatedRows') },
    { key: 'rejectedRows', header: 'Rejected', numeric: true, render: (r) => metadataCell(r, 'rejected_rows', 'rejectedRows') },
    { key: 'alreadyStoredRows', header: 'Already stored', numeric: true, render: (r) => metadataCell(r, 'duplicate_rows', 'duplicateRows', 'already_stored_rows') },
    { key: 'retryCount', header: 'Retries', numeric: true, render: unknownCell },
    { key: 'failureStage', header: 'Failure stage', render: unknownCell },
    { key: 'importName', header: 'Import', render: unknownCell },
    { key: 'errorMessage', header: 'Failure reason', render: (r) => (r.errorMessage ? <span className="text-danger">{r.errorMessage}</span> : '—') },
    { key: 'id', header: 'Run ID', render: (r) => <span className="mono" style={{ fontSize: 11 }}>{r.id}</span> },
  );

  return (
    <>
      <PageHeader eyebrow="Data" title="Pipeline" subtitle="ETL and processing run history." />
      <div className="card">
        <SearchFilterBar
          values={values}
          onChange={setFilters}
          onReset={reset}
          filters={[
            { name: 'jobType', label: 'Job type', type: 'select', options: options(JOB_TYPES) },
            { name: 'status', label: 'Status', type: 'select', options: options(RUN_STATUS) },
          ]}
        />
        <p className="help-text" style={{ padding: '12px 16px 0', margin: 0 }}>
          {RUN_METADATA_NOTE}
        </p>
        {runs.error && !runs.data ? (
          <ErrorState error={runs.error} onRetry={runs.reload} />
        ) : !runs.data ? (
          <SkeletonTable />
        ) : (
          <div aria-busy={runs.loading}>
            <DataTable columns={columns} rows={runs.data.items} caption="Pipeline runs" emptyTitle="No pipeline runs" />
            <Pagination page={runs.data.page} pageSize={runs.data.pageSize} totalCount={runs.data.totalCount} totalPages={runs.data.totalPages} onPageChange={setPage} onPageSizeChange={setPageSize} />
          </div>
        )}
      </div>

      <Modal open={!!selected} title={selected ? `${humanize(selected.jobType)} run` : ''} onClose={() => setSelected(null)} size="lg">
        {selected ? <RunDetailLoader run={selected} /> : null}
      </Modal>
    </>
  );
}

/** Loads the full run (list rows may omit parameters/summary in future) and renders it. */
function RunDetailLoader({ run }) {
  const { data, error, reload } = useAsync(() => pipelinesApi.getRun(run.id), [run.id, run.status]);
  if (error && !data) return <ErrorState error={error} onRetry={reload} />;
  return <RunDetail run={data || run} />;
}
