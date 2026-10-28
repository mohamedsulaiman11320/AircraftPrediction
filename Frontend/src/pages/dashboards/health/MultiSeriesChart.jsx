import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { linearScale, niceTicks, formatTick, formatTimeTick, timeTicks } from '../../../components/charts/scale.js';
import { formatDateTime } from '../../../utils/format.js';
import { METRICS, breachPoints, isBreach, limitFor, limitText } from './healthModel.js';

const AXIS_W = 48;          // width of each y-axis gutter
const PAD = { top: 26, bottom: 24 };
/** Series hidden until the user ticks them (user decision 2026-10-28: vibration has no approved limit). */
const HIDDEN_BY_DEFAULT = ['vibrationIndex'];

/** What the values are - this chart never mixes them. */
export const SERIES_KIND = { forecast: 'Forecast', history: 'Recorded' };

function useSize(ref, fallback) {
  const [size, setSize] = useState(fallback);
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el || typeof ResizeObserver === 'undefined') return undefined;
    const ro = new ResizeObserver(([entry]) => {
      const { width, height } = entry.contentRect;
      if (width > 0) setSize({ width, height });
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, [ref]);
  return size;
}

function pathFor(points, key, x, y) {
  let d = '';
  let pen = false;
  for (const p of points) {
    const v = p[key];
    if (typeof v !== 'number' || !Number.isFinite(v)) {
      pen = false;
      continue;
    }
    d += `${pen ? 'L' : 'M'}${x(p.t).toFixed(1)},${y(v).toFixed(1)}`;
    pen = true;
  }
  return d;
}

/** Episode marker: a diamond in the series colour - a different SHAPE from the round breach nodes. */
function diamond(cx, cy, r) {
  return `M${cx},${cy - r}L${cx + r},${cy}L${cx},${cy + r}L${cx - r},${cy}Z`;
}

/** One series on its own y-scale: band, line, points, limit line and breach markers. */
/**
 * Breach EPISODES: consecutive points beyond the limit form one episode, marked once at its first point (the exact
 * projected time the limit is crossed) - hundreds of hourly markers would hide the lines.
 */
function breachEpisodes(points, key, limit) {
  const hits = new Set(breachPoints(points, key, limit).map((b) => b.t));
  const episodes = [];
  let current = null;
  for (const p of points) {
    if (hits.has(p.timestampUtc)) {
      if (!current) episodes.push((current = { t: p.timestampUtc, value: p[key], points: [] }));
      current.points.push(p);
    } else current = null;
  }
  return episodes;
}

function Series({ metric, points, x, y, limit, plot, onBreach, registration, kindLabel, markerR }) {
  const markers = useMemo(() => breachEpisodes(points, metric.key, limit), [points, metric.key, limit]);
  const [active, setActive] = useState(0);
  const refs = useRef([]);
  useEffect(() => setActive(0), [markers.length]);
  const move = (i) => {
    const next = (i + markers.length) % markers.length;
    setActive(next);
    refs.current[next]?.focus();
  };
  const open = (m) => onBreach?.({ metric, limit, timestampUtc: m.t, value: m.value });
  const hasValues = points.some((p) => typeof p[metric.key] === 'number');

  return (
    <g data-testid={`panel-${metric.key}`}>
      {hasValues ? (
        <path d={pathFor(points, metric.key, x, y)} fill="none" stroke={metric.color} strokeWidth={2} strokeLinejoin="round" data-testid={`series-${metric.key}`} data-color={metric.color} />
      ) : null}
      {limit ? (
        <g data-testid={`limit-${metric.key}`}>
          <line x1={plot.left} x2={plot.right} y1={y(limit.value)} y2={y(limit.value)} stroke={metric.color} strokeWidth={1.5} strokeDasharray="6 4" />
        </g>
      ) : null}
      {/* Keyboard access only: one focusable diamond per breach run, invisible until focused (no resting dots). */}
      {markers.length ? (
        <g role="group" aria-label={`${markers.length} ${kindLabel.toLowerCase()} ${metric.name.toLowerCase()} values ${limitText(limit)}. Arrow keys move between them, Enter opens.`}>
          {markers.map((m, i) => (
            <path
              key={m.t}
              ref={(el) => {
                refs.current[i] = el;
              }}
              d={diamond(x(Date.parse(m.t)), y(m.value), Math.max(markerR, 7))}
              data-points={m.points.length}
              data-testid="breach-marker"
              data-kind={kindLabel}
              data-metric={metric.key}
              data-timestamp={m.t}
              fill={metric.color}
              stroke="#fff"
              strokeWidth={2.5}
              data-color={metric.color}
              tabIndex={i === active ? 0 : -1}
              role="button"
              aria-label={`${registration}: ${kindLabel.toLowerCase()} ${metric.name} ${formatTick(m.value)} ${metric.unit} at ${formatDateTime(m.t)}, ${limitText(limit)} (${m.points.length} hours beyond the limit) - open the forecast anomaly`}
              className="cursor-pointer opacity-0 outline-none focus:opacity-100 focus-visible:stroke-brand focus-visible:[stroke-width:3]"
              onClick={() => open(m)}
              onKeyDown={(e) => {
                if (e.key === 'ArrowRight' || e.key === 'ArrowDown') {
                  e.preventDefault();
                  move(i + 1);
                } else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') {
                  e.preventDefault();
                  move(i - 1);
                } else if (e.key === 'Enter' || e.key === ' ') {
                  e.preventDefault();
                  open(m);
                }
              }}
            >
              <title>{`${kindLabel} ${metric.name} crosses the limit (${limitText(limit)}) at ${formatDateTime(m.t)}: ${formatTick(m.value)} ${metric.unit}; ${m.points.length} hours beyond the limit`}</title>
            </path>
          ))}
        </g>
      ) : null}
    </g>
  );
}

/**
 * ONE line chart for all visible series, each on its own y-axis (user decision 2026-10-28): temperature on the left
 * axis, oil pressure and vibration on right-hand axes, each axis coloured and titled with its series and unit. Approved
 * limits are dashed lines in the series colour. At rest the lines carry no dots; hovering a forecast hour shows a
 * crosshair and tooltip, and a bold node in the series colour on every series beyond its limit there - clicking it opens
 * the forecast anomaly (user decision 2026-10-28).
 */
export function MultiSeriesChart({ points: raw, visible, thresholds, registration, onBreach, kind, emptyText }) {
  const boxRef = useRef(null);
  const { width, height } = useSize(boxRef, { width: 720, height: 300 });
  const [hoverT, setHoverT] = useState(null);
  const kindLabel = SERIES_KIND[kind];
  const metrics = METRICS.filter((m) => visible.has(m.key));

  const points = useMemo(
    () => (raw || []).map((p) => ({ ...p, t: Date.parse(p.timestampUtc) })).filter((p) => Number.isFinite(p.t)),
    [raw],
  );
  if (!metrics.length)
    return (
      <p className="m-0 py-10 text-center text-[13px] text-ink-muted" role="status">
        Select at least one parameter below the chart to show its {kindLabel.toLowerCase()} values.
      </p>
    );

  const rightAxes = Math.max(0, metrics.length - 1);
  const plot = { left: AXIS_W, right: width - AXIS_W * rightAxes, top: PAD.top, bottom: height - PAD.bottom };
  const tMin = points[0]?.t ?? 0;
  const tMax = points[points.length - 1]?.t ?? 1;
  const x = linearScale([tMin, tMax], [plot.left, plot.right]);
  const spacing = (plot.right - plot.left) / Math.max(1, points.length);
  const markerR = Math.max(3.5, Math.min(6, spacing * 0.9));

  const scales = metrics.map((m) => {
    const limit = limitFor(m.key, thresholds);
    const vals = points.flatMap((p) => [p[m.key], p[`${m.key}Low`], p[`${m.key}High`]]).filter((v) => typeof v === 'number' && Number.isFinite(v));
    const ticks = vals.length ? niceTicks(Math.min(...vals, ...(limit ? [limit.value] : [])), Math.max(...vals, ...(limit ? [limit.value] : [])), 5) : [0, 1];
    return { m, limit, ticks, y: linearScale([ticks[0], ticks[ticks.length - 1]], [plot.bottom, plot.top]), hasValues: vals.length > 0 };
  });
  const hover = hoverT == null ? null : points.find((p) => p.t === hoverT);
  // Tracked on the whole SVG (not the plot overlay) so moving onto a hover node does not count as leaving the chart.
  const onMove = (e) => {
    const mx = e.clientX - e.currentTarget.getBoundingClientRect().left;
    if (mx < plot.left - 4 || mx > plot.right + 4) {
      setHoverT(null);
      return;
    }
    let best = null;
    for (const p of points) if (best === null || Math.abs(x(p.t) - mx) < Math.abs(x(best.t) - mx)) best = p;
    setHoverT(best?.t ?? null);
  };
  const tipLeft = hover ? Math.min(Math.max(x(hover.t) + 10, 0), width - 270) : 0;
  const breachingAtHover = hover
    ? scales.filter((s) => s.limit && typeof hover[s.m.key] === 'number' && isBreach(hover[s.m.key], s.limit))
    : [];
  const missing = scales.filter((s) => !s.hasValues);

  return (
    <div ref={boxRef} className="relative h-full min-h-[240px] w-full bg-white" data-testid={`chart-${kind}`}>
      <svg
        width={width}
        height={height}
        role="img"
        aria-label={`${registration} ${kindLabel.toLowerCase()} values from ${formatDateTime(points[0]?.timestampUtc)} to ${formatDateTime(points[points.length - 1]?.timestampUtc)}: ${metrics.map((m) => m.name).join(', ')}`}
        className="block"
        onMouseMove={onMove}
        onMouseLeave={() => setHoverT(null)}
      >
        <rect x={0} y={0} width={width} height={height} fill="#ffffff" data-testid="chart-background" />
        {scales[0].ticks.map((t) => (
          <line key={t} x1={plot.left} x2={plot.right} y1={scales[0].y(t)} y2={scales[0].y(t)} stroke="var(--chart-grid)" />
        ))}
        {scales.map((s, i) => {
          const axisX = i === 0 ? plot.left : plot.right + AXIS_W * (i - 1);
          const anchor = i === 0 ? 'end' : 'start';
          const dx = i === 0 ? -6 : 6;
          return (
            <g key={s.m.key} data-testid={`axis-${s.m.key}`}>
              <line x1={axisX} x2={axisX} y1={plot.top} y2={plot.bottom} stroke={s.m.color} strokeWidth={1.5} />
              {s.ticks.map((t) => (
                <text key={t} x={axisX + dx} y={s.y(t)} textAnchor={anchor} dominantBaseline="middle" className="text-[10px] tabular-nums" fill={s.m.color}>
                  {formatTick(t)}
                </text>
              ))}
              <text x={axisX + (i === 0 ? -4 : 4)} y={plot.top - 10} textAnchor={anchor} className="text-[10px] font-semibold" fill={s.m.color}>
                {s.m.unit}
              </text>
            </g>
          );
        })}
        <rect
          x={plot.left}
          y={plot.top}
          width={Math.max(0, plot.right - plot.left)}
          height={Math.max(0, plot.bottom - plot.top)}
          fill="transparent"
        />
        {hover ? <line x1={x(hover.t)} x2={x(hover.t)} y1={plot.top} y2={plot.bottom} stroke="var(--chart-axis)" strokeDasharray="2 3" pointerEvents="none" /> : null}
        {scales.map((s) => (
          <Series
            key={s.m.key}
            metric={s.m}
            points={points}
            x={x}
            y={s.y}
            limit={s.limit}
            plot={plot}
            onBreach={onBreach}
            registration={registration}
            kindLabel={kindLabel}
            markerR={markerR}
          />
        ))}
        {/* Limit key at the plot's top-right: never collides with the lines, whatever the horizon. */}
        {scales
          .filter((s) => s.limit)
          .map((s, i) => (
            <text
              key={s.m.key}
              x={plot.right - 6}
              y={plot.top + 12 + i * 14}
              textAnchor="end"
              className="text-[10px] font-semibold"
              fill={s.m.color}
              paintOrder="stroke"
              stroke="#fff"
              strokeWidth={3}
              data-testid={`limit-label-${s.m.key}`}
            >
              - - {s.m.short} limit {s.limit.value} {s.m.unit}
            </text>
          ))}
        {breachingAtHover.map((s) => (
          <circle
            key={s.m.key}
            cx={x(hover.t)}
            cy={s.y(hover[s.m.key])}
            r={6}
            fill={s.m.color}
            stroke="#fff"
            strokeWidth={2}
            className="cursor-pointer"
            data-testid="breach-node"
            data-metric={s.m.key}
            data-color={s.m.color}
            onClick={() => onBreach?.({ metric: s.m, limit: s.limit, timestampUtc: hover.timestampUtc, value: hover[s.m.key] })}
          >
            <title>{`${s.m.name} ${formatTick(hover[s.m.key])} ${s.m.unit} - ${limitText(s.limit)}. Click to view anomaly.`}</title>
          </circle>
        ))}
        {missing.length ? (
          <text x={(plot.left + plot.right) / 2} y={plot.bottom - 8} textAnchor="middle" className="fill-ink-muted text-[11px]" data-testid="missing-series-note">
            {missing.map((s) => (emptyText ? emptyText(s.m) : `No ${s.m.name.toLowerCase()} values`)).join(' · ')}
          </text>
        ) : null}
        {timeTicks(tMin, tMax, Math.max(2, Math.min(7, Math.floor((plot.right - plot.left) / 110)))).map((t) => (
          <text key={t} x={x(t)} y={height - 6} textAnchor="middle" className="fill-ink-muted text-[10px]">
            {formatTimeTick(t, tMax - tMin)}
          </text>
        ))}
      </svg>
      {hover ? (
        <div
          role="status"
          className="pointer-events-none absolute top-1 z-10 w-[260px] rounded-lg border border-line bg-white px-2.5 py-1.5 text-[12px] text-ink shadow-[0_8px_24px_rgba(25,22,34,0.16)]"
          style={{ left: tipLeft }}
        >
          <div className="font-semibold">
            {kindLabel} · {formatDateTime(hover.timestampUtc)}
          </div>
          {metrics.map((m) => (
            <div key={m.key} className="flex items-start justify-between gap-2">
              <span className="flex items-center gap-1 text-ink-muted">
                <span className="inline-block h-2 w-2 rounded-full" style={{ background: m.color }} aria-hidden="true" />
                {m.name}
              </span>
              <span className="text-right tabular-nums">
                {typeof hover[m.key] === 'number' ? `${formatTick(hover[m.key])} ${m.unit}` : 'Unavailable'}
                {breachingAtHover.some((s) => s.m.key === m.key) ? (
                  <span className="block text-[11px] font-semibold text-crit" data-testid="tooltip-breach">
                    Beyond limit ({limitText(scales.find((s) => s.m.key === m.key).limit)}) · Click to view anomaly
                  </span>
                ) : null}
                {typeof hover[`${m.key}Low`] === 'number' && typeof hover[`${m.key}High`] === 'number' ? (
                  <span className="block text-[11px] text-ink-muted">
                    80% interval {formatTick(hover[`${m.key}Low`])}–{formatTick(hover[`${m.key}High`])}
                  </span>
                ) : null}
              </span>
            </div>
          ))}
        </div>
      ) : null}
    </div>
  );
}

/** Checkbox per parameter with its colour; unchecking hides a series (and its axis), never its data. */
export function SeriesLegend({ visible, onToggle, idPrefix }) {
  return (
    <fieldset className="m-0 flex flex-wrap items-center gap-x-4 gap-y-1 border-0 p-0" data-testid={`legend-${idPrefix}`}>
      <legend className="sr-only">Parameters to show</legend>
      {METRICS.map((m) => (
        <label key={m.key} className="flex cursor-pointer items-center gap-1.5 text-[12px] text-ink">
          <input
            type="checkbox"
            checked={visible.has(m.key)}
            onChange={() => onToggle(m.key)}
            className="h-3.5 w-3.5 cursor-pointer rounded accent-brand"
            data-testid={`toggle-${idPrefix}-${m.key}`}
          />
          <span className="inline-block h-[3px] w-4 rounded" style={{ background: m.color }} aria-hidden="true" />
          {m.name} <span className="text-ink-muted">({m.unit})</span>
        </label>
      ))}
    </fieldset>
  );
}

/** Visible-series state shared by a legend and a chart: all three on by default. */
export function useVisibleSeries() {
  const [visible, setVisible] = useState(() => new Set(METRICS.map((m) => m.key).filter((k) => !HIDDEN_BY_DEFAULT.includes(k))));
  const toggle = (key) =>
    setVisible((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  return [visible, toggle];
}
