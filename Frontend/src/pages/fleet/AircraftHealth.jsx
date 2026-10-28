import { Card } from '../../components/PageHeader.jsx';
import { KeyValueList, Disclaimer } from '../../components/Misc.jsx';
import { StatusBadge, TierBadge } from '../../components/StatusBadge.jsx';
import { formatDateTime, formatNumber, formatRelative } from '../../utils/format.js';

/**
 * The nullable `health` block returned on every aircraft. Nothing here is ever defaulted or
 * invented: a missing reading reads "No reading", an unassessable window says so, and the
 * threshold-based status (client-defined rules) is kept visually separate from the model-based
 * failure risk so a breach is never read as a prediction.
 */
export const NO_READING = 'No reading';
export const NO_ASSESSMENT = 'No valid assessment';

export const THRESHOLD_DISCLAIMER =
  'Threshold status uses client-defined values recorded in the BRD and is awaiting stakeholder validation. These are not manufacturer limits and not regulatory guidance.';

/** A measurement cell: an explicit "No reading" instead of a defaulted number. */
export function HealthMeasure({ value, digits = 1 }) {
  if (value === null || value === undefined) return <span className="muted">{NO_READING}</span>;
  return <span className="nowrap">{formatNumber(value, digits)}</span>;
}

export function HealthReadingTime({ health }) {
  const at = health?.latestReadingAtUtc;
  if (!at) return <span className="muted">{NO_READING}</span>;
  return (
    <>
      <span className="nowrap" title={formatDateTime(at)}>
        {formatRelative(at)}
      </span>
      {/* isStale comes from the seeded TelemetryStaleAfterHours rule; null = unknown, never shown as fresh. */}
      {health.isStale ? (
        <div>
          <StatusBadge kind="risk" value="Elevated" label="Stale data" title={`Older than ${health.staleAfterHours} h`} />
        </div>
      ) : null}
    </>
  );
}

/** Threshold-based status. Never reports "within limits" for a window without readings. */
export function ThresholdStatus({ health }) {
  if (!health || health.isAssessable === false) return <StatusBadge value="NotAssessable" prefix="Threshold status" />;
  if (health.isHighRisk === true) {
    // The limits breached in plain terms ("Temp > 700 °C · Oil < 35 PSI"); internal rule references are never shown.
    const limits = [...new Set((health.breaches || []).map((b) => b.limit).filter(Boolean))];
    return (
      <>
        <StatusBadge value="ThresholdBreach" prefix="Threshold status" />
        {limits.length ? <div className="subtle">{limits.join(' · ')}</div> : null}
      </>
    );
  }
  return <StatusBadge value="WithinThresholds" prefix="Threshold status" />;
}

/** Model-based 30-day failure risk. A missing score is stated, never shown as 0 or a tier. */
export function FailureRisk({ health }) {
  if (!health || health.failureRiskScore === null || health.failureRiskScore === undefined) {
    return <span className="muted">{NO_ASSESSMENT}</span>;
  }
  return (
    <>
      <TierBadge value={health.failureRiskTier} />
      <div className="subtle">Score {formatNumber(health.failureRiskScore, 3)}</div>
    </>
  );
}

/** True when any of the supplied health blocks was scored against unvalidated thresholds. */
export function hasUnvalidatedThresholds(healthList) {
  return (healthList || []).some((h) => h && h.thresholdsValidated === false);
}

export function thresholdSources(healthList) {
  return Array.from(new Set((healthList || []).map((h) => h?.thresholdSource).filter(Boolean)));
}

/** The shared unvalidated-threshold disclaimer, rendered only when some row needs it. */
export function ThresholdDisclaimer({ healthList }) {
  if (!hasUnvalidatedThresholds(healthList)) return null;
  const sources = thresholdSources(healthList);
  return (
    <Disclaimer>
      {THRESHOLD_DISCLAIMER}
      {sources.length ? ` Threshold source: ${sources.join(', ')}.` : ''}
    </Disclaimer>
  );
}

/** Aircraft-detail card for the same `health` block. */
export function AircraftHealthCard({ health }) {
  if (!health) {
    return (
      <Card title="Aircraft health">
        <p className="muted">No health data recorded for this aircraft.</p>
      </Card>
    );
  }
  const values = {
    'Engine temperature (°C)': <HealthMeasure value={health?.engineTemperatureC} />,
    'Oil pressure (PSI)': <HealthMeasure value={health?.oilPressurePsi} />,
    'Vibration index': <HealthMeasure value={health?.vibrationIndex} digits={3} />,
    'Latest reading': <HealthReadingTime health={health} />,
    'Window start': health?.windowFromUtc ? formatDateTime(health.windowFromUtc) : <span className="muted">{NO_READING}</span>,
    'Readings in window': formatNumber(health?.readingsInWindow ?? null),
    'Threshold status': <ThresholdStatus health={health} />,
    '30-day failure risk (model)': <FailureRisk health={health} />,
  };
  return (
    <Card title="Aircraft health">
      <KeyValueList data={values} format={(v) => v} emptyText="No health data recorded for this aircraft" />
      <div className="stack">
        <h3>Threshold breaches</h3>
        {health?.breaches?.length ? (
          <ul className="simple-list">
            {health.breaches.map((b) => (
              <li key={b.code}>
                <div>
                  <div className="row">
                    <StatusBadge value="ThresholdBreach" />
                    {b.limit ? <strong>{b.limit}</strong> : null}
                  </div>
                  <div className="subtle">{b.description}</div>
                  {b.recommendedInspection ? <div className="subtle">Recommended inspection: {b.recommendedInspection}</div> : null}
                </div>
              </li>
            ))}
          </ul>
        ) : (
          <p className="muted">{health && health.isAssessable === false ? 'Not assessable - no reading in window' : 'No threshold breaches recorded.'}</p>
        )}
        {health?.recommendedInspection ? <p>Recommended inspection: {health.recommendedInspection}</p> : null}
      </div>
      <ThresholdDisclaimer healthList={[health]} />
    </Card>
  );
}
