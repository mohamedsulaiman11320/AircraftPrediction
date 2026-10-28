// Retained for the backend CSV import path, which the Data Engineer UI no longer exposes.
export const MAX_IMPORT_BYTES = 200 * 1024 * 1024;
export const REQUIRED_COLUMNS = ['aircraft_registration', 'timestamp_utc', 'temperature', 'temperature_unit', 'pressure', 'pressure_unit'];
export const OPTIONAL_COLUMNS = ['sensor_id', 'data_source'];

/** Split one CSV header line, handling simple double-quoted fields. */
export function parseHeaderLine(line) {
  const out = [];
  let cur = '';
  let quoted = false;
  for (let i = 0; i < line.length; i++) {
    const ch = line[i];
    if (ch === '"') {
      if (quoted && line[i + 1] === '"') {
        cur += '"';
        i++;
      } else quoted = !quoted;
    } else if (ch === ',' && !quoted) {
      out.push(cur);
      cur = '';
    } else cur += ch;
  }
  out.push(cur);
  return out.map((c) => c.trim().toLowerCase());
}

/** Validate a header line; returns { ok, missing, columns }. */
export function checkHeader(firstLine) {
  const clean = (firstLine || '').replace(/^﻿/, '').replace(/\r$/, '');
  const columns = clean ? parseHeaderLine(clean) : [];
  const missing = REQUIRED_COLUMNS.filter((c) => !columns.includes(c));
  return { ok: missing.length === 0, missing, columns };
}

function readFirstLine(file) {
  const slice = file.slice(0, 64 * 1024);
  if (typeof slice.text === 'function') return slice.text().then((t) => t.split('\n')[0]);
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result).split('\n')[0]);
    reader.onerror = () => reject(reader.error);
    reader.readAsText(slice);
  });
}

/** Client-side pre-checks before upload (server re-validates everything). Returns list of error strings. */
export async function precheckCsvFile(file) {
  const errors = [];
  if (!file) return ['Choose a CSV file to upload.'];
  if (!/\.csv$/i.test(file.name)) errors.push('The file must have a .csv extension.');
  if (file.size === 0) errors.push('The file is empty.');
  if (file.size > MAX_IMPORT_BYTES) errors.push('The file exceeds the 200 MB limit.');
  if (errors.length) return errors;
  try {
    const first = await readFirstLine(file);
    const { ok, missing } = checkHeader(first);
    if (!ok) errors.push(`The header row is missing required column(s): ${missing.join(', ')}.`);
  } catch {
    errors.push('The file could not be read.');
  }
  return errors;
}
