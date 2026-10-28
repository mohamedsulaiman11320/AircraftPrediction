export const MAX_RAW_JSON_BYTES = 50 * 1024 * 1024;

function readText(file) {
  if (typeof file.text === 'function') return file.text();
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result));
    reader.onerror = () => reject(reader.error);
    reader.readAsText(file);
  });
}

/**
 * Client-side pre-checks for a raw JSON telemetry file (the server and the ETL re-validate
 * everything): .json extension, not empty, at most 50 MB, parses as JSON, root is an array.
 * Returns a list of error strings.
 */
export async function precheckRawJsonFile(file) {
  if (!file) return ['Choose a JSON file to upload.'];
  const errors = [];
  if (!/\.json$/i.test(file.name)) errors.push('The file must have a .json extension.');
  if (file.size === 0) errors.push('The file is empty.');
  if (file.size > MAX_RAW_JSON_BYTES) errors.push('The file exceeds the 50 MB limit.');
  if (errors.length) return errors;
  let text;
  try {
    text = await readText(file);
  } catch {
    return ['The file could not be read.'];
  }
  let parsed;
  try {
    parsed = JSON.parse(text.replace(/^﻿/, ''));
  } catch {
    return ['The file is not valid JSON.'];
  }
  if (!Array.isArray(parsed)) errors.push('The JSON root must be an array of telemetry records.');
  return errors;
}
