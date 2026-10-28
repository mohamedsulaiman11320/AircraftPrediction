import { render } from '@testing-library/react';
import { vi } from 'vitest';
import { Router } from '../router/Router.jsx';
import { AuthProvider } from '../auth/AuthContext.jsx';
import { ToastProvider } from '../components/Toast.jsx';
import { setSession } from '../auth/session.js';

export const USERS = {
  FlightDispatcher: { id: 1, username: 'fiona', email: 'fd@example.com', displayName: 'Fiona Dispatch', roles: ['FlightDispatcher'], status: 'Active' },
  MaintenanceTechnician: { id: 2, username: 'tom.tech', email: 'mt@example.com', displayName: 'Tom Tech', roles: ['MaintenanceTechnician'], status: 'Active' },
  Both: { id: 3, username: 'both', email: 'both@example.com', displayName: 'Bo Both', roles: ['FlightDispatcher', 'MaintenanceTechnician'], status: 'Active' },
  DataEngineer: { id: 5, username: 'eli', email: 'de@example.com', displayName: 'Eli Engineer', roles: ['DataEngineer'], status: 'Active' },
};

/** A TokenResponse as returned by /auth/login and /auth/refresh. */
export function tokenResponse(user, n = 1) {
  return {
    accessToken: `access-${n}`,
    tokenType: 'Bearer',
    expiresAtUtc: new Date(Date.now() + 3600000).toISOString(),
    refreshToken: `refresh-${n}`,
    refreshTokenExpiresAtUtc: new Date(Date.now() + 7 * 86400000).toISOString(),
    user,
  };
}

export function jsonResponse(status, body, headers = {}) {
  const text = body === undefined ? '' : JSON.stringify(body);
  return new Response(status === 204 ? null : text, {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json', ...headers },
  });
}

export function problem(status, code, title = 'Error', extra = {}) {
  return jsonResponse(status, { type: `https://httpstatuses.io/${status}`, title, status, code, traceId: 'trace-1', ...extra });
}

/**
 * Install a fetch mock. `routes` maps "METHOD /path" (path after /api/v1, no query) to a
 * function(request info) returning a Response (or a plain value -> 200 JSON).
 */
export function mockFetch(routes = {}) {
  const fn = vi.fn(async (url, init = {}) => {
    const u = new URL(url);
    const path = u.pathname.replace(/^\/api\/v1/, '');
    const key = `${(init.method || 'GET').toUpperCase()} ${path}`;
    const handler = routes[key];
    if (!handler) return problem(404, 'not_found', `No mock for ${key}`);
    const res = typeof handler === 'function' ? await handler({ url: u, init, body: init.body && typeof init.body === 'string' ? JSON.parse(init.body) : init.body }) : handler;
    return res instanceof Response ? res : jsonResponse(200, res);
  });
  vi.stubGlobal('fetch', fn);
  return fn;
}

export function signIn(user) {
  setSession({ ...tokenResponse(user), accessToken: 'test-token', refreshToken: 'test-refresh' });
}

export function renderApp(ui, { route = '/', user } = {}) {
  window.history.replaceState(null, '', route);
  if (user) signIn(user);
  return render(
    <Router>
      <AuthProvider>
        <ToastProvider>{ui}</ToastProvider>
      </AuthProvider>
    </Router>,
  );
}
