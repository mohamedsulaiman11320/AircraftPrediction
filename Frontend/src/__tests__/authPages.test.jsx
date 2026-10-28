import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AppRoutes } from '../App.jsx';
import { getRefreshToken, getStoredUser, getToken } from '../auth/session.js';
import { NEUTRAL_RESET_MESSAGE } from '../pages/auth/ForgotPasswordPage.jsx';
import { USERS, jsonResponse, mockFetch, problem, renderApp, tokenResponse } from '../test/utils.jsx';

afterEach(() => vi.unstubAllGlobals());

// Exact "Password" field label (the requirements list is labelled "Password requirements").
const PASSWORD_LABEL = /^Password\*?$/;

const dispatcherDashboard = {
  fleetCounts: { total: 1, available: 1, underMaintenance: 0, grounded: 0 },
  readiness: [],
  riskOverview: [],
  recentAlerts: [],
  activeRestrictions: [],
};
const technicianDashboard = { counts: { assignedOpen: 0, inInspection: 0, overdue: 0, awaitingClosure: 0 }, assignedWorkOrders: [], recentlyResolved: [] };

async function fillLogin(user, username = 'fiona', password = 'Whatever-123!') {
  await user.type(screen.getByLabelText(/^Username/), username);
  await user.type(screen.getByLabelText(PASSWORD_LABEL), password);
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
}

describe('Login page', () => {
  it('asks for a username (not an email) and validates empty fields without calling the API', async () => {
    const fetchMock = mockFetch({});
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/login' });
    expect(screen.queryByLabelText(/^Email/)).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(screen.getByText('Username is required.')).toBeInTheDocument();
    expect(screen.getByText('Password is required.')).toBeInTheDocument();
    expect(screen.getByLabelText(/^Username/)).toHaveAttribute('aria-invalid', 'true');
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('posts username + password, stores both tokens and lands a dispatcher on the overview', async () => {
    const fetchMock = mockFetch({
      'POST /auth/login': ({ body }) => {
        expect(body).toEqual({ username: 'fiona', password: 'Whatever-123!' });
        return { ...tokenResponse(USERS.FlightDispatcher), accessToken: 'jwt-abc', refreshToken: 'rt-abc' };
      },
      'GET /dashboards/dispatcher': dispatcherDashboard,
    });
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/login' });
    await fillLogin(user);
    await waitFor(() => expect(window.location.pathname).toBe('/dashboard/dispatcher'));
    expect(getToken()).toBe('jwt-abc');
    expect(getRefreshToken()).toBe('rt-abc');
    expect(getStoredUser().username).toBe('fiona');
    expect(await screen.findByRole('heading', { name: 'Health Dashboard' })).toBeInTheDocument();
    const dashCall = fetchMock.mock.calls.find(([url]) => String(url).includes('/dashboards/dispatcher'));
    expect(dashCall[1].headers.Authorization).toBe('Bearer jwt-abc');
  });

  it('lands a technician on My tickets', async () => {
    mockFetch({
      'POST /auth/login': tokenResponse(USERS.MaintenanceTechnician),
      'GET /dashboards/technician': technicianDashboard,
    });
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/login' });
    await fillLogin(user, 'tom.tech');
    await waitFor(() => expect(window.location.pathname).toBe('/my-tickets'));
    expect(await screen.findByRole('heading', { name: 'My tickets' })).toBeInTheDocument();
  });

  it('honours a safe returnTo parameter', async () => {
    mockFetch({
      'POST /auth/login': tokenResponse(USERS.FlightDispatcher),
      'GET /aircraft': { items: [], page: 1, pageSize: 25, totalCount: 0, totalPages: 0 },
      'GET /aircraft/fleet-groups': [],
    });
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/login?returnTo=%2Ffleet' });
    await fillLogin(user);
    await waitFor(() => expect(window.location.pathname).toBe('/fleet'));
  });

  it('shows the pending-approval page for 403 account_pending', async () => {
    mockFetch({ 'POST /auth/login': problem(403, 'account_pending', 'Account pending') });
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/login' });
    await fillLogin(user);
    expect(await screen.findByRole('heading', { name: 'Account pending approval' })).toBeInTheDocument();
    expect(window.location.pathname).toBe('/pending');
    expect(getToken()).toBeNull();
  });

  it('explains a 423 lockout', async () => {
    mockFetch({ 'POST /auth/login': problem(423, 'account_locked', 'Locked') });
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/login' });
    await fillLogin(user);
    expect(await screen.findByText('Account temporarily locked')).toBeInTheDocument();
    expect(screen.getByText(/about 15 minutes/)).toBeInTheDocument();
  });

  it('shows generic invalid-credentials, disabled and rate-limit messages', async () => {
    const responses = [problem(401, 'invalid_credentials', 'Unauthorized'), problem(403, 'account_disabled', 'Disabled'), problem(429, null, 'Too many')];
    mockFetch({ 'POST /auth/login': () => responses.shift() });
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/login' });
    await fillLogin(user);
    expect(await screen.findByText('The username or password is incorrect.')).toBeInTheDocument();
    // a failed login never triggers a token refresh
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(await screen.findByText('Account disabled')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(await screen.findByText('Too many attempts')).toBeInTheDocument();
  });
});

describe('Register page', () => {
  it('validates fields inline, mirroring the server username and password policies', async () => {
    const fetchMock = mockFetch({});
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/register' });
    await user.type(screen.getByLabelText(/^Username/), 'a!');
    await user.type(screen.getByLabelText(/^Email/), 'not-an-email');
    await user.type(screen.getByLabelText(PASSWORD_LABEL), 'weakpassword');
    await user.type(screen.getByLabelText(/^Confirm password/), 'different');
    await user.click(screen.getByRole('button', { name: 'Create account' }));
    expect(screen.getByText('Username must be at least 3 characters.')).toBeInTheDocument();
    expect(screen.getByText('Enter a valid email address.')).toBeInTheDocument();
    expect(screen.getByText('Display name is required.')).toBeInTheDocument();
    expect(screen.getByText('Password must contain an upper-case letter.')).toBeInTheDocument();
    expect(screen.getByText('Passwords do not match.')).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
    expect(screen.getByText('A lower-case letter').closest('li')).toHaveAttribute('data-ok', 'true');
    expect(screen.getByText('A digit').closest('li')).toHaveAttribute('data-ok', 'false');
  });

  it('lower-cases the username and submits a valid registration', async () => {
    let posted = null;
    mockFetch({
      'POST /auth/register': ({ body }) => {
        posted = body;
        return jsonResponse(202, { message: 'Registration received. An administrator will review it.' });
      },
    });
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/register' });
    await user.type(screen.getByLabelText(/^Username/), 'New.Person');
    await user.type(screen.getByLabelText(/^Email/), 'new@example.com');
    await user.type(screen.getByLabelText(/^Display name/), 'New Person');
    await user.type(screen.getByLabelText(PASSWORD_LABEL), 'Correct-Horse-9');
    await user.type(screen.getByLabelText(/^Confirm password/), 'Correct-Horse-9');
    await user.click(screen.getByRole('button', { name: 'Create account' }));
    expect(await screen.findByText('Registration received. An administrator will review it.')).toBeInTheDocument();
    expect(posted).toEqual({ username: 'new.person', email: 'new@example.com', displayName: 'New Person', password: 'Correct-Horse-9', confirmPassword: 'Correct-Horse-9' });
  });
});

describe('Forgot password page', () => {
  it.each([
    ['success', () => jsonResponse(202, { message: 'server text' })],
    ['not found', () => problem(404, 'not_found', 'Not found')],
    ['validation', () => problem(400, 'validation_failed', 'Bad')],
  ])('always shows the same neutral confirmation (%s)', async (_label, response) => {
    mockFetch({ 'POST /auth/forgot-password': response });
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/forgot-password' });
    await user.type(screen.getByLabelText(/^Email/), 'someone@example.com');
    await user.click(screen.getByRole('button', { name: 'Send reset link' }));
    expect(await screen.findByText(NEUTRAL_RESET_MESSAGE)).toBeInTheDocument();
    expect(screen.queryByText('server text')).not.toBeInTheDocument();
  });
});

describe('Reset password page', () => {
  it('requires a token in the URL', () => {
    mockFetch({});
    renderApp(<AppRoutes />, { route: '/reset-password' });
    expect(screen.getByText('Missing reset token')).toBeInTheDocument();
  });

  it('posts token + new password and returns to login', async () => {
    const fetchMock = mockFetch({ 'POST /auth/reset-password': new Response(null, { status: 204 }) });
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/reset-password?token=abc123' });
    await user.type(screen.getByLabelText(/^New password/), 'Correct-Horse-9');
    await user.type(screen.getByLabelText(/^Confirm new password/), 'Correct-Horse-9');
    await user.click(screen.getByRole('button', { name: 'Update password' }));
    await waitFor(() => expect(window.location.pathname).toBe('/login'));
    expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toEqual({ token: 'abc123', newPassword: 'Correct-Horse-9', confirmPassword: 'Correct-Horse-9' });
    expect(await screen.findByText('Password updated')).toBeInTheDocument();
  });
});
