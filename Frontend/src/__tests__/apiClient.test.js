import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, api, buildUrl, errorMessage, parseError, request } from '../api/client.js';
import { getRefreshToken, getToken, setSession } from '../auth/session.js';
import { jsonResponse, mockFetch, problem, tokenResponse } from '../test/utils.jsx';

afterEach(() => {
  vi.unstubAllGlobals();
});

function signedIn() {
  setSession({ accessToken: 'tok-123', expiresAtUtc: new Date(Date.now() + 3600000).toISOString(), refreshToken: 'rt-1', refreshTokenExpiresAtUtc: new Date(Date.now() + 7 * 86400000).toISOString(), user: { id: 1 } });
}

describe('api client', () => {
  it('builds URLs below /api/v1 with query strings', () => {
    expect(buildUrl('/aircraft', { page: 2, search: '' })).toBe('http://localhost:5080/api/v1/aircraft?page=2');
  });

  it('parses RFC 9457 ProblemDetails into ApiError', async () => {
    const res = problem(400, 'validation_failed', 'Validation failed', { detail: 'One or more fields are invalid.', errors: { email: ['Email is required.'] } });
    const err = await parseError(res);
    expect(err).toBeInstanceOf(ApiError);
    expect(err.status).toBe(400);
    expect(err.code).toBe('validation_failed');
    expect(err.title).toBe('Validation failed');
    expect(err.detail).toBe('One or more fields are invalid.');
    expect(err.errors).toEqual({ email: ['Email is required.'] });
    expect(err.traceId).toBe('trace-1');
    expect(err.fieldError('Email')).toBe('Email is required.');
  });

  it('handles non-JSON error bodies', async () => {
    const err = await parseError(new Response('Gateway exploded', { status: 502 }));
    expect(err.status).toBe(502);
    expect(err.detail).toBe('Gateway exploded');
    expect(err.code).toBeNull();
  });

  it('sends JSON with the bearer token and returns parsed data', async () => {
    signedIn();
    const fetchMock = vi.fn(async () => jsonResponse(200, { ok: true }));
    vi.stubGlobal('fetch', fetchMock);
    const data = await api.post('/work-orders', { title: 'x' });
    expect(data).toEqual({ ok: true });
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe('http://localhost:5080/api/v1/work-orders');
    expect(init.method).toBe('POST');
    expect(init.headers.Authorization).toBe('Bearer tok-123');
    expect(init.headers['Content-Type']).toBe('application/json');
    expect(JSON.parse(init.body)).toEqual({ title: 'x' });
  });

  it('returns null for 204 No Content', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 204 })));
    await expect(api.post('/auth/logout')).resolves.toBeNull();
  });

  it('throws ApiError with server code for business-rule errors', async () => {
    signedIn();
    vi.stubGlobal('fetch', vi.fn(async () => problem(400, 'business_rule_violation', 'Status change refused', { detail: 'Grounding flag active.' })));
    const err = await request('POST', '/aircraft/1/status', { body: {} }).catch((e) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect(err.code).toBe('business_rule_violation');
    expect(errorMessage(err)).toBe('Status change refused Grounding flag active.');
    // A 400 must not clear the session
    expect(getToken()).toBe('tok-123');
  });

  it('clears the session and redirects to /login when the refresh after a 401 also fails', async () => {
    signedIn();
    window.history.replaceState(null, '', '/fleet?status=Grounded');
    vi.stubGlobal('fetch', vi.fn(async () => problem(401, 'token_revoked', 'Unauthorized')));
    const err = await api.get('/aircraft').catch((e) => e);
    expect(err.status).toBe(401);
    expect(getToken()).toBeNull();
    expect(window.location.pathname).toBe('/login');
    const qs = new URLSearchParams(window.location.search);
    expect(qs.get('returnTo')).toBe('/fleet?status=Grounded');
    expect(qs.get('reason')).toBe('expired');
  });

  it('does not redirect on 401 when no token was sent (e.g. bad credentials)', async () => {
    window.history.replaceState(null, '', '/login');
    vi.stubGlobal('fetch', vi.fn(async () => problem(401, 'invalid_credentials', 'Invalid credentials')));
    const err = await api.post('/auth/login', { email: 'a', password: 'b' }, { auth: false }).catch((e) => e);
    expect(err.code).toBe('invalid_credentials');
    expect(window.location.search).toBe('');
  });

  it('maps network failures to status 0', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Promise.reject(new TypeError('Failed to fetch'))));
    const err = await api.get('/aircraft').catch((e) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect(err.status).toBe(0);
    expect(err.code).toBe('network_error');
  });

  it('uploads files as multipart FormData without forcing a content type', async () => {
    signedIn();
    const fetchMock = vi.fn(async () => jsonResponse(202, { id: 'imp1', status: 'Queued', statusUrl: '/x' }));
    vi.stubGlobal('fetch', fetchMock);
    const file = new File(['a,b\n'], 'data.csv', { type: 'text/csv' });
    const res = await api.upload('/telemetry/imports', file);
    expect(res.id).toBe('imp1');
    const init = fetchMock.mock.calls[0][1];
    expect(init.body).toBeInstanceOf(FormData);
    expect(init.body.get('file').name).toBe('data.csv');
    expect(init.headers['Content-Type']).toBeUndefined();
  });
});

describe('refresh token handling', () => {
  const user = { id: 1, username: 'fiona', roles: ['FlightDispatcher'], status: 'Active' };

  it('on 401 refreshes once, stores the rotated tokens and retries the request with the new access token', async () => {
    signedIn();
    const fetchMock = mockFetch({
      'GET /aircraft': ({ init }) =>
        init.headers.Authorization === 'Bearer access-2' ? { items: [], totalCount: 0 } : problem(401, 'token_expired', 'Unauthorized'),
      'POST /auth/refresh': ({ body }) => {
        expect(body).toEqual({ refreshToken: 'rt-1' });
        return tokenResponse(user, 2);
      },
    });
    const data = await api.get('/aircraft');
    expect(data).toEqual({ items: [], totalCount: 0 });
    expect(getToken()).toBe('access-2');
    expect(getRefreshToken()).toBe('refresh-2');
    const calls = fetchMock.mock.calls.map(([u, i]) => `${i.method} ${new URL(u).pathname}`);
    expect(calls).toEqual(['GET /api/v1/aircraft', 'POST /api/v1/auth/refresh', 'GET /api/v1/aircraft']);
  });

  it('is single-flight: concurrent 401s share one refresh call', async () => {
    signedIn();
    let refreshes = 0;
    const fetchMock = mockFetch({
      'GET /aircraft': ({ init }) => (init.headers.Authorization === 'Bearer access-2' ? { ok: 'a' } : problem(401, null, 'Unauthorized')),
      'GET /anomalies': ({ init }) => (init.headers.Authorization === 'Bearer access-2' ? { ok: 'b' } : problem(401, null, 'Unauthorized')),
      'GET /predictions': ({ init }) => (init.headers.Authorization === 'Bearer access-2' ? { ok: 'c' } : problem(401, null, 'Unauthorized')),
      'POST /auth/refresh': async () => {
        refreshes++;
        await new Promise((r) => setTimeout(r, 20));
        return tokenResponse(user, 2);
      },
    });
    const results = await Promise.all([api.get('/aircraft'), api.get('/anomalies'), api.get('/predictions')]);
    expect(results).toEqual([{ ok: 'a' }, { ok: 'b' }, { ok: 'c' }]);
    expect(refreshes).toBe(1);
    expect(fetchMock.mock.calls.filter(([u]) => String(u).endsWith('/auth/refresh'))).toHaveLength(1);
  });

  it('retries only once: a second 401 after a successful refresh signs the user out', async () => {
    signedIn();
    window.history.replaceState(null, '', '/fleet');
    const fetchMock = mockFetch({
      'GET /aircraft': problem(401, null, 'Unauthorized'),
      'POST /auth/refresh': tokenResponse(user, 2),
    });
    const err = await api.get('/aircraft').catch((e) => e);
    expect(err.status).toBe(401);
    expect(fetchMock.mock.calls.filter(([u]) => String(u).endsWith('/auth/refresh'))).toHaveLength(1);
    expect(fetchMock.mock.calls.filter(([u]) => String(u).includes('/aircraft'))).toHaveLength(2);
    expect(getToken()).toBeNull();
    expect(getRefreshToken()).toBeNull();
    expect(window.location.pathname).toBe('/login');
    expect(new URLSearchParams(window.location.search).get('reason')).toBe('expired');
  });

  it('refresh failure (invalid_refresh_token) clears the session and redirects to /login?reason=expired', async () => {
    signedIn();
    window.history.replaceState(null, '', '/work-orders');
    mockFetch({
      'GET /work-orders': problem(401, null, 'Unauthorized'),
      'POST /auth/refresh': problem(401, 'invalid_refresh_token', 'Unauthorized'),
    });
    const err = await api.get('/work-orders').catch((e) => e);
    expect(err.status).toBe(401);
    expect(getToken()).toBeNull();
    expect(getRefreshToken()).toBeNull();
    expect(window.location.pathname).toBe('/login');
    expect(new URLSearchParams(window.location.search).get('reason')).toBe('expired');
  });

  it('refreshes up-front when the stored access token has expired', async () => {
    setSession({
      accessToken: 'old',
      expiresAtUtc: new Date(Date.now() - 1000).toISOString(),
      refreshToken: 'rt-1',
      refreshTokenExpiresAtUtc: new Date(Date.now() + 86400000).toISOString(),
      user,
    });
    const fetchMock = mockFetch({
      'POST /auth/refresh': tokenResponse(user, 5),
      'GET /auth/me': ({ init }) => (init.headers.Authorization === 'Bearer access-5' ? user : problem(401, null, 'Unauthorized')),
    });
    await expect(api.get('/auth/me')).resolves.toEqual(user);
    expect(fetchMock.mock.calls.map(([u]) => new URL(u).pathname)).toEqual(['/api/v1/auth/refresh', '/api/v1/auth/me']);
  });

  it('logout sends the refresh token', async () => {
    signedIn();
    const fetchMock = mockFetch({ 'POST /auth/logout': new Response(null, { status: 204 }) });
    const { authApi } = await import('../api/auth.js');
    await authApi.logout(getRefreshToken());
    expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toEqual({ refreshToken: 'rt-1' });
  });
});
