import { useEffect, useRef, useState } from 'react';
import { telemetryApi } from '../../api/telemetry.js';
import { useAsync } from '../../hooks/useAsync.js';
import { usePolling } from '../../hooks/usePolling.js';
import { useAuth } from '../../auth/AuthContext.jsx';
import { ACCESS } from '../../router/access.js';
import { PageHeader, Card } from '../../components/PageHeader.jsx';
import { DataTable } from '../../components/DataTable.jsx';
import { Pagination } from '../../components/Pagination.jsx';
import { StatusBadge } from '../../components/StatusBadge.jsx';
import { AsyncBoundary, ErrorState } from '../../components/States.jsx';
import { ConfirmDialog } from '../../components/Modal.jsx';
import { SkeletonTable } from '../../components/Skeleton.jsx';
import { FormError } from '../../components/FormFields.jsx';
import { Alert } from '../../components/Misc.jsx';
import { useToast } from '../../components/Toast.jsx';
import { Icon } from '../../components/Icon.jsx';
import { formatBytes, formatDateTime, formatNumber } from '../../utils/format.js';
import { precheckRawJsonFile } from '../../utils/rawJson.js';
import { importColumns, importFileSize } from '../shared/columns.jsx';
import { DataTelemetryTabs } from '../shared/DataEngineerTabs.jsx';

const TERMINAL = ['Completed', 'Failed'];

/** Raw JSON uploads go through the Spark ETL, so they are NOT complete on upload. */
export const RAW_JSON_STAGES = [
  { status: 'Queued', label: 'Uploaded – waiting for ETL' },
  { status: 'Processing', label: 'Processing (Spark ETL)' },
  { status: 'Completed', label: 'Completed' },
];

export function importStageLabel(imp) {
  if (!imp) return '';
  if (imp.status === 'Failed') return 'Failed';
  if (imp.source === 'RawJson') return RAW_JSON_STAGES.find((s) => s.status === imp.status)?.label || imp.status;
  return imp.status;
}

/**
 * The Data Engineer UI uploads raw JSON only. The CSV import pipeline is still supported by the
 * backend (and by telemetryApi.upload / utils/csv.js), it simply has no entry point on this page.
 */
const RAW_JSON_UPLOAD = {
  label: 'Raw JSON telemetry (to ETL)',
  accept: '.json,application/json',
  precheck: precheckRawJsonFile,
  send: (f) => telemetryApi.uploadRawJson(f),
  source: 'RawJson',
  button: 'Upload to ETL',
  accepted: 'File uploaded (202). It is queued for the ETL and is not imported yet.',
};

function RejectedRows({ importId }) {
  const [page, setPage] = useState(1);
  const { data, error, reload } = useAsync(() => telemetryApi.rejectedRows(importId, { page, pageSize: 25 }), [importId, page]);
  if (error && !data) return <ErrorState error={error} onRetry={reload} title="Could not load rejected rows" />;
  if (!data) return <SkeletonTable rows={3} cols={3} />;
  return (
    <>
      <DataTable
        rowKey="rowNumber"
        columns={[
          { key: 'rowNumber', header: 'Row', numeric: true },
          { key: 'reasons', header: 'Reasons', render: (r) => (Array.isArray(r.reasons) ? r.reasons.join('; ') : r.reasons) },
          { key: 'rawLine', header: 'Raw line', render: (r) => <code style={{ wordBreak: 'break-all' }}>{r.rawLine}</code> },
        ]}
        rows={data.items}
        caption="Rejected rows"
        emptyTitle="No rejected rows"
      />
      {data.totalCount > 0 ? <Pagination page={data.page} pageSize={data.pageSize} totalCount={data.totalCount} totalPages={data.totalPages} onPageChange={setPage} /> : null}
    </>
  );
}

/** Stepper for the raw JSON -> ETL path. */
function RawJsonStages({ imp }) {
  const failed = imp.status === 'Failed';
  const idx = RAW_JSON_STAGES.findIndex((s) => s.status === imp.status);
  return (
    <ol className="stages" aria-label="ETL progress">
      {RAW_JSON_STAGES.map((s, i) => {
        const state = failed ? 'pending' : i < idx ? 'done' : i === idx ? 'current' : 'pending';
        return (
          <li key={s.status} className={`stages__item stages__item--${state}`} aria-current={state === 'current' ? 'step' : undefined}>
            {s.label}
          </li>
        );
      })}
      {failed ? (
        <li className="stages__item stages__item--failed" aria-current="step">
          Failed
        </li>
      ) : null}
    </ol>
  );
}

export function ImportSummary({ imp, showRejected, onRetry, retrying = false }) {
  if (!imp) return null;
  const running = !TERMINAL.includes(imp.status);
  const isRaw = imp.source === 'RawJson';
  return (
    <div className="stack">
      <div className="row row--between">
        <div className="row">
          <strong className="mono">{imp.fileName}</strong>
          {imp.source ? <StatusBadge value={imp.source} prefix="Source" /> : null}
          <StatusBadge value={imp.status} label={importStageLabel(imp)} />
          {running ? <span className="spinner" aria-label="Processing" style={{ color: 'var(--color-accent)' }} /> : null}
        </div>
        <span className="subtle">
          Requested {formatDateTime(imp.requestedAtUtc)}
          {imp.requestedBy ? ` by ${imp.requestedBy}` : ''}
          {imp.completedAtUtc ? ` · completed ${formatDateTime(imp.completedAtUtc)}` : ''}
        </span>
      </div>
      {isRaw ? <RawJsonStages imp={imp} /> : null}
      {/* Stored file facts are known as soon as the upload is accepted, whatever the ETL status is. */}
      <dl className="dl">
        <div>
          <dt>File size</dt>
          <dd>{importFileSize(imp.fileSizeBytes)}</dd>
        </div>
      </dl>
      {imp.status === 'Completed' || !isRaw ? (
        <dl className="dl">
          <div>
            <dt>Total rows</dt>
            <dd>{formatNumber(imp.totalRows)}</dd>
          </div>
          <div>
            <dt>Accepted</dt>
            <dd>{formatNumber(imp.acceptedRows)}</dd>
          </div>
          <div>
            <dt>Rejected</dt>
            <dd>{formatNumber(imp.rejectedRows)}</dd>
          </div>
          <div>
            <dt>Duplicates</dt>
            <dd>{formatNumber(imp.duplicateRows)}</dd>
          </div>
          <div>
            <dt>Warnings</dt>
            <dd>{formatNumber(imp.warningCount)}</dd>
          </div>
          <div>
            <dt>SHA-256</dt>
            <dd className="mono" style={{ fontSize: 11 }}>
              {imp.fileSha256 || '—'}
            </dd>
          </div>
        </dl>
      ) : imp.status !== 'Failed' ? (
        <p className="subtle">Row counts are available once the ETL has completed.</p>
      ) : null}
      {imp.status === 'Failed' ? (
        <Alert tone="danger" title="Import failed">
          {imp.errorMessage || 'No error message was recorded.'}
        </Alert>
      ) : null}
      {imp.status === 'Failed' && isRaw && onRetry ? (
        <div>
          <button type="button" className="btn btn--secondary" onClick={onRetry} disabled={retrying}>
            {retrying ? <span className="spinner" aria-hidden="true" /> : <Icon name="refresh" size={16} />}
            Retry
          </button>
        </div>
      ) : null}
      {imp.warnings?.length ? (
        <Alert tone="warning" title="Warnings">
          <ul>
            {imp.warnings.slice(0, 50).map((w, i) => (
              <li key={i}>{w}</li>
            ))}
          </ul>
        </Alert>
      ) : null}
      {showRejected && imp.status === 'Completed' && imp.rejectedRows > 0 ? (
        <div>
          <h3>Rejected rows</h3>
          <div className="card">
            <RejectedRows importId={imp.id} />
          </div>
        </div>
      ) : null}
    </div>
  );
}

/**
 * An import that keeps itself current: polls GET /telemetry/imports/{id} until Completed/Failed,
 * and offers Retry for a Failed raw JSON import (POST /telemetry/imports/{id}/retry, then resumes polling).
 */
export function TrackedImport({ initial, showRejected, onChange, announce = false }) {
  const toast = useToast();
  const { can } = useAuth();
  const [imp, setImp] = useState(initial);
  const [retrying, setRetrying] = useState(false);
  const [retryError, setRetryError] = useState(null);
  const announceRef = useRef(announce);

  useEffect(() => {
    setImp(initial);
    setRetryError(null);
    announceRef.current = announce;
  }, [initial?.id]); // eslint-disable-line react-hooks/exhaustive-deps

  usePolling(
    async () => {
      const next = await telemetryApi.getImport(imp.id);
      setImp(next);
      if (TERMINAL.includes(next.status)) {
        if (announceRef.current) {
          if (next.status === 'Completed')
            toast.success(`Import completed: ${formatNumber(next.acceptedRows)} accepted, ${formatNumber(next.rejectedRows)} rejected, ${formatNumber(next.duplicateRows)} duplicates.`);
          else toast.error(`Import failed: ${next.errorMessage || 'see details'}`);
        }
        onChange?.(next);
        return true;
      }
      return false;
    },
    { active: !!imp?.id && !TERMINAL.includes(imp.status), intervalMs: 2000 },
  );

  const retry = async () => {
    setRetrying(true);
    setRetryError(null);
    try {
      const updated = await telemetryApi.retryImport(imp.id);
      announceRef.current = true;
      setImp((prev) => ({ ...prev, ...updated, status: updated?.status || 'Queued' }));
      toast.info('Retry queued. The file is waiting for the ETL again.');
      onChange?.(updated);
    } catch (err) {
      setRetryError(err);
    } finally {
      setRetrying(false);
    }
  };

  return (
    <div className="stack" aria-live="polite">
      <ImportSummary imp={imp} showRejected={showRejected} onRetry={can(ACCESS.importRetry) ? retry : null} retrying={retrying} />
      <FormError error={retryError} title="Retry failed" />
    </div>
  );
}

/** ProblemDetails from an upload; `errors.file[]` (and any other field errors) are listed one per line. */
function UploadError({ error }) {
  if (!error) return null;
  const messages = error.errors ? Object.values(error.errors).flatMap((v) => (Array.isArray(v) ? v : [String(v)])) : [];
  if (!messages.length) return <FormError error={error} />;
  return (
    <Alert tone="danger" title={error.title || 'The file was rejected'}>
      {error.detail ? <p>{error.detail}</p> : null}
      <ul>
        {messages.map((m, i) => (
          <li key={i}>{m}</li>
        ))}
      </ul>
    </Alert>
  );
}

function UploadCard({ onImported }) {
  const toast = useToast();
  const inputRef = useRef(null);
  const [file, setFile] = useState(null);
  const [checkErrors, setCheckErrors] = useState([]);
  const [uploadError, setUploadError] = useState(null);
  const [busy, setBusy] = useState(false);
  const [drag, setDrag] = useState(false);
  const [current, setCurrent] = useState(null);
  const cfg = RAW_JSON_UPLOAD;

  const choose = async (f) => {
    setFile(f);
    setUploadError(null);
    setCheckErrors(f ? await cfg.precheck(f) : []);
  };

  const upload = async () => {
    const errs = await cfg.precheck(file);
    setCheckErrors(errs);
    if (errs.length) return;
    setBusy(true);
    setUploadError(null);
    try {
      const accepted = await cfg.send(file);
      toast.info(cfg.accepted);
      // 202 Accepted is NOT a finished ETL run: the status stays whatever the server reported
      // (Queued at best) and TrackedImport polls until the backend says Completed or Failed.
      setCurrent({
        id: accepted.id,
        status: accepted.status || 'Queued',
        source: cfg.source,
        fileName: file.name,
        fileSizeBytes: accepted.fileSizeBytes || file.size,
        storedPath: accepted.storedPath ?? null,
        requestedAtUtc: new Date().toISOString(),
      });
      setFile(null);
      if (inputRef.current) inputRef.current.value = '';
      onImported?.();
    } catch (err) {
      setUploadError(err?.status === 409 ? { title: 'Already uploaded', detail: err.detail || 'This exact file has already been uploaded.' } : err);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card title="Upload telemetry">
      <div
        className={`dropzone ${drag ? 'dropzone--active' : ''}`}
        onDragOver={(e) => {
          e.preventDefault();
          setDrag(true);
        }}
        onDragLeave={() => setDrag(false)}
        onDrop={(e) => {
          e.preventDefault();
          setDrag(false);
          const f = e.dataTransfer?.files?.[0];
          if (f) choose(f);
        }}
      >
        <Icon name="upload" size={28} />
        <p style={{ margin: '8px 0' }}>
          Drag a JSON file here or{' '}
          <label htmlFor="telemetry-file" className="link-btn" style={{ cursor: 'pointer' }}>
            browse
          </label>
        </p>
        <input
          id="telemetry-file"
          ref={inputRef}
          type="file"
          accept={cfg.accept}
          className="visually-hidden"
          aria-label="Raw JSON file"
          onChange={(e) => choose(e.target.files?.[0] || null)}
        />
        <p className="subtle" style={{ margin: 0 }}>
          Max 50 MB. A <span className="mono">.json</span> file whose root is an array of source records. It is stored and processed by the Spark ETL and is not imported until
          the ETL completes.
        </p>
      </div>
      {file ? (
        <p style={{ marginTop: 12 }}>
          Selected: <strong className="mono">{file.name}</strong> ({formatBytes(file.size)})
        </p>
      ) : null}
      {checkErrors.length ? (
        <Alert tone="danger" title="File cannot be uploaded">
          <ul>
            {checkErrors.map((e) => (
              <li key={e}>{e}</li>
            ))}
          </ul>
        </Alert>
      ) : null}
      <UploadError error={uploadError} />
      <div className="form-actions" style={{ marginTop: 12 }}>
        <button type="button" className="btn btn--primary" onClick={upload} disabled={!file || busy || checkErrors.length > 0}>
          {busy ? <span className="spinner" aria-hidden="true" /> : <Icon name="upload" size={16} />}
          {cfg.button}
        </button>
      </div>
      {current ? (
        <div className="section-gap">
          <h3>Current import</h3>
          <TrackedImport initial={current} showRejected announce onChange={() => onImported?.()} />
        </div>
      ) : null}
    </Card>
  );
}

const IN_PROGRESS = ['Queued', 'Processing'];

/**
 * Delete an import, after showing the real scope from GET /telemetry/imports/{id}/deletion-plan.
 *
 * Nothing optimistic is shown: the outcome is rendered only from the DELETE response. A response of
 * `completed: false` with a non-empty `remainingWork` is a PARTIAL FAILURE — the database rows went
 * but object storage still holds data — so the dialog stays open with a warning and a Retry instead
 * of any success wording.
 */
export function DeleteImportDialog({ imp, onClose, onDeleted }) {
  const toast = useToast();
  const plan = useAsync(() => telemetryApi.deletionPlan(imp.id), [imp.id]);
  const [partial, setPartial] = useState(null);

  const confirm = async () => {
    const result = await telemetryApi.deleteImport(imp.id);
    // The database half succeeded on both outcomes, so the list is refreshed either way.
    onDeleted?.(imp, result);
    if (result?.completed && !result?.remainingWork?.length) {
      toast.success(`Import removed: ${imp.fileName}`);
      onClose();
      return;
    }
    setPartial(result || { remainingWork: ['The server did not report what is left to remove.'] });
  };

  return (
    <ConfirmDialog
      open
      size="lg"
      title="Delete import"
      message={`Permanently delete the import ${imp.fileName}? This cannot be undone.`}
      confirmLabel={partial ? 'Retry delete' : 'Delete import'}
      tone="danger"
      confirmDisabled={!plan.data}
      onConfirm={confirm}
      onClose={onClose}
    >
      {partial ? (
        <Alert tone="warning" title="Deletion is INCOMPLETE" role="alert">
          <p>
            The database records for <strong className="mono">{imp.fileName}</strong> were removed, but object storage still holds data. This import is not fully
            deleted.
          </p>
          <ul>
            {(partial.remainingWork || []).map((w, i) => (
              <li key={i}>{w}</li>
            ))}
          </ul>
          <p>Deleting again is safe — the operation is resumable and removes only what is left.</p>
        </Alert>
      ) : null}
      <AsyncBoundary
        loading={plan.loading}
        error={plan.error}
        data={plan.data}
        onRetry={plan.reload}
        skeleton={
          <p className="muted" role="status">
            <span className="spinner" aria-hidden="true" /> Checking what would be removed…
          </p>
        }
      >
        {(p) => (
          <div className="stack">
            {IN_PROGRESS.includes(p.status) ? (
              <Alert tone="warning" title="Still being processed">
                This import has not finished the ETL. The server refuses to delete an import while it is {p.status.toLowerCase()}.
              </Alert>
            ) : null}
            <h3>What would be removed</h3>
            <dl className="dl">
              <div>
                <dt>Telemetry records</dt>
                <dd>{formatNumber(p.telemetryRecords)}</dd>
              </div>
              <div>
                <dt>Rejected rows</dt>
                <dd>{formatNumber(p.rejectedRows)}</dd>
              </div>
              <div>
                <dt>Pipeline runs</dt>
                <dd>{formatNumber(p.pipelineRuns)}</dd>
              </div>
              <div>
                <dt>Bronze objects</dt>
                <dd>{formatNumber(p.bronzeObjects)}</dd>
              </div>
              <div>
                <dt>Silver partitions</dt>
                <dd>{formatNumber(p.silverPartitionsRemoved)}</dd>
              </div>
              <div>
                <dt>Gold partitions</dt>
                <dd>{formatNumber(p.goldPartitionsRemoved)}</dd>
              </div>
            </dl>
            <h3>What is kept</h3>
            <ul aria-label="What is kept">
              {(p.preserved || []).map((item, i) => (
                <li key={i}>{item}</li>
              ))}
            </ul>
          </div>
        )}
      </AsyncBoundary>
    </ConfirmDialog>
  );
}

export function ImportsPage() {
  const { can } = useAuth();
  const [page, setPage] = useState(1);
  const [selected, setSelected] = useState(null);
  const [deleting, setDeleting] = useState(null);
  const history = useAsync(() => telemetryApi.imports({ page, pageSize: 25 }), [page]);
  const canUpload = can(ACCESS.rawJsonUpload);
  // Same capability that gates the rest of DE import management; the API enforces DataEngineer.
  const canDelete = can(ACCESS.telemetryImportsView);

  const afterDelete = (imp) => {
    history.reload();
    setSelected((s) => (s?.id === imp.id ? null : s));
  };

  return (
    <>
      <DataTelemetryTabs active="imports" />
      <PageHeader
        eyebrow="Data"
        title="Data Model"
        subtitle="Upload raw JSON telemetry for the ETL and review import outcomes. An accepted upload is queued for the ETL — it is only imported once the run reports Completed."
      />
      <div className="stack stack--lg">
        {canUpload ? <UploadCard onImported={history.reload} /> : null}
        <Card
          title="Import history"
          flush
          actions={
            <button type="button" className="btn btn--ghost btn--sm" onClick={history.reload}>
              <Icon name="refresh" size={14} /> Refresh
            </button>
          }
        >
          {history.error && !history.data ? (
            <ErrorState error={history.error} onRetry={history.reload} />
          ) : !history.data ? (
            <SkeletonTable />
          ) : (
            <>
              <DataTable
                columns={importColumns({ onSelect: setSelected, onDelete: canDelete ? setDeleting : undefined })}
                rows={history.data.items}
                caption="Import history"
                emptyTitle="No imports yet"
              />
              <Pagination page={history.data.page} pageSize={history.data.pageSize} totalCount={history.data.totalCount} totalPages={history.data.totalPages} onPageChange={setPage} />
            </>
          )}
        </Card>
        {selected ? (
          <Card
            title="Import details"
            actions={
              <button type="button" className="btn btn--ghost btn--sm" onClick={() => setSelected(null)}>
                Close
              </button>
            }
          >
            <TrackedImport initial={selected} showRejected={can(ACCESS.rejectedRows)} onChange={() => history.reload()} />
          </Card>
        ) : null}
      </div>
      {deleting ? <DeleteImportDialog imp={deleting} onClose={() => setDeleting(null)} onDeleted={afterDelete} /> : null}
    </>
  );
}
