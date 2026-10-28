/** Formatting helpers. All API timestamps are UTC; we display in the user's locale with a UTC hint. */
const dtf = new Intl.DateTimeFormat(undefined, {
  year: 'numeric',
  month: 'short',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  timeZone: 'UTC',
});
const df = new Intl.DateTimeFormat(undefined, { year: 'numeric', month: 'short', day: '2-digit', timeZone: 'UTC' });

export function formatDateTime(value) {
  if (!value) return '—';
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return '—';
  return `${dtf.format(d)} UTC`;
}

export function formatDate(value) {
  if (!value) return '—';
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return '—';
  return df.format(d);
}

export function formatRelative(value) {
  if (!value) return '—';
  const d = new Date(value).getTime();
  if (Number.isNaN(d)) return '—';
  const diff = Date.now() - d;
  const abs = Math.abs(diff);
  const mins = Math.round(abs / 60000);
  const suffix = diff >= 0 ? 'ago' : 'from now';
  if (mins < 1) return 'just now';
  if (mins < 60) return `${mins} min ${suffix}`;
  const hours = Math.round(mins / 60);
  if (hours < 48) return `${hours} h ${suffix}`;
  const days = Math.round(hours / 24);
  return `${days} d ${suffix}`;
}

export function formatNumber(value, digits = 0) {
  if (value === null || value === undefined || Number.isNaN(Number(value))) return '—';
  return Number(value).toLocaleString(undefined, { minimumFractionDigits: digits, maximumFractionDigits: digits });
}

export function formatPercent(value, digits = 1) {
  if (value === null || value === undefined || Number.isNaN(Number(value))) return '—';
  return `${Number(value).toFixed(digits)}%`;
}

/** Split PascalCase enum names into words: UnderMaintenance -> Under maintenance */
export function humanize(value) {
  if (value === null || value === undefined || value === '') return '—';
  const s = String(value)
    .replace(/_/g, ' ')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/([A-Z])([A-Z][a-z])/g, '$1 $2');
  return s.charAt(0).toUpperCase() + s.slice(1).toLowerCase();
}

export function formatBytes(bytes) {
  if (!bytes && bytes !== 0) return '—';
  const units = ['B', 'KB', 'MB', 'GB'];
  let i = 0;
  let v = bytes;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v.toFixed(i === 0 ? 0 : 1)} ${units[i]}`;
}

/** Value for <input type="datetime-local"> from an ISO string, interpreted in UTC. */
export function toInputDateTime(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toISOString().slice(0, 16);
}

/** ISO UTC string from a datetime-local value (treated as UTC). */
export function fromInputDateTime(value) {
  if (!value) return undefined;
  const d = new Date(value.length === 16 ? `${value}:00Z` : value);
  return Number.isNaN(d.getTime()) ? undefined : d.toISOString();
}

/** ISO UTC string from a date input (yyyy-mm-dd); endOfDay moves to 23:59:59Z. */
export function fromInputDate(value, endOfDay = false) {
  if (!value) return undefined;
  const d = new Date(`${value}T${endOfDay ? '23:59:59' : '00:00:00'}Z`);
  return Number.isNaN(d.getTime()) ? undefined : d.toISOString();
}

export function toInputDate(date) {
  if (!date) return '';
  const d = date instanceof Date ? date : new Date(date);
  return Number.isNaN(d.getTime()) ? '' : d.toISOString().slice(0, 10);
}

export function daysAgo(n) {
  return new Date(Date.now() - n * 86400000);
}

/** Up to two initials from the words that start with a letter ("Fiona (synthetic) Dispatch" -> "FD"). */
export function initials(name = '') {
  const parts = String(name).trim().split(/\s+/).filter((w) => /^\p{L}/u.test(w));
  if (parts.length === 0) return '?';
  return (parts[0][0] + (parts.length > 1 ? parts[parts.length - 1][0] : '')).toUpperCase();
}

/** Manufacturer and model, or null when neither is known (never "Unknown Unknown"). */
export function aircraftModelText(a) {
  const known = [a?.manufacturer, a?.model].filter((v) => v && v !== 'Unknown');
  return known.length ? known.join(' ') : null;
}

/** "N120UA · A320neo", or just the tail number when the model is unknown. */
export function aircraftLabel(a) {
  const model = a?.model && a.model !== 'Unknown' ? a.model : null;
  return model ? `${a.registrationNumber} · ${model}` : a.registrationNumber;
}
