import { describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ACCESS, DATA_ENGINEER, DISPATCHER, TECHNICIAN, defaultPathFor, getNavSections } from '../router/access.js';
import { hasAccess, isPendingUser, roleLabel } from '../auth/roles.js';
import { RequireAuth, RequireRole } from '../router/guards.jsx';
import { Routes } from '../router/Router.jsx';
import { AppRoutes } from '../App.jsx';
import { Sidebar } from '../components/Sidebar.jsx';
import { USERS, mockFetch, renderApp } from '../test/utils.jsx';

const labels = (user) => getNavSections(user).flatMap((s) => s.items.map((i) => i.label));

// User Management belongs to the Data Engineer (API: Policies.DataEngineer); the dispatcher keeps four menus.
const DISPATCHER_NAV = ['Health Dashboard', 'Fleet Health', 'Anomalies / Work Orders', 'Maintenance Log'];
const DATA_ENGINEER_NAV = ['Data Model', 'Pipeline', 'Data Quality', 'Model Versions', 'User Management'];
const DATA_ENGINEER_USER = { id: 5, username: 'eli', email: 'de@example.com', displayName: 'Eli Engineer', roles: [DATA_ENGINEER], status: 'Active' };

describe('role-based navigation (RBAC v2)', () => {
  it('Flight Dispatcher sees exactly the four scoped tabs, in order, without User Management', () => {
    expect(labels(USERS.FlightDispatcher)).toEqual(DISPATCHER_NAV);
  });

  it('Maintenance Technician sees only My tickets', () => {
    expect(labels(USERS.MaintenanceTechnician)).toEqual(['My tickets']);
  });

  it('Data Engineer sees exactly its five menus, in order, and lands on menu 1', () => {
    expect(labels(DATA_ENGINEER_USER)).toEqual(DATA_ENGINEER_NAV);
    expect(defaultPathFor(DATA_ENGINEER_USER)).toBe('/imports');
  });

  it('the data engineer menus cover their sibling routes', () => {
    const items = getNavSections(DATA_ENGINEER_USER).flatMap((s) => s.items);
    expect(items.map((i) => [i.to, i.icon])).toEqual([
      ['/imports', 'upload'],
      ['/pipelines', 'flow'],
      ['/data-quality', 'check'],
      ['/models', 'cube'],
      ['/admin/users', 'users'],
    ]);
    expect(items[0].match).toEqual(['/imports', '/telemetry']);
    expect(items[3].match).toEqual(['/models']);
  });

  it('Model Versions replaces Model & Forecast and the development lab is Data Engineer only', () => {
    expect(labels(DATA_ENGINEER_USER)).not.toContain('Model & Forecast');
    expect(ACCESS.modelsView).toEqual([DATA_ENGINEER]);
    expect(ACCESS.developmentLab).toEqual([DATA_ENGINEER]);
    expect(hasAccess(USERS.FlightDispatcher, ACCESS.developmentLab)).toBe(false);
    expect(hasAccess(USERS.MaintenanceTechnician, ACCESS.developmentLab)).toBe(false);
    expect(ACCESS.predictions).toEqual([DATA_ENGINEER]);
    expect(hasAccess(DATA_ENGINEER_USER, ACCESS.modelsView)).toBe(true);
    expect(hasAccess(DATA_ENGINEER_USER, ACCESS.predictions)).toBe(true);
  });

  it.each([
    ['/imports', 'Data Model'],
    ['/telemetry', 'Data Model'],
  ])('marks the data engineer menu current on %s', (route, label) => {
    renderApp(<Sidebar open />, { route, user: DATA_ENGINEER_USER });
    const nav = screen.getByRole('navigation', { name: 'Primary' });
    expect(within(nav).getAllByRole('link').map((l) => l.textContent)).toEqual(DATA_ENGINEER_NAV);
    expect(within(nav).getByRole('link', { name: label })).toHaveAttribute('aria-current', 'page');
  });

  it.each(['/models', '/models/development'])('marks Model Versions current on %s', (route) => {
    renderApp(<Sidebar open />, { route, user: DATA_ENGINEER_USER });
    const nav = screen.getByRole('navigation', { name: 'Primary' });
    expect(within(nav).getAllByRole('link').map((l) => l.textContent)).toEqual(DATA_ENGINEER_NAV);
    expect(within(nav).getByRole('link', { name: 'Model Versions' })).toHaveAttribute('aria-current', 'page');
  });

  it('shows the same menus and marks none of them current on /predictions', () => {
    renderApp(<Sidebar open />, { route: '/predictions', user: DATA_ENGINEER_USER });
    const nav = screen.getByRole('navigation', { name: 'Primary' });
    expect(within(nav).getAllByRole('link').map((l) => l.textContent)).toEqual(DATA_ENGINEER_NAV);
    for (const l of within(nav).getAllByRole('link')) expect(l).not.toHaveAttribute('aria-current');
  });

  it('User Management is a Data Engineer capability, not a dispatcher one', () => {
    expect(ACCESS.adminUsers).toEqual([DATA_ENGINEER]);
    expect(hasAccess(USERS.FlightDispatcher, ACCESS.adminUsers)).toBe(false);
    expect(hasAccess(DATA_ENGINEER_USER, ACCESS.adminUsers)).toBe(true);
  });

  it('a user holding both roles gets the union', () => {
    expect(labels(USERS.Both)).toEqual([DISPATCHER_NAV[0], 'My tickets', ...DISPATCHER_NAV.slice(1)]);
  });

  it('shows nothing for pending, role-less or disabled users', () => {
    expect(labels({ ...USERS.FlightDispatcher, status: 'Pending' })).toEqual([]);
    expect(labels({ ...USERS.FlightDispatcher, roles: [] })).toEqual([]);
    expect(labels({ ...USERS.FlightDispatcher, status: 'Disabled' })).toEqual([]);
    expect(isPendingUser({ status: 'Active', roles: [] })).toBe(true);
  });

  it('computes landing pages and access checks from the roles array', () => {
    expect(defaultPathFor(USERS.FlightDispatcher)).toBe('/dashboard/dispatcher');
    expect(defaultPathFor(USERS.MaintenanceTechnician)).toBe('/my-tickets');
    expect(defaultPathFor(USERS.Both)).toBe('/dashboard/dispatcher');
    expect(defaultPathFor({ ...USERS.FlightDispatcher, roles: [] })).toBe('/pending');
    expect(hasAccess(USERS.MaintenanceTechnician, [DISPATCHER])).toBe(false);
    expect(hasAccess(USERS.Both, [TECHNICIAN])).toBe(true);
  });

  it('renders friendly role labels from role names', () => {
    expect(roleLabel('FlightDispatcher')).toBe('Flight Dispatcher');
    expect(roleLabel('MaintenanceTechnician')).toBe('Maintenance Technician');
  });

  it('renders the technician sidebar with only My tickets', () => {
    renderApp(<Sidebar open />, { route: '/my-tickets', user: USERS.MaintenanceTechnician });
    const nav = screen.getByRole('navigation', { name: 'Primary' });
    expect(within(nav).getAllByRole('link').map((l) => l.textContent)).toEqual(['My tickets']);
    expect(within(nav).getByRole('link', { name: 'My tickets' })).toHaveAttribute('aria-current', 'page');
  });

  it('marks the active dispatcher link with aria-current', () => {
    renderApp(<Sidebar open />, { route: '/fleet/abc', user: USERS.FlightDispatcher });
    const nav = screen.getByRole('navigation', { name: 'Primary' });
    expect(within(nav).getByRole('link', { name: 'Fleet Health' })).toHaveAttribute('aria-current', 'page');
  });

  it.each(['/anomalies', '/anomalies/an-1', '/work-orders', '/work-orders/wo-1'])('marks Anomalies / Work Orders current on %s — work orders live under it', (route) => {
    renderApp(<Sidebar open />, { route, user: USERS.FlightDispatcher });
    const nav = screen.getByRole('navigation', { name: 'Primary' });
    expect(within(nav).getByRole('link', { name: 'Anomalies / Work Orders' })).toHaveAttribute('aria-current', 'page');
    expect(within(nav).getByRole('link', { name: 'Fleet Health' })).not.toHaveAttribute('aria-current');
  });

  it('labels the anomalies menu exactly "Anomalies / Work Orders"', () => {
    const items = getNavSections(USERS.FlightDispatcher).flatMap((s) => s.items);
    const anomalies = items.find((i) => i.label === 'Anomalies / Work Orders');
    expect(anomalies).toBeDefined();
    expect(anomalies.to).toBe('/anomalies');
    expect(anomalies.icon).toBe('alert');
    expect(anomalies.match).toEqual(['/anomalies', '/work-orders']);
    expect(items.map((i) => i.label)).toContain('Maintenance Log');
    expect(items.map((i) => i.label)).not.toContain('Anomalies');
    expect(items.map((i) => i.label)).not.toContain('Reports / Maintenance Log');
    // a label change only: the work-order capabilities behind the tab are untouched
    expect(hasAccess(USERS.FlightDispatcher, ACCESS.workOrdersView)).toBe(true);
    expect(hasAccess(USERS.FlightDispatcher, ACCESS.workOrderCreate)).toBe(true);
  });

  it('counts four dispatcher menus, five for the data engineer and one for the technician', () => {
    expect(labels(USERS.FlightDispatcher)).toHaveLength(4);
    expect(labels(DATA_ENGINEER_USER)).toHaveLength(5);
    expect(labels(USERS.MaintenanceTechnician)).toHaveLength(1);
  });
});

describe('dispatcher tab 3: anomalies and work orders read as one tab', () => {
  const page = (items) => ({ items, page: 1, pageSize: 25, totalCount: items.length, totalPages: 1 });

  it('offers both sub-tabs and navigates between the two screens', async () => {
    mockFetch({
      'GET /auth/me': USERS.FlightDispatcher,
      'GET /aircraft': page([]),
      'GET /anomalies': page([]),
      'GET /anomalies/summary': { total: 0, open: 0, bySeverity: {}, byDay: [] },
      'GET /work-orders': page([]),
    });
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/anomalies', user: USERS.FlightDispatcher });
    const tablist = await screen.findByRole('tablist', { name: 'Anomalies and work orders' });
    expect(within(tablist).getAllByRole('tab').map((t) => t.textContent)).toEqual(['Anomalies', 'Work orders']);
    expect(within(tablist).getByRole('tab', { name: 'Anomalies' })).toHaveAttribute('aria-selected', 'true');
    await user.click(within(tablist).getByRole('tab', { name: 'Work orders' }));
    await waitFor(() => expect(window.location.pathname).toBe('/work-orders'));
    expect(await screen.findByRole('heading', { level: 1, name: 'Work orders' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Work orders' })).toHaveAttribute('aria-selected', 'true');
  });
});

describe('dispatcher tab 4: maintenance log', () => {
  const emptyPage = { items: [], page: 1, pageSize: 25, totalCount: 0, totalPages: 0 };

  it('the Maintenance Log menu renders the read-only maintenance log, with no create form', async () => {
    mockFetch({
      'GET /auth/me': USERS.FlightDispatcher,
      'GET /aircraft': emptyPage,
      'GET /maintenance-logs': emptyPage,
    });
    const item = getNavSections(USERS.FlightDispatcher)
      .flatMap((s) => s.items)
      .find((i) => i.label === 'Maintenance Log');
    expect(item.to).toBe('/maintenance-log');
    renderApp(<AppRoutes />, { route: item.to, user: USERS.FlightDispatcher });
    expect(await screen.findByRole('heading', { level: 1, name: 'Maintenance Log' })).toBeInTheDocument();
    expect(await screen.findByText('No maintenance records recorded yet')).toBeInTheDocument();
    expect(screen.getByLabelText('Event type')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Record|Submit|New/ })).not.toBeInTheDocument();
  });

  it('the Reports page no longer duplicates Fleet Health or the Maintenance Log', async () => {
    mockFetch({ 'GET /auth/me': USERS.FlightDispatcher, 'GET /reports/maintenance-history': { months: [] } });
    renderApp(<AppRoutes />, { route: '/reports', user: USERS.FlightDispatcher });
    expect(await screen.findByRole('heading', { level: 1, name: 'Reports' })).toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Maintenance log' })).not.toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Fleet health' })).not.toBeInTheDocument();
    // one report is left for this role, so there is no stranded tab strip either
    expect(screen.queryByRole('tablist', { name: 'Reports' })).not.toBeInTheDocument();
    expect(screen.getByText(/Monthly scheduled, unscheduled and inspection counts/)).toBeInTheDocument();
  });

  it('a Data Engineer still sees its own two report tabs', async () => {
    const de = { id: 5, username: 'eli', email: 'de@example.com', displayName: 'Eli Engineer', roles: [DATA_ENGINEER], status: 'Active' };
    mockFetch({ 'GET /auth/me': de, 'GET /reports/data-quality': { totalRecords: 0 } });
    renderApp(<AppRoutes />, { route: '/reports', user: de });
    const tablist = await screen.findByRole('tablist', { name: 'Reports' });
    expect(within(tablist).getAllByRole('tab').map((t) => t.textContent)).toEqual(['Data quality', 'Model performance']);
  });
});

describe('route guards', () => {
  const routes = [
    {
      path: '/pipelines',
      element: (
        <RequireAuth>
          <RequireRole allow={[DISPATCHER]}>
            <h1>Pipelines</h1>
          </RequireRole>
        </RequireAuth>
      ),
    },
    { path: '/access-denied', element: <h1>Denied</h1> },
    { path: '/login', element: <h1>Login</h1> },
    { path: '/pending', element: <h1>Pending</h1> },
  ];

  it('RequireRole redirects users without the role to /access-denied', async () => {
    mockFetch({ 'GET /auth/me': USERS.MaintenanceTechnician });
    renderApp(<Routes routes={routes} />, { route: '/pipelines', user: USERS.MaintenanceTechnician });
    expect(await screen.findByRole('heading', { name: 'Denied' })).toBeInTheDocument();
    expect(window.location.pathname).toBe('/access-denied');
    expect(new URLSearchParams(window.location.search).get('from')).toBe('/pipelines');
  });

  it('RequireRole renders children for permitted roles', async () => {
    mockFetch({ 'GET /auth/me': USERS.FlightDispatcher });
    renderApp(<Routes routes={routes} />, { route: '/pipelines', user: USERS.FlightDispatcher });
    expect(await screen.findByRole('heading', { name: 'Pipelines' })).toBeInTheDocument();
  });

  it('RequireAuth sends anonymous users to /login with returnTo', async () => {
    renderApp(<Routes routes={routes} />, { route: '/pipelines' });
    expect(await screen.findByRole('heading', { name: 'Login' })).toBeInTheDocument();
    expect(new URLSearchParams(window.location.search).get('returnTo')).toBe('/pipelines');
  });

  it('RequireAuth sends signed-in users without roles to /pending', async () => {
    const pending = { ...USERS.FlightDispatcher, roles: [], status: 'Pending' };
    mockFetch({ 'GET /auth/me': pending });
    renderApp(<Routes routes={routes} />, { route: '/pipelines', user: pending });
    expect(await screen.findByRole('heading', { name: 'Pending' })).toBeInTheDocument();
  });

  it.each(['/fleet', '/fleet/a1', '/anomalies', '/predictions', '/telemetry', '/reports', '/maintenance-log', '/admin/users', '/work-orders', '/dashboard/dispatcher', '/pipelines'])(
    'a technician opening %s is sent to access denied',
    async (path) => {
      mockFetch({ 'GET /auth/me': USERS.MaintenanceTechnician });
      renderApp(<AppRoutes />, { route: path, user: USERS.MaintenanceTechnician });
      await waitFor(() => expect(window.location.pathname).toBe('/access-denied'));
      expect(await screen.findByRole('heading', { name: 'Access denied' })).toBeInTheDocument();
    },
  );

  it.each(['/telemetry', '/imports', '/data-quality', '/pipelines', '/models', '/predictions', '/audit', '/dashboard/leadership', '/dashboard/data-engineer', '/fleet/new'])(
    'a dispatcher scoped to five tabs is sent to access denied from %s',
    async (path) => {
      mockFetch({ 'GET /auth/me': USERS.FlightDispatcher });
      renderApp(<AppRoutes />, { route: path, user: USERS.FlightDispatcher });
      await waitFor(() => expect(window.location.pathname).toBe('/access-denied'));
      expect(await screen.findByRole('heading', { name: 'Access denied' })).toBeInTheDocument();
    },
  );

  it('a dispatcher cannot open the technician-only My tickets page', async () => {
    mockFetch({ 'GET /auth/me': USERS.FlightDispatcher });
    renderApp(<AppRoutes />, { route: '/my-tickets', user: USERS.FlightDispatcher });
    await waitFor(() => expect(window.location.pathname).toBe('/access-denied'));
  });
});
