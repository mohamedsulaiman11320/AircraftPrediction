import { useId, useState } from 'react';
import { telemetryApi } from '../../api/telemetry.js';
import { useAsync } from '../../hooks/useAsync.js';
import { LineChart } from '../../components/charts/LineChart.jsx';
import { Card } from '../../components/PageHeader.jsx';
import { EmptyState, ErrorState } from '../../components/States.jsx';
import { Skeleton } from '../../components/Skeleton.jsx';
import { Alert } from '../../components/Misc.jsx';
import { RiskBadge } from '../../components/StatusBadge.jsx';
import { SelectField, TextField } from '../../components/FormFields.jsx';
import { AircraftSelect, useAircraftOptions } from '../shared/AircraftSelect.jsx';
import { BUCKET_OPTIONS, rangeErrorFor } from '../shared/dateRange.js';
import { daysAgo, formatDateTime, formatRelative, fromInputDate, toInputDate } from '../../utils/format.js';

/**
 * The exact sentence shown wherever a forecast would otherwise be expected. No approved supervised
 * failure-prediction model is eligible, so the panel plots recorded history only: no projection,
 * no future points, no confidence band and no predicted probability.
 */
export const NO_TELEMETRY_IN_RANGE = 'No telemetry recorded for this aircraft in the selected range.';

/**
 * One chart per unit. `LineChart` is single-y-axis by design, so °C, PSI and the dimensionless
 * vibration index each get their own chart with their own labelled axis instead of being squeezed
 * onto a shared scale. All three read the same time axis of real `timestampUtc` values.
 *
 * `pressurePsi` is already converted server-side (kPa x 0.1450377377) and is plotted exactly as
 * returned — converting again here would show a tenth of the real pressure.
 */
const SERIES = [
  {
    field: 'temperatureC',
    heading: 'Engine temperature (°C)',
    name: 'Engine temperature',
    unit: '°C',
    color: 'var(--chart-2)',
    empty: 'No engine-temperature readings in this range.',
  },
  {
    field: 'pressurePsi',
    heading: 'Oil pressure (PSI)',
    name: 'Oil pressure',
    unit: 'PSI',
    color: 'var(--chart-1)',
    empty: 'No oil-pressure readings in this range.',
  },
  {
    field: 'vibrationIndex',
    heading: 'Vibration index (dimensionless)',
    name: 'Vibration index',
    unit: 'index',
    color: 'var(--chart-4)',
    empty: 'No vibration readings in this range.',
  },
];

/**
 * Staleness against the engine-health evaluation window rather than a number invented here: every
 * health block carries `windowFromUtc`, the start of the window the threshold rules assessed, which
 * the backend derives from the seeded `health_threshold` row BR-01 "HighRiskWindowHours" (24 hours
 * at the time of writing). Reading the window back off the payload keeps this warning in step with
 * the rules instead of restating their number. Returns null when the window cannot be evaluated.
 */
export function stalenessOf(health, now = Date.now()) {
  const latest = health?.latestReadingAtUtc ? Date.parse(health.latestReadingAtUtc) : NaN;
  const windowFrom = health?.windowFromUtc ? Date.parse(health.windowFromUtc) : NaN;
  if (!Number.isFinite(latest) || !Number.isFinite(windowFrom)) return null;
  return {
    stale: latest < windowFrom,
    thresholdHours: Math.max(1, Math.round((now - windowFrom) / 3600000)),
    latestReadingAtUtc: health.latestReadingAtUtc,
  };
}

/** The risk label always says what the category is — a descriptive indicator, never a probability. */
function riskLabel(risk) {
  if (!risk) return 'Risk indicator — descriptive category, not a failure probability';
  if (risk.basis === 'DescriptiveIndicator') return 'Risk indicator — descriptive indicator, not a failure probability';
  return `Risk indicator (basis: ${risk.basis}) — descriptive category, not a failure probability`;
}

function lastTimestamp(points) {
  for (let i = points.length - 1; i >= 0; i--) {
    if (points[i]?.timestampUtc) return points[i].timestampUtc;
  }
  return null;
}

/**
 * Historical telemetry for one aircraft: three separately scaled charts, the aircraft's latest
 * reading time, the descriptive risk indicator and the forecast-unavailable notice.
 *
 * The series is fetched with `keepData: false`, so a change of aircraft (or of range) clears the
 * previous series immediately and shows the loading skeleton — a chart is never left on screen
 * labelled with an aircraft it does not belong to.
 */
export function FleetHealthTelemetryPanel({ aircraft, fromUtc, toUtc, bucketMinutes = 60, disabled = false }) {
  const baseId = useId();
  const aircraftId = aircraft?.id || '';
  const { data, error, loading, reload } = useAsync(
    () => telemetryApi.series({ aircraftId, fromUtc, toUtc, bucketMinutes }),
    [aircraftId, fromUtc, toUtc, bucketMinutes],
    { enabled: !!aircraftId && !disabled, keepData: false },
  );

  const points = data?.points || [];
  const staleness = stalenessOf(aircraft?.health);
  const latestAt = aircraft?.health?.latestReadingAtUtc || aircraft?.lastTelemetryAtUtc || lastTimestamp(points);
  const risk = aircraft?.latestRisk || null;
  const totalSamples = points.reduce((s, p) => s + (p.count || 0), 0);

  const chart = (c) => (
    <section key={c.field} aria-labelledby={`${baseId}-${c.field}`}>
      <h3 id={`${baseId}-${c.field}`} style={{ fontSize: 'var(--fs-base)', margin: '0 0 8px' }}>
        {c.heading}
      </h3>
      <LineChart
        ariaLabel={c.name}
        unit={c.unit}
        emptyText={c.empty}
        series={[
          {
            name: c.name,
            color: c.color,
            // `?? null` keeps a real 0 and turns a missing reading into a gap, never into a zero.
            data: points.map((p) => ({ x: p.timestampUtc, y: p[c.field] ?? null })),
          },
        ]}
      />
    </section>
  );

  let body;
  if (!aircraftId) {
    body = <EmptyState icon="activity" title="Select an aircraft to plot its telemetry history." />;
  } else if (error && !data) {
    body = <ErrorState error={error} onRetry={reload} title="Could not load telemetry history" />;
  } else if (!data) {
    body = (
      <div className="grid grid--2" role="status" aria-label="Loading telemetry history">
        <span className="visually-hidden">Loading…</span>
        <Skeleton height={240} />
        <Skeleton height={240} />
      </div>
    );
  } else if (points.length === 0) {
    body = <EmptyState icon="activity" title={NO_TELEMETRY_IN_RANGE} message="Try a wider date range or a larger bucket size." />;
  } else {
    body = (
      <>
        <p className="subtle" style={{ marginTop: 0 }}>
          {points.length} buckets of {data.bucketMinutes} min · {totalSamples.toLocaleString()} samples (bucket averages). A bucket with
          no reading is left as a gap, never drawn as zero.
        </p>
        <div className="grid grid--2">{SERIES.slice(0, 2).map(chart)}</div>
        <div style={{ marginTop: 16 }}>{chart(SERIES[2])}</div>
      </>
    );
  }

  return (
    <div className="stack" aria-busy={loading || undefined}>
      <div className="row row--between" style={{ alignItems: 'flex-start', flexWrap: 'wrap', gap: 12 }}>
        <div>
          <strong>{aircraft?.registrationNumber || '—'}</strong>
          <div className="subtle">
            Latest telemetry reading:{' '}
            {latestAt ? (
              <span className="nowrap" title={formatRelative(latestAt)}>
                {formatDateTime(latestAt)} ({formatRelative(latestAt)})
              </span>
            ) : (
              'No reading'
            )}
          </div>
        </div>
        <div>
          <div className="subtle">{riskLabel(risk)}</div>
          <RiskBadge value={risk?.riskCategory} />
        </div>
      </div>

      {staleness?.stale ? (
        <Alert tone="warning" title="Stale telemetry">
          The latest reading is from {formatDateTime(staleness.latestReadingAtUtc)} ({formatRelative(staleness.latestReadingAtUtc)}), older
          than the {staleness.thresholdHours}-hour evaluation window the engine-health rules assess. Readings before that window are shown
          as history only.
        </Alert>
      ) : null}

      <p className="subtle" style={{ margin: 0 }} data-testid="recorded-history-note">
        Recorded readings. The 30-day forecast is on the Health Dashboard.
      </p>

      {body}
    </div>
  );
}

/**
 * The Fleet Health telemetry graph: an aircraft picker and a UTC date range over the panel above.
 * Aircraft come from the shared lookup, so no tail number is ever hard-coded here.
 */
export function FleetHealthGraph({ title = 'Telemetry history', id = 'fleet-health-graph' }) {
  const { data: options, error: optionsError, reload: reloadOptions } = useAircraftOptions();
  const [selectedId, setSelectedId] = useState('');
  const [range, setRange] = useState(() => ({ from: toInputDate(daysAgo(7)), to: toInputDate(new Date()), bucket: '60' }));

  // A selection that is no longer in the list falls back to the first aircraft, as on the explorer.
  const known = !!options && options.some((o) => o.value === selectedId);
  const aircraftId = known ? selectedId : options?.length ? options[0].value : '';
  const selected = options?.find((o) => o.value === aircraftId)?.aircraft || null;

  const fromUtc = fromInputDate(range.from);
  const toUtc = fromInputDate(range.to, true);
  const rangeError = rangeErrorFor(fromUtc, toUtc);

  return (
    <Card title={title} id={id} flush>
      <div className="filter-bar" role="search">
        <AircraftSelect value={aircraftId} onChange={setSelectedId} />
        <TextField label="From (UTC)" type="date" value={range.from} onChange={(e) => setRange((r) => ({ ...r, from: e.target.value }))} />
        <TextField label="To (UTC)" type="date" value={range.to} onChange={(e) => setRange((r) => ({ ...r, to: e.target.value }))} />
        <SelectField
          label="Bucket size"
          value={range.bucket}
          onChange={(e) => setRange((r) => ({ ...r, bucket: e.target.value }))}
          options={BUCKET_OPTIONS}
        />
      </div>
      <div className="card__body">
        {rangeError ? <Alert tone="danger">{rangeError}</Alert> : null}
        {optionsError && !options ? (
          <ErrorState error={optionsError} onRetry={reloadOptions} title="Could not load aircraft" />
        ) : !options ? (
          <div role="status" aria-label="Loading aircraft">
            <span className="visually-hidden">Loading…</span>
            <Skeleton height={240} />
          </div>
        ) : options.length === 0 ? (
          <EmptyState icon="activity" title="No aircraft available" message="No aircraft were returned, so there is no telemetry history to plot." />
        ) : (
          <FleetHealthTelemetryPanel
            aircraft={selected}
            fromUtc={fromUtc}
            toUtc={toUtc}
            bucketMinutes={Number(range.bucket) || 60}
            disabled={!!rangeError}
          />
        )}
      </div>
    </Card>
  );
}
