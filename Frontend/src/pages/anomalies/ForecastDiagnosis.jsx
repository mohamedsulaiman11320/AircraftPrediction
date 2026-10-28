import { formatDateTime, formatNumber, humanize } from '../../utils/format.js';

const MEASUREMENT_NAMES = { EngineTemperature: 'Engine temperature', OilPressure: 'Oil pressure' };
const UNIT_LABELS = { C: '°C', psi: 'PSI' };
const DIRECTION = { GreaterThan: 'high', LessThan: 'low' };

const unit = (u) => UNIT_LABELS[u] || u || '';
const value = (v, u) => `${formatNumber(v, 1)} ${unit(u)}`.trim();

/**
 * Plain-English view of a forecast anomaly's evidence: each projected breach against its limit, the forecast window and
 * the recommended action. Model identifiers sit in a closed "Technical details" section.
 */
export function ForecastDiagnosis({ evidence }) {
  if (!evidence) return <p className="muted">No forecast evidence was recorded.</p>;
  const breaches = Array.isArray(evidence.breaches) ? evidence.breaches : [];
  return (
    <div className="stack" data-testid="forecast-diagnosis">
      <ul className="stack" style={{ listStyle: 'none', margin: 0, padding: 0 }}>
        {breaches.map((b) => (
          <li key={b.code} className="forecast-breach" data-testid="forecast-breach-row">
            <dl className="dl">
              <div>
                <dt>Projected breach</dt>
                <dd>
                  <strong>
                    {MEASUREMENT_NAMES[b.measurement] || humanize(b.measurement)} {DIRECTION[b.comparison] || ''}
                  </strong>{' '}
                  ({value(b.extreme, b.unit)} vs {value(b.limit, b.unit)} limit)
                </dd>
              </div>
              <div>
                <dt>Forecast window</dt>
                <dd>
                  {formatDateTime(b.firstUtc)} — {formatDateTime(b.lastUtc)}
                </dd>
              </div>
              {b.recommendedInspection ? (
                <div>
                  <dt>Recommended action</dt>
                  <dd>{b.recommendedInspection}</dd>
                </div>
              ) : null}
            </dl>
          </li>
        ))}
      </ul>
      <details data-testid="technical-details">
        <summary>Technical details</summary>
        <dl className="dl">
          {evidence.horizon ? (
            <div>
              <dt>Forecast horizon</dt>
              <dd>{humanize(evidence.horizon)}</dd>
            </div>
          ) : null}
          {evidence.dataAsOfUtc ? (
            <div>
              <dt>From readings up to</dt>
              <dd>{formatDateTime(evidence.dataAsOfUtc)}</dd>
            </div>
          ) : null}
          {evidence.forecastModelVersion ? (
            <div>
              <dt>Forecast model version</dt>
              <dd className="mono">{evidence.forecastModelVersion}</dd>
            </div>
          ) : null}
          {evidence.forecastModelVersionId ? (
            <div>
              <dt>Forecast model id</dt>
              <dd className="mono">{evidence.forecastModelVersionId}</dd>
            </div>
          ) : null}
          {breaches.map((b) => (
            <div key={b.code}>
              <dt>Forecast points beyond the limit</dt>
              <dd>
                {MEASUREMENT_NAMES[b.measurement] || humanize(b.measurement)}: {b.points}
              </dd>
            </div>
          ))}
        </dl>
      </details>
    </div>
  );
}
