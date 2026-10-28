/**
 * Pure helpers for the Health Dashboard. Everything here derives from API payloads
 * (GET /dashboards/dispatcher, /telemetry/series, /anomalies) - no counts, limits or aircraft are defined here.
 */
import { WO_STATUS_LABELS } from '../../../utils/enums.js';

/** Telemetry series fields, keyed by the health_threshold `measurement` that may limit them. */
export const METRICS = [
  // Series colours are fixed: temperature red, pressure green, vibration blue (validated with the dataviz checker:
  // all pairs pass, and each series also has its own labelled panel and legend entry).
  { key: 'temperatureC', measurement: 'EngineTemperature', name: 'Engine temperature', short: 'Temperature', unit: '°C', color: '#dc2626' },
  { key: 'pressurePsi', measurement: 'OilPressure', name: 'Oil pressure', short: 'Oil pressure', unit: 'PSI', color: '#15803d' },
  { key: 'vibrationIndex', measurement: 'Vibration', name: 'Vibration', short: 'Vibration', unit: 'index', color: '#2563eb' },
];

/** Name of the measurement a limit applies to ("Oil pressure"), or the limit's own description. */
export function limitName(limit) {
  return METRICS.find((m) => m.measurement === limit?.measurement)?.name || limit?.description || limit?.code || 'Limit';
}

/** Plain-language name of a breached limit, from the limit itself (never from a rule id). */
export function breachLabel(limit, { short = false } = {}) {
  const metric = METRICS.find((m) => m.measurement === limit?.measurement);
  if (!metric) return limit?.description || limit?.code || 'Limit breached';
  return `${short ? metric.short : metric.name} ${limit.comparison === 'LessThan' ? 'low' : 'high'}`;
}

/** Limit text such as "below 35 PSI" / "above 700 °C". */
export function limitText(limit) {
  const metric = METRICS.find((m) => m.measurement === limit.measurement);
  return `${limit.comparison === 'LessThan' ? 'below' : 'above'} ${limit.value} ${metric?.unit ?? limit.unit ?? ''}`.trim();
}

export function isBreach(value, limit) {
  if (typeof value !== 'number' || !Number.isFinite(value) || !limit) return false;
  if (limit.comparison === 'LessThan') return value < limit.value;
  if (limit.comparison === 'GreaterThan') return value > limit.value;
  return false;
}

/** Approved (validated) limit for a series field, or null when none is approved. */
export function limitFor(metricKey, thresholds = []) {
  const metric = METRICS.find((m) => m.key === metricKey);
  return thresholds.find((t) => t.measurement === metric?.measurement && t.isValidated) || null;
}

/** Card A: threshold health of the latest 24 h window - status partition of the assessed fleet. */
export function thresholdSegments(health) {
  if (!health) return [];
  return [
    { key: 'breaching', label: 'Breaching a limit', value: health.highRisk ?? 0, tone: 'crit', icon: 'alert' },
    { key: 'within', label: 'Within limits', value: health.withinLimits ?? 0, tone: 'ok', icon: 'check' },
    { key: 'noData', label: 'No recent data', value: health.notAssessable ?? 0, tone: 'idle', icon: 'minus' },
  ];
}

/** Card C: work orders in lifecycle order (Cancelled is not part of the active lifecycle and is listed apart). */
export const WORK_ORDER_STAGES = [
  { key: 'open', status: 'Open', tone: 'wo-1' },
  { key: 'assigned', status: 'Assigned', tone: 'wo-2' },
  { key: 'inInspection', status: 'InInspection', tone: 'wo-3' },
  { key: 'resolved', status: 'Resolved', tone: 'wo-4' },
  { key: 'closed', status: 'Closed', tone: 'wo-5' },
];

/** "IN_PROGRESS" -> "In progress": the same labels as the work-order screens, written for reading. */
export function stageLabel(status) {
  const raw = WO_STATUS_LABELS[status] || status;
  const words = raw.replace(/_/g, ' ').toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
}

export function workOrderSegments(counts) {
  if (!counts) return [];
  return WORK_ORDER_STAGES.map((s) => ({ ...s, label: stageLabel(s.status), value: counts[s.key] ?? 0 }));
}

export function percent(value, total) {
  return total > 0 ? Math.round((value / total) * 100) : 0;
}

/**
 * One card per aircraft in the response, joining its risk row and its readiness row by id (either may be absent on
 * an older payload), ordered by tail number.
 */
export function aircraftCards(dashboard) {
  const readiness = new Map((dashboard?.readiness || []).map((r) => [r.aircraftId, r]));
  const risk = new Map((dashboard?.aircraftRisk || []).map((r) => [r.aircraftId, r]));
  const ids = [...new Set([...risk.keys(), ...readiness.keys()])];
  return ids.map((id) => {
    const ready = readiness.get(id) || {};
    const r = risk.get(id) || { aircraftId: id, registrationNumber: ready.registrationNumber, fleetGroup: ready.fleetGroup,
      latestReadingAtUtc: ready.lastTelemetryAtUtc };
    return {
      id,
      registration: r.registrationNumber,
      fleetGroup: r.fleetGroup,
      model: ready.model,
      state: ready.derivedState || ready.operationalStatus || null,
      isHighRisk: r.isHighRisk,
      isStale: r.isStale,
      latestReadingAtUtc: r.latestReadingAtUtc,
      breachCodes: r.breachCodes || [],
      predictionStatus: r.predictionStatus,
      provisionalScore: r.provisionalScore,
      failureProbability: r.failureProbability,
      riskTier: r.riskTier,
      modelVersion: r.modelVersion,
      featureCutoffUtc: r.featureCutoffUtc,
      predictionAtUtc: r.predictionAtUtc,
      openWorkOrders: r.openWorkOrders,
      forecastCondition: r.forecastCondition || null,
      forecastAnomalyId: r.forecastAnomalyId ?? null,
    };
  }).sort((a, b) => String(a.registration).localeCompare(String(b.registration)));
}

/** Case-insensitive search over the identifying fields the API returns (tail number, fleet group, model). */
export function filterAircraft(cards, query) {
  const q = (query || '').trim().toLowerCase();
  if (!q) return cards;
  return cards.filter((c) => [c.registration, c.fleetGroup, c.model].some((v) => v && String(v).toLowerCase().includes(q)));
}

/** Breach points of one series against its approved limit. */
export function breachPoints(points, metricKey, limit) {
  if (!limit) return [];
  return points.filter((p) => isBreach(p[metricKey], limit)).map((p) => ({ t: p.timestampUtc, value: p[metricKey] }));
}

/**
 * The persisted threshold anomaly that covers a breach, or null. A ThresholdRule incident covers its evaluation
 * window (windowFromUtc..windowToUtc) and, while it stays open, every later breach up to lastObservedAtUtc; it must
 * also name the breached limit in its rule codes. Nothing is inferred beyond that: no match means no anomaly.
 */
export function anomalyForBreach(anomalies, { timestampUtc, limitCode }) {
  const t = Date.parse(timestampUtc);
  if (!Number.isFinite(t)) return null;
  const matches = (anomalies || []).filter((a) => {
    if (a.source !== 'ThresholdRule') return false;
    const codes = String(a.ruleCodes || '').split(',').map((c) => c.trim());
    if (!codes.includes(limitCode)) return false;
    const fv = a.featureValues || {};
    const from = Date.parse(fv.windowFromUtc);
    const ends = [fv.windowToUtc, a.lastObservedAtUtc, a.observedAtUtc].map((v) => Date.parse(v)).filter(Number.isFinite);
    if (!Number.isFinite(from) || !ends.length) return false;
    return t >= from && t <= Math.max(...ends);
  });
  // Several incidents can never overlap for one aircraft and rule, but prefer the most recent if they did.
  return matches.sort((a, b) => Date.parse(b.observedAtUtc) - Date.parse(a.observedAtUtc))[0] || null;
}

/** Short status line for an aircraft card. */
export function thresholdSummary(card, thresholds = []) {
  if (card.isHighRisk === null || card.isHighRisk === undefined) return { tone: 'idle', text: 'No recent data' };
  if (!card.isHighRisk) return { tone: 'ok', text: 'Within limits' };
  const names = card.breachCodes.map((code) => breachLabel(thresholds.find((t) => t.code === code) || { code }, { short: true }));
  return { tone: 'crit', text: names.length ? names.join(' · ') : 'Limit breached' };
}

export function predictionSummary(card) {
  if (card.predictionStatus === 'Validated' && typeof card.failureProbability === 'number') return 'Validated prediction';
  if (card.predictionStatus === 'NotValidated' && typeof card.provisionalScore === 'number') return 'Provisional score';
  return 'No prediction';
}

/** Forecast condition badge text (from the backend's open forecast incident). */
export const FORECAST_CONDITION_LABELS = { Critical: 'Critical', HighCritical: 'High Critical' };

/**
 * Anomaly behind a FORECAST breach point: the aircraft's ForecastModel incident whose forecast window
 * (observedAtUtc = first forecast breach .. lastObservedAtUtc) covers the point and whose rule codes name the limit.
 */
export function forecastAnomalyForBreach(anomalies, { timestampUtc, limitCode }) {
  const t = Date.parse(timestampUtc);
  if (!Number.isFinite(t)) return null;
  const matches = (anomalies || []).filter((a) => {
    if (a.source !== 'ForecastModel') return false;
    if (!String(a.ruleCodes || '').split(',').map((c) => c.trim()).includes(limitCode)) return false;
    const from = Date.parse(a.observedAtUtc);
    const to = Date.parse(a.lastObservedAtUtc || a.observedAtUtc);
    return Number.isFinite(from) && t >= from && t <= to;
  });
  return matches.sort((a, b) => Date.parse(b.observedAtUtc) - Date.parse(a.observedAtUtc))[0] || null;
}
