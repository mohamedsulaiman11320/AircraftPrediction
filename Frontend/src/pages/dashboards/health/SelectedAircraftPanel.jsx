import { STATUS_LABELS } from '../../../components/StatusBadge.jsx';
import { FORECAST_CONDITION_LABELS } from './healthModel.js';
import { ForecastSection } from './ForecastSection.jsx';

/**
 * The failure model's aircraft-level score for the 30-day horizon - shown apart from the sensor forecast. A validated
 * probability is shown as a percentage; an uncalibrated model score as a 0-100 risk score (never a percentage or tier).
 */
export function FailureRiskSummary({ card, horizonDays }) {
  const validated = card.predictionStatus === 'Validated' && typeof card.failureProbability === 'number';
  const provisional = !validated && card.predictionStatus === 'NotValidated' && typeof card.provisionalScore === 'number';
  const pct = (v) => Math.round(v * 1000) / 10;
  return (
    <div className="flex items-center gap-2 rounded-lg border border-line bg-canvas px-3 py-1" data-testid="risk-summary">
      <span className="text-[12px] text-ink-muted">{horizonDays}-day risk score</span>
      {validated ? (
        <span className="text-[14px] font-semibold tabular-nums text-ink">{pct(card.failureProbability)}%</span>
      ) : provisional ? (
        <span className="text-[14px] font-semibold tabular-nums text-ink" data-testid="risk-score" title="Relative model score for comparing aircraft">
          {Math.round(card.provisionalScore * 100)}
          <span className="text-[11px] font-normal text-ink-muted"> / 100</span>
        </span>
      ) : (
        <span className="text-[13px] text-ink-muted">No model assessment</span>
      )}
    </div>
  );
}

/** Selected aircraft: its condition and the 30-day forecast (chart / table). Recorded telemetry lives on Fleet Health. */
export function SelectedAircraftPanel({ card, thresholds, horizonDays, horizon, onHorizon }) {
  return (
    <section className="flex min-h-0 min-w-0 flex-1 flex-col gap-2 rounded-xl border border-line bg-white p-3" aria-label={`${card.registration} forecast`} data-testid="selected-aircraft">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex min-w-0 flex-wrap items-center gap-2">
          <h2 className="m-0 text-[15px] font-semibold text-ink">{card.registration}</h2>
          {card.state ? <span className="text-[12px] text-ink-muted">{STATUS_LABELS[card.state] || card.state}</span> : null}
          {card.forecastCondition ? (
            <span className="rounded bg-crit px-1.5 py-0.5 text-[11px] font-semibold text-white" data-testid="selected-forecast-condition">
              {FORECAST_CONDITION_LABELS[card.forecastCondition] || card.forecastCondition}
            </span>
          ) : null}
        </div>
        <FailureRiskSummary card={card} horizonDays={horizonDays} />
      </div>
      <ForecastSection card={card} thresholds={thresholds} horizon={horizon} onHorizon={onHorizon} />
    </section>
  );
}
