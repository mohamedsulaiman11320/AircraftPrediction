/**
 * Shared date-range rules for the telemetry endpoints.
 *
 * `GET /telemetry` and `GET /telemetry/series` accept at most 120 days per request and clamp
 * `bucketMinutes` to 1..1440, so the pickers never offer a bucket the API would silently change.
 */
export const MAX_RANGE_DAYS = 120;

export const BUCKET_OPTIONS = [
  { value: '5', label: '5 min' },
  { value: '15', label: '15 min' },
  { value: '60', label: '1 hour' },
  { value: '360', label: '6 hours' },
  { value: '1440', label: '1 day' },
];

/** The message for an unusable range, or null when the range can be requested. */
export function rangeErrorFor(fromUtc, toUtc) {
  if (!fromUtc || !toUtc) return null;
  const days = (Date.parse(toUtc) - Date.parse(fromUtc)) / 86400000;
  if (!Number.isFinite(days)) return null;
  if (days < 0) return 'The start date must be before the end date.';
  if (days > MAX_RANGE_DAYS) return `The range may not exceed ${MAX_RANGE_DAYS} days.`;
  return null;
}
