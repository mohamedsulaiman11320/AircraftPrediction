import { API_BASE_URL, API_PREFIX, DEMO_MODE } from './config.js';
import { clearSession, getRefreshToken, getToken, setSession } from '../auth/session.js';
import { navigate } from '../router/history.js';
import { toQueryString } from '../router/matchPath.js';

/** Error thrown for every non-2xx response (and network failures, status 0). */
export class ApiError extends Error {
  constructor({ status = 0, code = null, title = '', detail = '', errors = null, traceId = null, type = null } = {}) {
    super(detail || title || `Request failed (${status})`);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.title = title;
    this.detail = detail;
    this.errors = errors;
    this.traceId = traceId;
    this.type = type;
  }

  /** First message for a field (server keys are camelCase but tolerate PascalCase). */
  fieldError(name) {
    if (!this.errors) return null;
    const key = Object.keys(this.errors).find((k) => k.toLowerCase() === name.toLowerCase());
    const v = key ? this.errors[key] : null;
    return Array.isArray(v) ? v[0] : v || null;
  }
}

const DEFAULT_TITLES = {
  400: 'The request was not valid.',
  401: 'Your session has expired. Please sign in again.',
  403: 'You do not have permission to perform this action.',
  404: 'The requested item was not found.',
  409: 'The item was changed or already exists.',
  423: 'The account is temporarily locked.',
  429: 'Too many requests. Please wait a moment and try again.',
  500: 'The server encountered an error.',
};

/** Convert a fetch Response with a non-2xx status into an ApiError (RFC 9457 ProblemDetails aware). */
export async function parseError(response) {
  let body = null;
  const text = await response.text().catch(() => '');
  if (text) {
    try {
      body = JSON.parse(text);
    } catch {
      body = { detail: text.slice(0, 500) };
    }
  }
  const status = response.status;
  const b = body && typeof body === 'object' ? body : {};
  return new ApiError({
    status,
    code: b.code || null,
    title: b.title || DEFAULT_TITLES[status] || `Request failed (${status})`,
    detail: b.detail || '',
    errors: b.errors || null,
    traceId: b.traceId || null,
    type: b.type || null,
  });
}

let unauthorizedHandler = defaultUnauthorizedHandler;

function defaultUnauthorizedHandler() {
  clearSession();
  const here = window.location.pathname + window.location.search;
  if (!window.location.pathname.startsWith('/login')) {
    navigate('/login' + toQueryString({ returnTo: here, reason: 'expired' }), { replace: true });
  }
}

/** Override what happens on a 401 for an authenticated request (mainly for tests). */
export function setUnauthorizedHandler(fn) {
  unauthorizedHandler = fn || defaultUnauthorizedHandler;
}

export function buildUrl(path, query) {
  const full = path.startsWith('http') ? path : API_BASE_URL + (path.startsWith('/api/') || path.startsWith('/health') ? '' : API_PREFIX) + path;
  return full + toQueryString(query || {});
}

let refreshInFlight = null;

/**
 * Exchange the stored refresh token for a new TokenResponse (rotation). Single-flight: concurrent
 * callers share one POST /auth/refresh. Resolves to the new access token, or null on failure.
 */
export function refreshAccessToken() {
  if (refreshInFlight) return refreshInFlight;
  refreshInFlight = (async () => {
    const refreshToken = getRefreshToken();
    if (!refreshToken) return null;
    try {
      const res = await fetch(buildUrl('/auth/refresh'), {
        method: 'POST',
        headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken }),
      });
      if (!res.ok) return null;
      const tokens = await res.json();
      if (!tokens?.accessToken) return null;
      setSession(tokens);
      return tokens.accessToken;
    } catch {
      return null;
    }
  })().finally(() => {
    refreshInFlight = null;
  });
  return refreshInFlight;
}

let demoModule = null;
async function demo() {
  if (!demoModule) demoModule = await import('../demo/adapter.js');
  return demoModule;
}

/**
 * Core request helper.
 * @param {string} method
 * @param {string} path  path below /api/v1 (e.g. '/aircraft')
 * @param {object} opts  { query, body, formData, signal, auth=true, raw=false }
 */
export async function request(method, path, opts = {}) {
  const { query, body, formData, signal, auth = true, raw = false, headers: extraHeaders } = opts;

  if (DEMO_MODE) {
    const d = await demo();
    return d.demoRequest(method, path, { query, body, formData, raw });
  }

  let payload;
  const baseHeaders = { Accept: raw ? '*/*' : 'application/json', ...extraHeaders };
  if (formData) {
    payload = formData; // browser sets the multipart boundary
  } else if (body !== undefined) {
    baseHeaders['Content-Type'] = 'application/json';
    payload = JSON.stringify(body);
  }
  const url = buildUrl(path, query);

  const send = async (token) => {
    const headers = { ...baseHeaders };
    if (token) headers.Authorization = 'Bearer ' + token;
    try {
      return await fetch(url, { method, headers, body: payload, signal });
    } catch (err) {
      if (err && err.name === 'AbortError') throw err;
      throw new ApiError({
        status: 0,
        code: 'network_error',
        title: 'Unable to reach the server',
        detail: 'Check your connection or that the API is running, then try again.',
      });
    }
  };

  let token = auth ? getToken() : null;
  let refreshed = false;
  // Access token expired but a refresh token is available: refresh before sending.
  if (auth && !token && getRefreshToken()) {
    refreshed = true;
    token = await refreshAccessToken();
  }
  let response = await send(token);

  // At most one silent refresh + retry for an authenticated request that was rejected.
  if (response.status === 401 && auth && (token || getRefreshToken())) {
    const current = getToken();
    let fresh = null;
    if (current && current !== token) fresh = current; // another request already refreshed
    else if (!refreshed) fresh = await refreshAccessToken();
    if (fresh) {
      response = await send(fresh);
    }
    if (response.status === 401) {
      const error = await parseError(response);
      unauthorizedHandler(error);
      throw error;
    }
  }

  if (!response.ok) {
    throw await parseError(response);
  }

  if (raw) return response;
  if (response.status === 204) return null;
  const text = await response.text();
  if (!text) return null;
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

export const api = {
  get: (path, query, opts) => request('GET', path, { ...opts, query }),
  post: (path, body, opts) => request('POST', path, { ...opts, body: body ?? {} }),
  put: (path, body, opts) => request('PUT', path, { ...opts, body }),
  patch: (path, body, opts) => request('PATCH', path, { ...opts, body }),
  del: (path, opts) => request('DELETE', path, opts),
  upload: (path, file, fieldName = 'file', opts) => {
    const fd = new FormData();
    fd.append(fieldName, file);
    return request('POST', path, { ...opts, formData: fd });
  },
};

function filenameFromDisposition(header) {
  if (!header) return null;
  const star = /filename\*=UTF-8''([^;]+)/i.exec(header);
  if (star) return decodeURIComponent(star[1]);
  const plain = /filename="?([^";]+)"?/i.exec(header);
  return plain ? plain[1] : null;
}

/** Trigger a browser download for a Blob. */
export function saveBlob(blob, filename) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/** Fetch a CSV with the auth header and download it as a file. */
export async function downloadCsv(path, query = {}, fallbackName = 'export.csv') {
  const response = await request('GET', path, { query: { ...query, format: 'csv' }, raw: true });
  if (response instanceof Blob) {
    saveBlob(response, fallbackName);
    return;
  }
  const blob = await response.blob();
  const name = filenameFromDisposition(response.headers?.get?.('Content-Disposition')) || fallbackName;
  saveBlob(blob, name);
}

/** Human-friendly message for any thrown error. */
export function errorMessage(err, fallback = 'Something went wrong.') {
  if (!err) return fallback;
  if (err instanceof ApiError) {
    if (err.detail && err.title && err.detail !== err.title) return `${err.title} ${err.detail}`.trim();
    return err.detail || err.title || fallback;
  }
  return err.message || fallback;
}
