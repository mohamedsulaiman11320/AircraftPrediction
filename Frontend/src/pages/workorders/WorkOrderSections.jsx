import { Alert, JsonView, KeyValueList } from '../../components/Misc.jsx';
import { ForecastDiagnosis } from '../anomalies/ForecastDiagnosis.jsx';
import { DiagnosisText } from './DiagnosisText.jsx';
import { SeverityBadge, StatusBadge } from '../../components/StatusBadge.jsx';
import { formatDateTime, formatNumber, humanize } from '../../utils/format.js';

/**
 * Read-only sections of the work-order detail page: the stored deterministic diagnosis, the evidence
 * from the linked anomaly, the append-only technician report history and the dispatcher's review notes.
 * Every value comes from the API; nothing here is computed or inferred.
 */

const ISO_RE = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/;

/** Display names for AnomalyDto.source. Unknown values fall back to the humanized enum name. */
const ANOMALY_SOURCE_LABELS = {
  ThresholdRule: 'Threshold rule',
  IsolationForest: 'Isolation Forest',
  FailureModel: 'Validated model',
};

export function anomalySourceLabel(source) {
  return ANOMALY_SOURCE_LABELS[source] || (source ? humanize(source) : 'Anomaly model');
}

/** "A,B" or ["A","B"] -> ["A","B"]. */
export function splitRuleCodes(codes) {
  if (Array.isArray(codes)) return codes.filter(Boolean).map(String);
  if (!codes) return [];
  return String(codes)
    .split(',')
    .map((c) => c.trim())
    .filter(Boolean);
}

function Field({ label, children }) {
  return (
    <div>
      <dt>{label}</dt>
      <dd>{children ?? '—'}</dd>
    </div>
  );
}

function Text({ title, text }) {
  return (
    <div>
      <h4 style={{ fontSize: 'var(--fs-sm)', margin: '0 0 var(--space-1)' }}>{title}</h4>
      <p style={{ whiteSpace: 'pre-wrap', margin: 0 }}>{text || <span className="muted">Not recorded.</span>}</p>
    </div>
  );
}

function evidenceScalar(v) {
  if (v === null || v === undefined || v === '') return '—';
  if (typeof v === 'boolean') return v ? 'Yes' : 'No';
  if (typeof v === 'number') return Number.isInteger(v) ? String(v) : String(Number(v.toFixed(3)));
  if (typeof v === 'string' && ISO_RE.test(v)) return formatDateTime(v);
  return String(v);
}

/** Evidence JSON as nested, readable key/value lists (arrays of objects become numbered lists). */
export function EvidenceView({ value, depth = 0 }) {
  if (value === null || value === undefined) return <span className="muted">None recorded</span>;
  if (Array.isArray(value)) {
    if (value.length === 0) return <span className="muted">None</span>;
    if (value.every((x) => x === null || typeof x !== 'object')) return <span>{value.map(evidenceScalar).join(', ')}</span>;
    return (
      <ol className="stack" style={{ paddingLeft: '1.25rem', margin: 0 }}>
        {value.map((item, i) => (
          <li key={i}>
            <EvidenceView value={item} depth={depth + 1} />
          </li>
        ))}
      </ol>
    );
  }
  if (typeof value === 'object') {
    // Guard against pathological nesting; the raw JSON stays available below.
    if (depth > 4) return <span className="mono">{JSON.stringify(value)}</span>;
    return <KeyValueList data={value} format={(v) => (v !== null && typeof v === 'object' ? <EvidenceView value={v} depth={depth + 1} /> : evidenceScalar(v))} />;
  }
  return <span>{evidenceScalar(value)}</span>;
}

/** The stored diagnosis: deterministic rules, never presented as a confirmed fault. */
export function DiagnosisCardBody({ workOrder }) {
  const status = workOrder.diagnosisStatus;
  if (status === 'Failed') {
    return (
      <Alert tone="danger" title="The diagnosis could not be generated">
        <p style={{ margin: 0 }}>{workOrder.diagnosisError || 'No error detail was recorded.'}</p>
        <p style={{ margin: 0 }}>The work order itself is unaffected and can be worked as normal.</p>
      </Alert>
    );
  }
  if (status !== 'Generated' || !workOrder.diagnosisText) {
    return <p className="muted">No diagnosis has been generated for this work order.</p>;
  }
  return (
    <div className="stack">
      {/* Plain text from the API, structured into sections and rendered as text (never HTML). */}
      <DiagnosisText text={workOrder.diagnosisText} />
      <p className="m-0 text-[13px] text-slate-600">
        <strong>Deterministic rules — not a confirmed fault.</strong> The inspection establishes the cause.
      </p>
      <details className="text-[13px] text-slate-600" data-testid="diagnosis-technical">
        <summary className="cursor-pointer">Technical details</summary>
        <dl className="dl">
          <Field label="Method">
            <span className="mono" data-testid="diagnosis-method">
              {workOrder.diagnosisMethod}
            </span>
          </Field>
          <Field label="Rule version">{workOrder.diagnosisRuleVersion ? <span className="mono">{workOrder.diagnosisRuleVersion}</span> : null}</Field>
          <Field label="Generated">{workOrder.diagnosisGeneratedAtUtc ? formatDateTime(workOrder.diagnosisGeneratedAtUtc) : null}</Field>
        </dl>
      </details>
    </div>
  );
}

/** Evidence from the linked anomaly. The anomaly score is a ranking, never a probability. */
export function EvidenceSection({ anomaly }) {
  if (!anomaly) return <p className="muted">No anomaly is linked to this work order.</p>;
  const codes = splitRuleCodes(anomaly.ruleCodes);
  const hasScore = anomaly.anomalyScore !== null && anomaly.anomalyScore !== undefined;
  return (
    <div className="stack">
      <dl className="dl">
        <Field label="Source">
          <StatusBadge value={anomaly.source || 'Unknown'} label={anomalySourceLabel(anomaly.source)} prefix="Anomaly source" />
        </Field>
        <Field label="Rule codes">
          {codes.length ? (
            <span className="chips" data-testid="evidence-rule-codes">
              {codes.map((c) => (
                <code key={c} className="chip">
                  {c}
                </code>
              ))}
            </span>
          ) : null}
        </Field>
        <Field label="Severity">
          <SeverityBadge value={anomaly.severity} />
        </Field>
        <Field label="Observed">{formatDateTime(anomaly.observedAtUtc)}</Field>
        <Field label="Last observed">{anomaly.lastObservedAtUtc ? formatDateTime(anomaly.lastObservedAtUtc) : null}</Field>
        {anomaly.sensorId ? <Field label="Sensor">{anomaly.sensorId}</Field> : null}
        {hasScore ? (
          <Field label="Anomaly score (not a probability)">
            <span className="mono">{formatNumber(anomaly.anomalyScore, 4)}</span>
          </Field>
        ) : null}
        {anomaly.modelVersionId != null || (anomaly.modelName && anomaly.modelName !== 'Unknown') ? (
          <Field label="Model">
            {anomaly.modelName} <span className="mono">{anomaly.modelVersion}</span>
          </Field>
        ) : null}
        <Field label="Investigation">
          <StatusBadge value={anomaly.investigationStatus} />
        </Field>
      </dl>
      {anomaly.source === 'ForecastModel' && anomaly.evidence ? (
        <div data-testid="anomaly-evidence">
          <ForecastDiagnosis evidence={anomaly.evidence} />
        </div>
      ) : anomaly.evidence ? (
        <>
          <h3 style={{ fontSize: 'var(--fs-sm)', margin: 0 }}>Evidence recorded with the anomaly</h3>
          <div data-testid="anomaly-evidence">
            <EvidenceView value={anomaly.evidence} />
          </div>
          <details>
            <summary>Raw evidence (JSON)</summary>
            <JsonView value={anomaly.evidence} label="Raw anomaly evidence" />
          </details>
        </>
      ) : (
        <>
          <h3 style={{ fontSize: 'var(--fs-sm)', margin: 0 }}>Feature values at detection</h3>
          <KeyValueList data={anomaly.featureValues} />
        </>
      )}
    </div>
  );
}

/** Append-only report history, oldest first: Report 1, Report 2, ... */
export function TechnicianReports({ reports, actionCodes }) {
  if (!reports?.length) return <p className="muted">No technician report has been submitted yet.</p>;
  const describe = (code) => (actionCodes || []).find((c) => c.code === code)?.description;
  const ordered = [...reports].sort((a, b) => (a.sequence ?? 0) - (b.sequence ?? 0));
  return (
    <ol className="stack stack--lg" aria-label="Technician reports" style={{ listStyle: 'none', padding: 0, margin: 0 }}>
      {ordered.map((r, i) => {
        const description = describe(r.actionCode);
        return (
          <li key={r.sequence ?? i} className="report-entry" data-testid="technician-report">
            <h3 style={{ fontSize: 'var(--fs-md)', margin: 0 }}>Report {r.sequence ?? i + 1}</h3>
            <dl className="dl">
              <Field label="Technician">{r.technicianName || (r.technicianUserId != null ? `User ${r.technicianUserId}` : null)}</Field>
              <Field label="Submitted">{formatDateTime(r.submittedAtUtc)}</Field>
              <Field label="Action code">
                {r.actionCode ? (
                  <>
                    <span className="mono">{r.actionCode}</span>
                    {description ? ` — ${description}` : null}
                  </>
                ) : null}
              </Field>
              <Field label="Failure confirmed">{r.actualFailureConfirmed === null || r.actualFailureConfirmed === undefined ? null : r.actualFailureConfirmed ? 'Yes' : 'No'}</Field>
            </dl>
            <div className="stack">
              <Text title="Inspection findings" text={r.inspectionFindings} />
              <Text title="Work performed" text={r.correctiveAction} />
              <Text title="Notes" text={r.resolutionNotes} />
            </div>
          </li>
        );
      })}
    </ol>
  );
}
