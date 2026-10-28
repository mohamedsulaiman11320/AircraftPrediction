import { useId } from 'react';
import { DEMO_MODE } from '../api/config.js';

export function DemoBanner({ force = false }) {
  if (!DEMO_MODE && !force) return null;
  return (
    <div className="demo-banner" role="note" aria-label="Demo data notice">
      DEMO DATA — not from the live API. All registrations and values are fictional.
    </div>
  );
}

/** Pretty-printed JSON block. */
export function JsonView({ value, label = 'JSON' }) {
  if (value === null || value === undefined) return <p className="muted">None</p>;
  let text;
  try {
    text = JSON.stringify(value, null, 2);
  } catch {
    text = String(value);
  }
  return (
    <pre className="json-view" aria-label={label} tabIndex={0}>
      {text}
    </pre>
  );
}

/** Key/value object rendered as a definition list (for feature values, metrics, parameters). */
export function KeyValueList({ data, emptyText = 'None recorded', format }) {
  const entries = data && typeof data === 'object' ? Object.entries(data) : [];
  if (entries.length === 0) return <p className="muted">{emptyText}</p>;
  return (
    <dl className="dl">
      {entries.map(([k, v]) => (
        <div key={k}>
          <dt>{k}</dt>
          <dd className="mono">{format ? format(v, k) : renderValue(v)}</dd>
        </div>
      ))}
    </dl>
  );
}

function renderValue(v) {
  if (v === null || v === undefined) return '—';
  if (typeof v === 'number') return Number.isInteger(v) ? String(v) : v.toFixed(4);
  if (typeof v === 'object') return JSON.stringify(v);
  return String(v);
}

export function Tabs({ tabs, active, onChange, label }) {
  const base = useId();
  const onKeyDown = (e, idx) => {
    if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return;
    e.preventDefault();
    const next = (idx + (e.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length;
    onChange(tabs[next].id);
    document.getElementById(`${base}-${tabs[next].id}`)?.focus();
  };
  return (
    <div className="tabs" role="tablist" aria-label={label}>
      {tabs.map((t, i) => (
        <button
          key={t.id}
          id={`${base}-${t.id}`}
          type="button"
          role="tab"
          className="tab"
          aria-selected={active === t.id}
          tabIndex={active === t.id ? 0 : -1}
          onClick={() => onChange(t.id)}
          onKeyDown={(e) => onKeyDown(e, i)}
        >
          {t.label}
        </button>
      ))}
    </div>
  );
}

export function Alert({ tone = 'info', title, children, role }) {
  return (
    <div className={`alert alert--${tone}`} role={role || (tone === 'danger' ? 'alert' : 'note')}>
      <div>
        {title ? <div className="alert__title">{title}</div> : null}
        {children}
      </div>
    </div>
  );
}

export function Disclaimer({ children }) {
  if (!children) return null;
  return (
    <div className="disclaimer" role="note">
      <strong>Advisory: </strong>
      {children}
    </div>
  );
}

/** Failure-prediction readiness block (Blocked + reasons is a normal state, not an error). */
export function FailurePredictionStatus({ status }) {
  if (!status) return null;
  const tone = status.status === 'Blocked' ? 'warning' : status.status === 'Active' ? 'success' : 'info';
  return (
    <div className={`alert alert--${tone}`} role="note">
      <div>
        <div className="alert__title">
          Failure prediction: {status.status}
          {status.horizonDays ? ` · ${status.horizonDays}-day horizon` : ''}
        </div>
        {status.status !== 'Active' ? (
          <p>
            Model-based failure probabilities are not shown until a validated failure-prediction model is approved and active. Risk
            categories meanwhile are descriptive indicators only.
          </p>
        ) : null}
        {status.reasons?.length ? (
          <ul>
            {status.reasons.map((r) => (
              <li key={r}>{r}</li>
            ))}
          </ul>
        ) : null}
      </div>
    </div>
  );
}
