import { Link } from '../../router/Link.jsx';
import { AircraftLink } from './AircraftLink.jsx';
import { ActionMenu } from '../../components/ActionMenu.jsx';
import { DiagnosisStatusBadge, PriorityBadge, RiskBadge, SeverityBadge, StatusBadge, WorkOrderSourceBadge, WorkOrderStatusBadge } from '../../components/StatusBadge.jsx';
import { ProbabilityValue } from '../../components/Probability.jsx';
import { formatBytes, formatDate, formatDateTime, formatNumber, humanize } from '../../utils/format.js';

const PRIORITY_ORDER = ['Low', 'Medium', 'High', 'Critical'];
const SEVERITY_ORDER = ['Low', 'Medium', 'High'];
const RISK_ORDER = ['Undetermined', 'Low', 'Elevated', 'High'];

/** `showDiagnosis` adds the stored-diagnosis indicator (technician ticket lists). */
export function workOrderColumns({ showAssignee = true, showAircraft = true, linkAircraft = true, showSource = true, showDiagnosis = false } = {}) {
  return [
    {
      key: 'workOrderNumber',
      header: 'Work order',
      sortable: true,
      render: (w) => (
        <Link to={`/work-orders/${w.id}`} className="row-link nowrap">
          {w.workOrderNumber}
        </Link>
      ),
    },
    { key: 'title', header: 'Title', sortable: true },
    ...(showAircraft
      ? [
          {
            key: 'registrationNumber',
            header: 'Aircraft',
            sortable: true,
            render: (w) => (linkAircraft ? <Link to={`/fleet/${w.aircraftId}`}>{w.registrationNumber}</Link> : w.registrationNumber),
          },
        ]
      : []),
    { key: 'priority', header: 'Priority', sortable: true, sortValue: (w) => PRIORITY_ORDER.indexOf(w.priority), render: (w) => <PriorityBadge value={w.priority} /> },
    { key: 'status', header: 'Status', sortable: true, render: (w) => <WorkOrderStatusBadge value={w.status} /> },
    ...(showSource ? [{ key: 'source', header: 'Source', sortable: true, render: (w) => <WorkOrderSourceBadge value={w.source} /> }] : []),
    ...(showDiagnosis ? [{ key: 'diagnosisStatus', header: 'Diagnosis', sortable: true, render: (w) => <DiagnosisStatusBadge value={w.diagnosisStatus} /> }] : []),
    ...(showAssignee
      ? [{ key: 'assignedTechnicianName', header: 'Assigned to', sortable: true, render: (w) => w.assignedTechnicianName || <span className="muted">Unassigned</span> }]
      : []),
    {
      key: 'dueAtUtc',
      header: 'Due',
      sortable: true,
      render: (w) => (
        <span className={w.isOverdue ? 'text-danger nowrap' : 'nowrap'}>
          {formatDate(w.dueAtUtc)}
          {w.isOverdue ? ' · Overdue' : ''}
        </span>
      ),
    },
  ];
}

export function anomalyColumns({ showAircraft = true } = {}) {
  return [
    {
      key: 'observedAtUtc',
      header: 'Observed',
      sortable: true,
      render: (a) => (
        <Link to={`/anomalies/${a.id}`} className="row-link nowrap">
          {formatDateTime(a.observedAtUtc)}
        </Link>
      ),
    },
    ...(showAircraft
      ? [{ key: 'registrationNumber', header: 'Aircraft', sortable: true, render: (a) => <AircraftLink id={a.aircraftId}>{a.registrationNumber}</AircraftLink> }]
      : []),
    { key: 'sensorId', header: 'Sensor', render: (a) => a.sensorId || '—' },
    { key: 'severity', header: 'Severity', sortable: true, sortValue: (a) => SEVERITY_ORDER.indexOf(a.severity), render: (a) => <SeverityBadge value={a.severity} /> },
    { key: 'anomalyScore', header: 'Anomaly score', numeric: true, sortable: true, render: (a) => formatNumber(a.anomalyScore, 3) },
    { key: 'investigationStatus', header: 'Investigation', sortable: true, render: (a) => <StatusBadge value={a.investigationStatus} /> },
    { key: 'modelVersion', header: 'Model', render: (a) => <span className="subtle">{a.modelName} {a.modelVersion}</span> },
  ];
}

/**
 * Prediction period = telemetry cut-off (featureCutoffUtc) .. cut-off + horizonDays. It is a rolling
 * window, not a calendar month. Nothing is derived when the server did not supply the cut-off.
 */
export function predictionPeriod(p) {
  const days = Number(p?.horizonDays);
  const label = Number.isFinite(days) && days > 0 ? `Rolling ${days}-day window` : null;
  const fromMs = p?.featureCutoffUtc ? Date.parse(p.featureCutoffUtc) : NaN;
  if (!label || Number.isNaN(fromMs)) return { label, fromUtc: null, toUtc: null };
  return { label, fromUtc: new Date(fromMs).toISOString(), toUtc: new Date(fromMs + days * 86400000).toISOString() };
}

export function PredictionPeriod({ prediction }) {
  const { label, fromUtc, toUtc } = predictionPeriod(prediction);
  if (!label) return <span className="muted">—</span>;
  return (
    <span className="nowrap" data-testid="prediction-period">
      {fromUtc ? (
        <>
          {formatDateTime(fromUtc)} → {formatDateTime(toUtc)}
          <br />
        </>
      ) : null}
      <span className="subtle">
        {label}
        {fromUtc ? '' : ' · telemetry cut-off not recorded'}
      </span>
    </span>
  );
}

export function predictionColumns({ showAircraft = true } = {}) {
  return [
    { key: 'predictionTimestampUtc', header: 'Generated', sortable: true, render: (p) => <span className="nowrap">{formatDateTime(p.predictionTimestampUtc)}</span> },
    ...(showAircraft
      ? [{ key: 'registrationNumber', header: 'Aircraft', sortable: true, render: (p) => <AircraftLink id={p.aircraftId}>{p.registrationNumber}</AircraftLink> }]
      : []),
    { key: 'riskCategory', header: 'Risk category', sortable: true, sortValue: (p) => RISK_ORDER.indexOf(p.riskCategory), render: (p) => <RiskBadge value={p.riskCategory} /> },
    { key: 'basis', header: 'Basis', sortable: true, render: (p) => <StatusBadge value={p.basis} /> },
    { key: 'probability', header: 'Probability', render: (p) => <ProbabilityValue prediction={p} /> },
    { key: 'predictionState', header: 'State', sortable: true, render: (p) => <StatusBadge value={p.predictionState} /> },
    { key: 'period', header: 'Prediction period', sortValue: (p) => p.featureCutoffUtc || '', sortable: true, render: (p) => <PredictionPeriod prediction={p} /> },
    { key: 'model', header: 'Model version', render: (p) => (p.modelName ? `${p.modelName} ${p.modelVersion || ''}` : <span className="muted">None (rules)</span>) },
    { key: 'approvalStatus', header: 'Review', sortable: true, render: (p) => <StatusBadge value={p.approvalStatus} /> },
    { key: 'actualOutcome', header: 'Actual outcome', sortable: true, render: (p) => <StatusBadge value={p.actualOutcome} /> },
  ];
}

export function maintenanceColumns() {
  return [
    { key: 'occurredAtUtc', header: 'Date', sortable: true, render: (m) => <span className="nowrap">{formatDate(m.occurredAtUtc)}</span> },
    { key: 'eventType', header: 'Type', sortable: true, render: (m) => <StatusBadge value={m.eventType} kind="event" label={humanize(m.eventType)} /> },
    { key: 'description', header: 'Description' },
    {
      key: 'actualFailureConfirmed',
      header: 'Failure confirmed',
      render: (m) => (m.actualFailureConfirmed === null || m.actualFailureConfirmed === undefined ? '—' : m.actualFailureConfirmed ? 'Yes' : 'No'),
    },
    { key: 'isVerified', header: 'Verified', render: (m) => (m.isVerified ? 'Yes' : 'No') },
    { key: 'source', header: 'Source', render: (m) => (m.workOrderId ? <Link to={`/work-orders/${m.workOrderId}`}>{m.source}</Link> : m.source) },
  ];
}

const RAW_JSON_STAGE_LABELS = { Queued: 'Uploaded – waiting for ETL', Processing: 'Processing (Spark ETL)' };
function rawJsonStage(status) {
  return RAW_JSON_STAGE_LABELS[status];
}

/**
 * `fileSizeBytes` is 0 when the backend did not record a size, so it reads as "not known" rather
 * than as a 0-byte file. The value is never inferred. (The MinIO Bronze object key `storedPath` is
 * still returned by the API but is internal storage detail and is no longer shown in the UI.)
 */
export function importFileSize(bytes) {
  return bytes ? formatBytes(bytes) : '—';
}

/**
 * `onDelete` adds the per-row three-dot actions menu. It is passed only when the signed-in user
 * holds the capability that gates import management; hiding the button is convenience, the server
 * enforces the DataEngineer policy on the endpoint itself.
 */
export function importColumns({ onSelect, onDelete } = {}) {
  return [
    {
      key: 'fileName',
      header: 'File',
      sortable: true,
      render: (i) =>
        onSelect ? (
          <button type="button" className="link-btn" onClick={() => onSelect(i)}>
            {i.fileName}
          </button>
        ) : (
          <span className="mono">{i.fileName}</span>
        ),
    },
    { key: 'source', header: 'Source', sortable: true, render: (i) => <StatusBadge value={i.source} /> },
    { key: 'status', header: 'Status', sortable: true, render: (i) => <StatusBadge value={i.status} label={i.source === 'RawJson' ? rawJsonStage(i.status) : undefined} /> },
    { key: 'fileSizeBytes', header: 'Size', numeric: true, sortable: true, render: (i) => importFileSize(i.fileSizeBytes) },
    { key: 'totalRows', header: 'Total', numeric: true, sortable: true, render: (i) => formatNumber(i.totalRows) },
    { key: 'acceptedRows', header: 'Accepted', numeric: true, sortable: true, render: (i) => formatNumber(i.acceptedRows) },
    { key: 'rejectedRows', header: 'Rejected', numeric: true, sortable: true, render: (i) => formatNumber(i.rejectedRows) },
    { key: 'duplicateRows', header: 'Duplicates', numeric: true, sortable: true, render: (i) => formatNumber(i.duplicateRows) },
    { key: 'requestedAtUtc', header: 'Upload time', sortable: true, render: (i) => <span className="nowrap">{formatDateTime(i.requestedAtUtc)}</span> },
    { key: 'requestedBy', header: 'Uploaded by', render: (i) => i.requestedBy || '—' },
    { key: 'errorMessage', header: 'Error', render: (i) => (i.errorMessage ? <span className="text-danger">{i.errorMessage}</span> : '—') },
    ...(onDelete
      ? [
          {
            key: 'actions',
            header: 'Actions',
            className: 'actions-cell',
            render: (i) => (
              <ActionMenu label={`Actions for ${i.fileName}`} items={[{ key: 'delete', label: 'Delete import', tone: 'danger', onSelect: () => onDelete(i) }]} />
            ),
          },
        ]
      : []),
  ];
}

export function runColumns({ onSelect, showRequestedBy = true } = {}) {
  return [
    {
      key: 'jobType',
      header: 'Job',
      sortable: true,
      render: (r) =>
        onSelect ? (
          <button type="button" className="link-btn" onClick={() => onSelect(r)}>
            {humanize(r.jobType)}
          </button>
        ) : (
          humanize(r.jobType)
        ),
    },
    { key: 'status', header: 'Status', sortable: true, render: (r) => <StatusBadge value={r.status} /> },
    { key: 'trigger', header: 'Trigger' },
    { key: 'requestedAtUtc', header: 'Requested', sortable: true, render: (r) => <span className="nowrap">{formatDateTime(r.requestedAtUtc)}</span> },
    { key: 'completedAtUtc', header: 'Completed', sortable: true, render: (r) => <span className="nowrap">{formatDateTime(r.completedAtUtc)}</span> },
    { key: 'recordsProcessed', header: 'Records', numeric: true, render: (r) => formatNumber(r.recordsProcessed) },
    ...(showRequestedBy ? [{ key: 'requestedBy', header: 'By', render: (r) => r.requestedBy || '—' }] : []),
  ];
}

export function modelColumns() {
  return [
    {
      key: 'modelName',
      header: 'Model',
      sortable: true,
      render: (m) => (
        <Link to={`/models/${m.id}`} className="row-link">
          {m.modelName} <span className="mono">{m.version}</span>
        </Link>
      ),
    },
    { key: 'modelType', header: 'Type', sortable: true, render: (m) => humanize(m.modelType) },
    { key: 'algorithm', header: 'Algorithm', sortable: true },
    { key: 'status', header: 'Status', sortable: true, render: (m) => <StatusBadge value={m.status} /> },
    { key: 'trainingRowCount', header: 'Training rows', numeric: true, sortable: true, render: (m) => formatNumber(m.trainingRowCount) },
    { key: 'createdAtUtc', header: 'Created', sortable: true, render: (m) => <span className="nowrap">{formatDate(m.createdAtUtc)}</span> },
  ];
}
