import { describe, expect, it } from 'vitest';
import { act, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AppRoutes } from '../App.jsx';
import { USERS, mockFetch, renderApp } from '../test/utils.jsx';
import { getToken, getRefreshToken, getStoredUser } from '../auth/session.js';

const dashboard = {
  fleetCounts: { total: 0 }, readiness: [], riskOverview: [], recentAlerts: [], activeRestrictions: [], aircraftWithoutPrediction: 0,
  health: { aircraft: 0, highRisk: 0, withinLimits: 0, notAssessable: 0, stale: 0 },
  prediction: { horizonDays: 30, validated: 0, notValidated: 0, noModelPrediction: 0 },
  workOrders: { open: 0, assigned: 0, inInspection: 0, resolved: 0, closed: 0, cancelled: 0 },
  aircraftRisk: [], thresholds: [],
};

function routes(extra = {}) {
  return {
    'GET /auth/me': USERS.FlightDispatcher,
    'GET /dashboards/dispatcher': dashboard,
    'GET /aircraft': { items: [], page: 1, pageSize: 25, totalCount: 0 },
    'GET /aircraft/fleet-groups': [],
    'POST /auth/logout': new Response(null, { status: 204 }),
    ...extra,
  };
}

describe('profile menu', () => {
  it('replaces the standalone sign-out button with an avatar showing the real user', async () => {
    mockFetch(routes());
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/dashboard/dispatcher', user: USERS.FlightDispatcher });
    const avatar = await screen.findByTestId('profile-avatar');
    expect(avatar).toHaveTextContent('FD');                                   // initials of "Fiona Dispatch"
    expect(screen.queryByRole('button', { name: /sign out/i })).not.toBeInTheDocument();
    await user.click(avatar);
    expect(avatar).toHaveAttribute('aria-expanded', 'true');
    const popover = screen.getByTestId('profile-popover');
    expect(screen.getByTestId('profile-email')).toHaveTextContent('fd@example.com');
    expect(screen.getByTestId('profile-role')).toHaveTextContent('Flight Dispatcher');
    expect(popover).toContainElement(screen.getByTestId('sign-out'));
    expect(screen.getByTestId('sign-out')).toHaveFocus();
    expect(popover.textContent).not.toMatch(/token|access-/i);               // no credentials exposed
  });

  it('closes on Escape (focus returns to the avatar) and on an outside click', async () => {
    mockFetch(routes());
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/dashboard/dispatcher', user: USERS.FlightDispatcher });
    const avatar = await screen.findByTestId('profile-avatar');
    await user.click(avatar);
    await user.keyboard('{Escape}');
    expect(screen.queryByTestId('profile-popover')).not.toBeInTheDocument();
    expect(avatar).toHaveFocus();
    await user.click(avatar);
    await user.click(screen.getByRole('heading', { level: 1 }));
    expect(screen.queryByTestId('profile-popover')).not.toBeInTheDocument();
  });

  it('signs out through the existing logout, clears the session and Back cannot show a protected page', async () => {
    const fetchMock = mockFetch(routes());
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/dashboard/dispatcher', user: USERS.FlightDispatcher });
    await screen.findByTestId('kpi-threshold');
    // A second protected page, so Back has an authenticated entry to return to.
    await user.click(screen.getByRole('link', { name: /Fleet Health/ }));
    await waitFor(() => expect(window.location.pathname).toBe('/fleet'));

    await user.click(screen.getByTestId('profile-avatar'));
    await user.click(screen.getByTestId('sign-out'));
    await waitFor(() => expect(window.location.pathname).toBe('/login'));
    expect(fetchMock.mock.calls.some(([url, init]) => String(url).endsWith('/auth/logout') && init?.method === 'POST')).toBe(true);
    expect(getToken()).toBeNull();
    expect(getRefreshToken()).toBeNull();
    expect(getStoredUser()).toBeNull();

    const visited = [];
    const onPop = () => visited.push(window.location.pathname);
    window.addEventListener('popstate', onPop);
    await act(async () => {
      window.history.back();
      await new Promise((r) => setTimeout(r, 50));
    });
    window.removeEventListener('popstate', onPop);
    expect(visited).toEqual(['/dashboard/dispatcher']);                     // Back really reached the protected entry
    // The history entry is the dashboard, but the guard re-checks and sends the user to sign in.
    await waitFor(() => expect(window.location.pathname).toBe('/login'));
    expect(screen.queryByTestId('kpi-threshold')).not.toBeInTheDocument();
    expect(screen.queryByTestId('profile-avatar')).not.toBeInTheDocument();
  });
});

describe('initials', () => {
  it('uses only words that start with a letter', async () => {
    const { initials } = await import('../utils/format.js');
    expect(initials('Flight Dispatcher (synthetic)')).toBe('FD');
    expect(initials('Fiona Dispatch')).toBe('FD');
    expect(initials('fd@example.com')).toBe('F');
    expect(initials('  ')).toBe('?');
    expect(initials('(x) 42')).toBe('?');
  });
});
