import { Sparkline } from './charts/Sparkline.jsx';

export function KpiCard({ label, value, hint, tone = 'accent', spark }) {
  return (
    <div className={`card kpi kpi--${tone}`}>
      <span className="kpi__label">{label}</span>
      <span className="kpi__value">{value ?? '—'}</span>
      {hint ? <span className="kpi__hint">{hint}</span> : null}
      {spark && spark.length > 1 ? (
        <div className="kpi__spark">
          <Sparkline values={spark} label={`${label} trend`} />
        </div>
      ) : null}
    </div>
  );
}
