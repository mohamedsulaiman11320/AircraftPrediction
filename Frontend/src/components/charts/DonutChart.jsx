import { useId } from 'react';

/** Donut chart with a legend listing every value (identity never relies on colour alone). */
export function DonutChart({ data = [], size = 180, thickness = 26, ariaLabel = 'Distribution', centerLabel = 'Total' }) {
  const id = useId();
  const total = data.reduce((s, d) => s + (Number(d.value) || 0), 0);
  const r = (size - thickness) / 2;
  const c = size / 2;
  const circ = 2 * Math.PI * r;
  let offset = 0;
  const gap = total > 0 && data.filter((d) => d.value > 0).length > 1 ? 2 : 0;
  const summary = data.map((d) => `${d.label}: ${d.value}`).join(', ');

  return (
    <div className="donut-layout">
      <svg viewBox={`0 0 ${size} ${size}`} width={size} height={size} role="img" aria-labelledby={`${id}-t`}>
        <title id={`${id}-t`}>{`${ariaLabel}. ${summary}. ${centerLabel} ${total}.`}</title>
        <circle cx={c} cy={c} r={r} fill="none" stroke="var(--slate-100)" strokeWidth={thickness} />
        {total > 0
          ? data.map((d) => {
              const v = Number(d.value) || 0;
              if (v <= 0) return null;
              const len = (v / total) * circ;
              const seg = (
                <circle
                  key={d.label}
                  cx={c}
                  cy={c}
                  r={r}
                  fill="none"
                  stroke={d.color}
                  strokeWidth={thickness}
                  strokeDasharray={`${Math.max(0, len - gap)} ${circ}`}
                  strokeDashoffset={-offset}
                  transform={`rotate(-90 ${c} ${c})`}
                >
                  <title>{`${d.label}: ${v}`}</title>
                </circle>
              );
              offset += len;
              return seg;
            })
          : null}
        <text x={c} y={c - 2} textAnchor="middle" style={{ fontFamily: 'var(--font-heading)', fontSize: 26, fontWeight: 700, fill: 'var(--color-text)' }}>
          {total}
        </text>
        <text x={c} y={c + 18} textAnchor="middle" style={{ fontSize: 11, fill: 'var(--color-text-subtle)' }}>
          {centerLabel}
        </text>
      </svg>
      <ul className="donut-legend">
        {data.map((d) => (
          <li key={d.label}>
            <span className="chart__swatch" style={{ background: d.color }} aria-hidden="true" />
            {d.label}
            <strong>{d.value}</strong>
          </li>
        ))}
      </ul>
    </div>
  );
}
