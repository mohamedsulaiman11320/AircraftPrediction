import { useId, useState } from 'react';
import { formatTick, linearScale, niceTicks } from './scale.js';

const W = 720;
const M = { top: 16, right: 16, bottom: 36, left: 44 };

/**
 * Grouped vertical bar chart.
 * data: [{ label, [seriesKey]: number }]; series: [{ key, name, color }]
 */
export function BarChart({ data = [], series = [], height = 240, ariaLabel, emptyText = 'No data.' }) {
  const id = useId();
  const [hover, setHover] = useState(null);
  const H = height;
  const values = data.flatMap((d) => series.map((s) => Number(d[s.key]) || 0));
  if (data.length === 0 || series.length === 0) {
    return (
      <div className="state" style={{ padding: 24 }}>
        <p className="muted">{emptyText}</p>
      </div>
    );
  }
  const ticks = niceTicks(0, Math.max(1, ...values), 5);
  const y = linearScale([0, ticks[ticks.length - 1]], [H - M.bottom, M.top]);
  const plotW = W - M.left - M.right;
  const groupW = plotW / data.length;
  const pad = Math.min(24, groupW * 0.2);
  const barW = Math.max(2, (groupW - pad * 2 - (series.length - 1) * 2) / series.length);
  const label = ariaLabel || `Bar chart of ${series.map((s) => s.name).join(', ')}`;

  return (
    <figure className="chart" style={{ margin: 0 }}>
      <div className="chart-wrap">
        <svg viewBox={`0 0 ${W} ${H}`} role="img" aria-labelledby={`${id}-t`}>
          <title id={`${id}-t`}>{label}</title>
          {ticks.map((t) => (
            <g key={t}>
              <line x1={M.left} x2={W - M.right} y1={y(t)} y2={y(t)} stroke="var(--chart-grid)" />
              <text x={M.left - 8} y={y(t) + 4} textAnchor="end">
                {formatTick(t)}
              </text>
            </g>
          ))}
          {data.map((d, gi) => {
            const gx = M.left + gi * groupW + pad;
            return (
              <g key={d.label}>
                {series.map((s, si) => {
                  const v = Number(d[s.key]) || 0;
                  const bx = gx + si * (barW + 2);
                  const by = y(v);
                  const bh = Math.max(0, H - M.bottom - by);
                  const isHover = hover && hover.gi === gi && hover.si === si;
                  return (
                    <g key={s.key} onMouseEnter={() => setHover({ gi, si })} onMouseLeave={() => setHover(null)}>
                      <rect x={bx - 1} y={M.top} width={barW + 2} height={H - M.bottom - M.top} fill="transparent" />
                      <path
                        d={roundedTop(bx, by, barW, bh, Math.min(4, barW / 2))}
                        fill={s.color}
                        opacity={hover && !isHover ? 0.55 : 1}
                      />
                      <title>{`${d.label} · ${s.name}: ${v}`}</title>
                    </g>
                  );
                })}
                <text x={M.left + gi * groupW + groupW / 2} y={H - 12} textAnchor="middle">
                  {d.label}
                </text>
              </g>
            );
          })}
          <line x1={M.left} x2={W - M.right} y1={H - M.bottom} y2={H - M.bottom} stroke="var(--color-border-strong)" />
        </svg>
        {hover ? (
          <div
            className="chart__tooltip"
            style={{ left: `${((M.left + hover.gi * groupW + groupW / 2) / W) * 100}%`, top: 0 }}
            aria-hidden="true"
          >
            {data[hover.gi].label} · {series[hover.si].name}: {Number(data[hover.gi][series[hover.si].key]) || 0}
          </div>
        ) : null}
      </div>
      {series.length > 1 ? (
        <ul className="chart__legend" aria-label="Legend">
          {series.map((s) => (
            <li key={s.key}>
              <span className="chart__swatch" style={{ background: s.color }} aria-hidden="true" />
              {s.name}
            </li>
          ))}
        </ul>
      ) : null}
      <details className="subtle" style={{ marginTop: 8 }}>
        <summary>View as table</summary>
        <div className="table-wrap">
          <table className="data-table">
            <thead>
              <tr>
                <th scope="col">Period</th>
                {series.map((s) => (
                  <th key={s.key} scope="col" className="num">
                    {s.name}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {data.map((d) => (
                <tr key={d.label}>
                  <td>{d.label}</td>
                  {series.map((s) => (
                    <td key={s.key} className="num">
                      {Number(d[s.key]) || 0}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </details>
    </figure>
  );
}

function roundedTop(x, y, w, h, r) {
  if (h <= 0) return '';
  const rr = Math.min(r, h);
  return `M${x},${y + h}V${y + rr}Q${x},${y} ${x + rr},${y}H${x + w - rr}Q${x + w},${y} ${x + w},${y + rr}V${y + h}Z`;
}
