import { Ring } from '../dashboards/health/KpiCards.jsx';
import { percent } from '../dashboards/health/healthModel.js';

/** Fleet condition from each aircraft's open 30-day forecast incident (`forecastCondition`) and its derived state. */
export const FLEET_CONDITIONS = [
  { key: 'available', label: 'Available', tone: 'ok', swatch: 'var(--color-ok)' },
  { key: 'critical', label: 'Critical', tone: 'warn', swatch: 'var(--color-warn)', hint: 'Temperature forecast above its limit' },
  { key: 'highCritical', label: 'High Critical', tone: 'crit', swatch: 'var(--color-crit)', hint: 'Temperature and oil pressure forecast beyond their limits' },
  { key: 'other', label: 'Other status', tone: 'idle', swatch: 'var(--color-idle)' },
];

export function fleetCondition(a) {
  if (a.forecastCondition === 'HighCritical') return 'highCritical';
  if (a.forecastCondition === 'Critical') return 'critical';
  return (a.derivedState || a.operationalStatus) === 'Available' ? 'available' : 'other';
}

/** Donut plus one toggle button per condition; pressing a condition filters the Fleet Health table. */
export function FleetConditionCard({ aircraft, selected, onSelect }) {
  const counts = Object.fromEntries(FLEET_CONDITIONS.map((c) => [c.key, 0]));
  for (const a of aircraft) counts[fleetCondition(a)] += 1;
  const segments = FLEET_CONDITIONS.filter((c) => c.key !== 'other' || counts.other > 0).map((c) => ({ ...c, value: counts[c.key] }));
  const total = aircraft.length;
  return (
    <section className="card" aria-label="Aircraft health" data-testid="fleet-condition">
      <div className="flex flex-wrap items-center gap-5 p-4">
        <Ring segments={segments} total={total} size={96} thickness={14} />
        <div className="flex min-w-0 flex-1 flex-col gap-2">
          <h2 className="m-0 text-[15px] font-semibold text-ink">Aircraft health</h2>
          <div className="grid gap-2 sm:grid-cols-3" role="group" aria-label="Filter the table by condition">
            {segments.map((s) => (
              <button
                key={s.key}
                type="button"
                aria-pressed={selected === s.key}
                onClick={() => onSelect(selected === s.key ? null : s.key)}
                title={s.hint}
                data-testid={`condition-${s.key}`}
                className={[
                  'flex items-center gap-3 rounded-lg border bg-white px-3 py-2 text-left',
                  selected === s.key ? 'border-brand ring-2 ring-brand/30' : 'border-line hover:border-brand',
                ].join(' ')}
              >
                <span className="h-8 w-1.5 shrink-0 rounded-full" style={{ background: s.swatch }} aria-hidden="true" />
                <span className="flex min-w-0 flex-1 flex-col">
                  <span className="text-[22px] font-semibold leading-none tabular-nums text-ink" data-testid="condition-count">{s.value}</span>
                  <span className="truncate text-[12px] text-ink-muted">{s.label}</span>
                </span>
                <span className="text-[12px] tabular-nums text-ink-muted">{percent(s.value, total)}%</span>
              </button>
            ))}
          </div>
        </div>
      </div>
    </section>
  );
}
