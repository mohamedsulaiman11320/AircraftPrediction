import { useId } from 'react';
import { Link } from '../../../router/Link.jsx';
import { Icon } from '../../../components/Icon.jsx';
import { percent, thresholdSegments, workOrderSegments } from './healthModel.js';

/** Fill for each tone. Provisional scores get a hatched neutral fill - never a risk colour. */
const TONE_FILL = {
  crit: 'var(--color-crit)',
  ok: 'var(--color-ok)',
  warn: 'var(--color-warn)',
  idle: 'var(--color-idle)',
  brand: 'var(--color-brand)',
  'wo-1': 'var(--color-wo-1)',
  'wo-2': 'var(--color-wo-2)',
  'wo-3': 'var(--color-wo-3)',
  'wo-4': 'var(--color-wo-4)',
  'wo-5': 'var(--color-wo-5)',
};

function fillFor(tone, hatchId) {
  return tone === 'provisional' ? `url(#${hatchId})` : TONE_FILL[tone];
}

function Hatch({ id }) {
  return (
    <pattern id={id} width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
      <rect width="6" height="6" fill="var(--color-idle)" opacity="0.35" />
      <line x1="0" y1="0" x2="0" y2="6" stroke="var(--color-idle)" strokeWidth="3" />
    </pattern>
  );
}

/** Donut ring: arcs with a 2px surface gap; the total sits in the centre. Decorative - the legend carries the data. */
export function Ring({ segments, total, size = 72, thickness = 11 }) {
  const hatchId = useId().replace(/:/g, '');
  const r = (size - thickness) / 2;
  const c = size / 2;
  const circ = 2 * Math.PI * r;
  const shown = segments.filter((s) => s.value > 0);
  const gap = shown.length > 1 ? 2 : 0;
  let offset = 0;
  return (
    <svg viewBox={`0 0 ${size} ${size}`} width={size} height={size} aria-hidden="true" className="shrink-0">
      <defs>
        <Hatch id={hatchId} />
      </defs>
      <circle cx={c} cy={c} r={r} fill="none" stroke="var(--color-line)" strokeWidth={thickness} />
      {total > 0
        ? shown.map((s) => {
            const len = (s.value / total) * circ;
            const arc = (
              <circle
                key={s.key}
                cx={c}
                cy={c}
                r={r}
                fill="none"
                stroke={fillFor(s.tone, hatchId)}
                strokeWidth={thickness}
                strokeDasharray={`${Math.max(0, len - gap)} ${circ}`}
                strokeDashoffset={-offset}
                transform={`rotate(-90 ${c} ${c})`}
              />
            );
            offset += len;
            return arc;
          })
        : null}
      <text x={c} y={c - 2} textAnchor="middle" className="fill-ink text-[20px] font-semibold" dominantBaseline="middle">
        {total}
      </text>
      <text x={c} y={c + 15} textAnchor="middle" className="fill-ink-muted text-[9px]">
        aircraft
      </text>
    </svg>
  );
}

function Swatch({ tone }) {
  const hatchId = useId().replace(/:/g, '');
  return (
    <svg width="10" height="10" aria-hidden="true" className="shrink-0">
      <defs>
        <Hatch id={hatchId} />
      </defs>
      <rect width="10" height="10" rx="2" fill={fillFor(tone, hatchId)} />
    </svg>
  );
}

/** Legend rows: swatch + icon + label + count + percentage (text in ink tokens, never in the series colour). */
function Legend({ segments, total, withIcons = true, columns = 1 }) {
  return (
    <ul className={['m-0 grid min-w-0 flex-1 list-none gap-x-4 gap-y-1 p-0 text-[13px]', columns === 2 ? 'sm:grid-cols-2' : ''].join(' ')}>
      {segments.map((s) => (
        <li key={s.key} className="flex items-center gap-2">
          <Swatch tone={s.tone} />
          {withIcons && s.icon ? <Icon name={s.icon} size={14} className="shrink-0 text-ink-muted" /> : null}
          <span className="min-w-0 flex-1 truncate text-ink">{s.label}</span>
          <span className="tabular-nums font-semibold text-ink">{s.value}</span>
          <span className="w-9 text-right tabular-nums text-ink-muted">{percent(s.value, total)}%</span>
        </li>
      ))}
    </ul>
  );
}

/** The whole card is one link to an existing screen; it contains no other interactive element. */
function CardLink({ to, title, subtitle, children, testId, badge }) {
  return (
    <Link
      to={to}
      data-testid={testId}
      className="group flex min-w-0 flex-col gap-2 rounded-xl border border-line bg-white p-3 text-ink no-underline shadow-[0_1px_2px_rgba(25,22,34,0.06)] transition-colors hover:border-brand focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand"
    >
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <h2 className="m-0 text-[14px] font-semibold leading-tight text-ink">{title}</h2>
          {subtitle ? <p className="m-0 mt-0.5 text-[12px] text-ink-muted">{subtitle}</p> : null}
        </div>
        <span className="flex items-center gap-1 text-[12px] font-medium text-brand opacity-80 group-hover:opacity-100">
          {badge}
          <Icon name="chevronRight" size={14} />
        </span>
      </div>
      {children}
    </Link>
  );
}

export function ThresholdHealthCard({ health }) {
  const segments = thresholdSegments(health);
  const total = health?.aircraft ?? 0;
  return (
    <CardLink to="/fleet" testId="kpi-threshold" title="Threshold health" subtitle="Each aircraft's latest 24 hours of readings">
      <div className="flex items-center gap-3" role="group" aria-label={`Threshold health of ${total} aircraft: ${segments.map((s) => `${s.label} ${s.value}`).join(', ')}`}>
        <Ring segments={segments} total={total} />
        <Legend segments={segments} total={total} />
      </div>
      {health?.stale > 0 ? (
        <p className="m-0 text-[12px] text-warn" data-testid="stale-note">
          {health.stale} aircraft with stale data - status reflects their last readings.
        </p>
      ) : null}
    </CardLink>
  );
}

/** Lifecycle as one stacked bar (readable at any count), the legend listing every stage including zeros. */
export function WorkOrdersCard({ workOrders }) {
  const segments = workOrderSegments(workOrders);
  const total = segments.reduce((n, s) => n + s.value, 0);
  const shown = segments.filter((s) => s.value > 0);
  return (
    <CardLink to="/work-orders" testId="kpi-work-orders" title="Work orders" subtitle={`${total} in the active lifecycle`}>
      <div role="group" aria-label={`Work orders: ${segments.map((s) => `${s.label} ${s.value}`).join(', ')}`} className="flex flex-col gap-2">
        <div className="flex h-3 w-full gap-[2px] overflow-hidden rounded-full bg-line" aria-hidden="true">
          {shown.map((s) => (
            <span key={s.key} style={{ flexGrow: s.value, background: TONE_FILL[s.tone] }} className="h-full first:rounded-l-full last:rounded-r-full" />
          ))}
        </div>
        <Legend segments={segments} total={total} withIcons={false} columns={2} />
      </div>
      {workOrders?.cancelled ? <p className="m-0 text-[12px] text-ink-muted">{workOrders.cancelled} cancelled (not shown)</p> : null}
    </CardLink>
  );
}
