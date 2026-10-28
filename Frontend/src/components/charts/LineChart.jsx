import { useId, useMemo, useState } from 'react';
import { formatTick, formatTimeTick, linearScale, niceTicks, timeTicks } from './scale.js';
import { formatDateTime } from '../../utils/format.js';

const W = 720;
const M = { top: 16, right: 20, bottom: 32, left: 52 };

/**
 * Time-series line chart (single y-axis by design; plot different units in separate charts).
 * series: [{ name, color, data: [{ x: ISO string | Date | number, y: number | null }] }]
 * Null y values create gaps. Hover / arrow keys show a crosshair tooltip; a table view is provided.
 */
export function LineChart({ series = [], height = 240, unit = '', ariaLabel, emptyText = 'No data in this range.' }) {
  const id = useId();
  const [active, setActive] = useState(null);
  const H = height;

  const prepared = useMemo(
    () =>
      series.map((s) => ({
        ...s,
        pts: (s.data || [])
          .map((p) => ({ t: new Date(p.x).getTime(), y: p.y === null || p.y === undefined ? null : Number(p.y) }))
          .filter((p) => Number.isFinite(p.t))
          .sort((a, b) => a.t - b.t),
      })),
    [series],
  );

  const times = useMemo(() => {
    const set = new Set();
    prepared.forEach((s) => s.pts.forEach((p) => set.add(p.t)));
    return Array.from(set).sort((a, b) => a - b);
  }, [prepared]);

  const ys = prepared.flatMap((s) => s.pts.map((p) => p.y)).filter((v) => v !== null && Number.isFinite(v));
  if (times.length === 0 || ys.length === 0) {
    return (
      <div className="state" style={{ padding: 24 }}>
        <p className="muted">{emptyText}</p>
      </div>
    );
  }

  const tMin = times[0];
  const tMax = times[times.length - 1] === tMin ? tMin + 3600000 : times[times.length - 1];
  const yTicks = niceTicks(Math.min(...ys), Math.max(...ys), 5);
  const x = linearScale([tMin, tMax], [M.left, W - M.right]);
  const y = linearScale([yTicks[0], yTicks[yTicks.length - 1]], [H - M.bottom, M.top]);
  const xTicks = timeTicks(tMin, tMax, Math.min(6, Math.max(2, times.length)));

  const pathFor = (pts) => {
    let d = '';
    let pen = false;
    pts.forEach((p) => {
      if (p.y === null) {
        pen = false;
        return;
      }
      d += `${pen ? 'L' : 'M'}${x(p.t).toFixed(1)},${y(p.y).toFixed(1)}`;
      pen = true;
    });
    return d;
  };

  const activeT = active !== null ? times[active] : null;
  const valuesAt = (t) => prepared.map((s) => ({ name: s.name, color: s.color, y: s.pts.find((p) => p.t === t)?.y ?? null }));

  const onMove = (e) => {
    const svg = e.currentTarget.ownerSVGElement || e.currentTarget;
    const rect = svg.getBoundingClientRect();
    if (!rect.width) return;
    const px = ((e.clientX - rect.left) / rect.width) * W;
    let best = 0;
    let bestD = Infinity;
    times.forEach((t, i) => {
      const d = Math.abs(x(t) - px);
      if (d < bestD) {
        bestD = d;
        best = i;
      }
    });
    setActive(best);
  };

  const onKeyDown = (e) => {
    if (e.key === 'ArrowRight') {
      e.preventDefault();
      setActive((a) => (a === null ? 0 : Math.min(times.length - 1, a + 1)));
    } else if (e.key === 'ArrowLeft') {
      e.preventDefault();
      setActive((a) => (a === null ? times.length - 1 : Math.max(0, a - 1)));
    } else if (e.key === 'Escape') {
      setActive(null);
    }
  };

  const label = ariaLabel || series.map((s) => s.name).join(' and ');
  const tooltipLeftPct = activeT !== null ? (x(activeT) / W) * 100 : 0;

  return (
    <figure className="chart" style={{ margin: 0 }}>
      <div className="chart-wrap">
        <svg
          viewBox={`0 0 ${W} ${H}`}
          role="img"
          aria-labelledby={`${id}-title`}
          tabIndex={0}
          onKeyDown={onKeyDown}
          onBlur={() => setActive(null)}
        >
          <title id={`${id}-title`}>{`${label}${unit ? ` (${unit})` : ''}, ${times.length} points. Use arrow keys to inspect values.`}</title>
          {yTicks.map((t) => (
            <g key={t}>
              <line x1={M.left} x2={W - M.right} y1={y(t)} y2={y(t)} stroke="var(--chart-grid)" strokeWidth="1" />
              <text x={M.left - 8} y={y(t) + 4} textAnchor="end">
                {formatTick(t)}
              </text>
            </g>
          ))}
          {/* The axis unit, anchored at the start of the plot area so it never sits on the first y tick. */}
          {unit ? (
            <text x={M.left} y={M.top - 5} textAnchor="start" fontWeight="600">
              {unit}
            </text>
          ) : null}
          {xTicks.map((t) => (
            <text key={t} x={x(t)} y={H - 10} textAnchor="middle">
              {formatTimeTick(t, tMax - tMin)}
            </text>
          ))}
          <line x1={M.left} x2={W - M.right} y1={H - M.bottom} y2={H - M.bottom} stroke="var(--color-border-strong)" />
          {prepared.map((s) => (
            <path key={s.name} d={pathFor(s.pts)} fill="none" stroke={s.color} strokeWidth="2" strokeLinejoin="round" strokeLinecap="round" />
          ))}
          {activeT !== null ? (
            <g aria-hidden="true">
              <line x1={x(activeT)} x2={x(activeT)} y1={M.top} y2={H - M.bottom} stroke="var(--slate-400)" strokeDasharray="3 3" />
              {valuesAt(activeT).map((v) =>
                v.y === null ? null : <circle key={v.name} cx={x(activeT)} cy={y(v.y)} r="4.5" fill={v.color} stroke="#fff" strokeWidth="2" />,
              )}
            </g>
          ) : null}
          <rect
            x={M.left}
            y={M.top}
            width={W - M.left - M.right}
            height={H - M.top - M.bottom}
            fill="transparent"
            onMouseMove={onMove}
            onMouseLeave={() => setActive(null)}
          />
        </svg>
        {activeT !== null ? (
          <div className="chart__tooltip" style={{ left: `${tooltipLeftPct}%`, top: 0 }} role="status">
            <div>{formatDateTime(activeT)}</div>
            {valuesAt(activeT).map((v) => (
              <div key={v.name}>
                <span className="chart__swatch" style={{ background: v.color, marginRight: 6 }} />
                {v.name}: {v.y === null ? 'no data' : `${formatTick(v.y)}${unit ? ` ${unit}` : ''}`}
              </div>
            ))}
          </div>
        ) : null}
      </div>
      {series.length > 1 ? (
        <ul className="chart__legend" aria-label="Legend">
          {series.map((s) => (
            <li key={s.name}>
              <span className="chart__swatch" style={{ background: s.color }} aria-hidden="true" />
              {s.name}
            </li>
          ))}
        </ul>
      ) : null}
      <details className="subtle" style={{ marginTop: 8 }}>
        <summary>View as table</summary>
        <div className="table-wrap" style={{ maxHeight: 260, overflowY: 'auto' }}>
          <table className="data-table">
            <thead>
              <tr>
                <th scope="col">Time (UTC)</th>
                {series.map((s) => (
                  <th key={s.name} scope="col" className="num">
                    {s.name}
                    {unit ? ` (${unit})` : ''}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {times.map((t) => (
                <tr key={t}>
                  <td>{formatDateTime(t)}</td>
                  {valuesAt(t).map((v) => (
                    <td key={v.name} className="num">
                      {v.y === null ? '—' : formatTick(v.y)}
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
