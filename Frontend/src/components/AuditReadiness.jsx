import { formatNumber, humanize } from '../utils/format.js';

/**
 * Failure-prediction readiness as written by the Python dataset-audit job. The ML job writes
 * snake_case (`failure_prediction_readiness`) and the API passes it through unchanged; camelCase
 * keys are accepted as a fallback.
 */
export function getAuditReadiness(resultSummary) {
  if (!resultSummary || typeof resultSummary !== 'object') return null;
  const r = resultSummary.failure_prediction_readiness ?? resultSummary.failurePredictionReadiness ?? null;
  if (!r || typeof r !== 'object') return null;
  return r;
}

/** Numeric readiness facts (horizon_days, positive_examples, ...) in display order. */
export function readinessNumbers(readiness) {
  if (!readiness) return [];
  return Object.entries(readiness)
    .filter(([k, v]) => k !== 'status' && k !== 'reasons' && typeof v === 'number' && Number.isFinite(v))
    .map(([k, v]) => ({ key: k, label: humanize(k), value: v }));
}

export function AuditReadinessPanel({ resultSummary, readiness: given }) {
  const readiness = given || getAuditReadiness(resultSummary);
  if (!readiness) return null;
  const nums = readinessNumbers(readiness);
  const reasons = Array.isArray(readiness.reasons) ? readiness.reasons : [];
  return (
    <div className={`alert alert--${readiness.status === 'Ready' ? 'success' : 'warning'}`} role="note">
      <div style={{ width: '100%' }}>
        <div className="alert__title">Failure prediction readiness: {readiness.status || 'Unknown'}</div>
        {reasons.length ? (
          <ul>
            {reasons.map((r) => (
              <li key={r}>{r}</li>
            ))}
          </ul>
        ) : null}
        {nums.length ? (
          <dl className="dl" style={{ marginTop: 8 }} aria-label="Readiness figures">
            {nums.map((n) => (
              <div key={n.key}>
                <dt>{n.label}</dt>
                <dd>{formatNumber(n.value, Number.isInteger(n.value) ? 0 : 2)}</dd>
              </div>
            ))}
          </dl>
        ) : null}
      </div>
    </div>
  );
}
