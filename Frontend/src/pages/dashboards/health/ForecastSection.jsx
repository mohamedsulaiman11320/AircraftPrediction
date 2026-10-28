import { useState } from 'react';
import { forecastApi } from '../../../api/forecast.js';
import { anomaliesApi } from '../../../api/anomalies.js';
import { useNavigate } from '../../../router/Router.jsx';
import { useAsync } from '../../../hooks/useAsync.js';
import { Icon } from '../../../components/Icon.jsx';
import { Skeleton } from '../../../components/Skeleton.jsx';
import { formatDateTime } from '../../../utils/format.js';
import { SENSOR_FORECAST_HORIZON, SENSOR_FORECAST_HORIZON_LABELS } from '../../../utils/enums.js';
import { formatTick } from '../../../components/charts/scale.js';
import { METRICS, forecastAnomalyForBreach, isBreach, limitFor, limitText } from './healthModel.js';
import { MultiSeriesChart, SeriesLegend, useVisibleSeries } from './MultiSeriesChart.jsx';

const VIEWS = ['Chart View', 'Table View'];

function Segmented({ label, options, value, onChange, isDisabled = () => false, testId, describe }) {
  return (
    <div role="group" aria-label={label} className="flex rounded-lg border border-line p-0.5" data-testid={testId}>
      {options.map((o) => {
        const disabled = isDisabled(o.value);
        return (
          <button
            key={o.value}
            type="button"
            aria-pressed={value === o.value}
            disabled={disabled}
            title={describe?.(o.value)}
            onClick={() => onChange(o.value)}
            className={[
              'rounded-md border-0 px-2.5 py-0.5 text-[12px] font-medium focus-visible:outline-2 focus-visible:outline-brand',
              value === o.value ? 'bg-brand text-white' : 'bg-transparent text-ink hover:text-brand',
              disabled ? 'cursor-not-allowed opacity-45 hover:text-ink' : '',
            ].join(' ')}
          >
            {o.label}
            {disabled ? <span className="sr-only"> (unavailable)</span> : null}
          </button>
        );
      })}
    </div>
  );
}

/** Same dataset as the chart: one row per forecast time; a field the forecast does not supply reads "Unavailable". */
export function ForecastTable({ forecast, thresholds }) {
  const rows = forecast?.points || [];
  if (!rows.length)
    return (
      <p className="m-0 py-6 text-center text-[13px] text-ink-muted" data-testid="forecast-table-empty">
        Not generated - there are no forecast values for this aircraft and horizon.
      </p>
    );
  const supported = new Set(forecast.supportedMetrics || []);
  const cell = (p, m) => {
    const v = p[m.key];
    if (typeof v !== 'number')
      return <span className="text-ink-muted">{supported.size && !supported.has(m.key) ? 'Not validated' : 'Unavailable'}</span>;
    const unit = m.unit === 'index' ? '' : ` ${m.unit}`;
    const lo = p[`${m.key}Low`];
    const hi = p[`${m.key}High`];
    return (
      <>
        {formatTick(v)}
        {unit}
        {typeof lo === 'number' && typeof hi === 'number' ? (
          <span className="ml-1 text-ink-muted">({formatTick(lo)}–{formatTick(hi)})</span>
        ) : null}
      </>
    );
  };
  const source = [forecast.modelName, forecast.modelVersion].filter(Boolean).join(' ') || 'Not supplied';
  return (
    <div className="max-h-64 overflow-auto rounded-lg border border-line" data-testid="forecast-table">
      <table className="w-full border-collapse text-[12px]">
        <caption className="caption-bottom px-2 py-1 text-left text-[11px] text-ink-muted">Forecast values for the selected aircraft and horizon. Values in brackets: the 80% interval from the model's backtest.</caption>
        <thead className="sticky top-0 bg-canvas text-left text-ink-muted">
          <tr>
            <th scope="col" className="px-2 py-1.5 font-medium">Forecast time</th>
            {METRICS.map((m) => (
              <th key={m.key} scope="col" className="px-2 py-1.5 font-medium">
                <span className="mr-1 inline-block h-2 w-2 rounded-full align-middle" style={{ background: m.color }} aria-hidden="true" />
                {m.name} ({m.unit})
              </th>
            ))}
            <th scope="col" className="px-2 py-1.5 font-medium">Threshold status</th>
            <th scope="col" className="px-2 py-1.5 font-medium">Source</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((p) => {
            const breached = METRICS.map((m) => ({ m, limit: limitFor(m.key, thresholds) })).filter(({ m, limit }) => isBreach(p[m.key], limit));
            return (
              <tr key={p.timestampUtc} className="border-t border-line">
                <td className="px-2 py-1 tabular-nums">{formatDateTime(p.timestampUtc)}</td>
                {METRICS.map((m) => (
                  <td key={m.key} className="px-2 py-1 tabular-nums">{cell(p, m)}</td>
                ))}
                <td className="px-2 py-1">
                  {breached.length ? (
                    <span className="font-medium text-crit">
                      Beyond limit: {breached.map(({ m, limit }) => `${m.name} ${limitText(limit)}`).join('; ')}
                    </span>
                  ) : (
                    <span className="text-ok">Within approved limits</span>
                  )}
                </td>
                <td className="px-2 py-1 text-ink-muted">{source}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

/**
 * Future sensor forecast for the selected aircraft - from GET /aircraft/{id}/sensor-forecast only. When the API says
 * NotAvailable, the chart and table stay empty with its reason; recorded telemetry is never shown here instead.
 */
export function ForecastSection({ card, thresholds, horizon, onHorizon }) {
  const [view, setView] = useState(VIEWS[0]);
  const [visible, toggle] = useVisibleSeries();
  const [notice, setNotice] = useState(null);
  const navigate = useNavigate();
  const { data, error, loading, reload } = useAsync(() => forecastApi.sensorForecast(card.id, horizon), [card.id, horizon], { keepData: false });
  const supported = new Set(data?.supportedHorizons || []);
  const available = data?.status === 'Available' && data.points?.length > 0;

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-2" data-testid="forecast-section">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <Segmented
          label="Forecast horizon"
          testId="horizon-filters"
          options={SENSOR_FORECAST_HORIZON.map((h) => ({ value: h, label: SENSOR_FORECAST_HORIZON_LABELS[h] }))}
          value={horizon}
          onChange={onHorizon}
          isDisabled={(h) => !!data && !supported.has(h) && h !== horizon}
          describe={(h) => (data && !supported.has(h) ? 'No forecasting model supports this horizon' : undefined)}
        />
        <Segmented label="View" testId="view-toggle" options={VIEWS.map((v) => ({ value: v, label: v }))} value={view} onChange={setView} />
      </div>
      {data?.generatedAtUtc || data?.dataAsOfUtc ? (
        <p className="m-0 text-[11px] text-ink-muted" data-testid="forecast-freshness">
          {data.generatedAtUtc ? `Generated ${formatDateTime(data.generatedAtUtc)}` : null}
          {data.generatedAtUtc && data.dataAsOfUtc ? ' · ' : null}
          {data.dataAsOfUtc ? `from data up to ${formatDateTime(data.dataAsOfUtc)}` : null}
          {data.slicedFromHorizon ? ` · first ${SENSOR_FORECAST_HORIZON_LABELS[horizon].toLowerCase()} of the ${SENSOR_FORECAST_HORIZON_LABELS[data.slicedFromHorizon].toLowerCase()} forecast` : null}
        </p>
      ) : null}
      {notice ? (
        <p role="status" data-testid="forecast-breach-notice" className="m-0 flex items-start gap-2 rounded-lg border border-line bg-canvas px-3 py-2 text-[12px] text-ink">
          <Icon name="info" size={14} className="mt-0.5 shrink-0 text-ink-muted" />
          <span className="flex-1">
            {notice.state === 'error'
              ? `Could not look up the forecast anomaly: ${notice.message}`
              : notice.state === 'loading'
                ? 'Looking up the forecast anomaly...'
                : `Forecast breach: ${notice.metric.name.toLowerCase()} ${formatTick(notice.value)} ${notice.metric.unit} predicted for ${formatDateTime(notice.timestampUtc)} (${limitText(notice.limit)}). No forecast anomaly has been recorded for this point yet - anomalies are raised when the daily processing runs.`}
          </span>
          <button type="button" onClick={() => setNotice(null)} aria-label="Dismiss" className="border-0 bg-transparent p-0 text-ink-muted hover:text-ink">
            <Icon name="close" size={14} />
          </button>
        </p>
      ) : null}
      <div className="min-h-0 flex-1">
        {error ? (
          <div role="alert" className="flex items-center gap-2 py-6 text-[13px] text-crit" data-testid="forecast-error">
            Could not load the forecast.
            <button type="button" className="border-0 bg-transparent p-0 font-medium text-brand underline" onClick={reload}>
              Retry
            </button>
          </div>
        ) : loading || !data ? (
          <Skeleton height={200} />
        ) : !available ? (
          <div className="flex h-full min-h-[180px] flex-col items-center justify-center gap-1 rounded-lg border border-dashed border-line bg-canvas px-6 text-center" data-testid="forecast-unavailable" role="status">
            <p className="m-0 text-[14px] font-semibold text-ink">
              No sensor forecast available for {SENSOR_FORECAST_HORIZON_LABELS[horizon]}
            </p>
            <p className="m-0 max-w-xl text-[12px] text-ink-muted">{data.reason || 'The forecast API returned no forecast values.'}</p>
            <p className="m-0 text-[12px] text-ink-muted">Recorded readings are under "Recorded history"; they are not a forecast.</p>
          </div>
        ) : view === 'Chart View' ? (
          <MultiSeriesChart
            kind="forecast"
            emptyText={(m) => (data.supportedMetrics && !data.supportedMetrics.includes(m.key) ? `${m.name} is not validated for this horizon - not forecast` : `No ${m.name.toLowerCase()} values`)}
            points={data.points}
            visible={visible}
            thresholds={thresholds}
            registration={card.registration}
            onBreach={async (b) => {
              setNotice({ state: 'loading' });
              try {
                const page = await anomaliesApi.list({ aircraftId: card.id, source: 'ForecastModel', page: 1, pageSize: 100 });
                const match = forecastAnomalyForBreach(page?.items, { timestampUtc: b.timestampUtc, limitCode: b.limit.code });
                if (match) navigate(`/anomalies/${match.id}`);
                else setNotice({ ...b, state: 'none' });
              } catch (e) {
                setNotice({ state: 'error', message: e?.message || 'request failed' });
              }
            }}
          />
        ) : (
          <ForecastTable forecast={data} thresholds={thresholds} />
        )}
      </div>
      {view === 'Chart View' && available ? <SeriesLegend visible={visible} onToggle={toggle} idPrefix="forecast" /> : null}
    </div>
  );
}
