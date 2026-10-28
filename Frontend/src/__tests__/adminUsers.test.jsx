import { describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AppRoutes } from '../App.jsx';
import { USERS, mockFetch, renderApp } from '../test/utils.jsx';

// A catalogue the UI has never seen proves role pickers are driven by GET /admin/roles.
const ROLE_CATALOGUE = [
  { roleId: 1, roleName: 'FlightDispatcher' },
  { roleId: 2, roleName: 'MaintenanceTechnician' },
  { roleId: 7, roleName: 'QualityInspector' },
];

const pendingUser = {
  id: 42,
  username: 'new.hire',
  email: 'new@example.com',
  displayName: 'New Hire',
  roles: [],
  status: 'Pending',
  createdAtUtc: '2026-10-20T08:00:00Z',
  lastLoginAtUtc: null,
  approvedAtUtc: null,
  failedLoginCount: 0,
  lockoutEndUtc: null,
  disabledReason: null,
};
const activeTech = { ...pendingUser, id: 43, username: 'tom.tech', displayName: 'Tom Tech', roles: ['MaintenanceTechnician'], status: 'Active' };

function routes(extra = {}) {
  return {
    'GET /auth/me': USERS.DataEngineer,
    'GET /admin/roles': ROLE_CATALOGUE,
    'GET /admin/users': { items: [pendingUser, activeTech], page: 1, pageSize: 25, totalCount: 2, totalPages: 1 },
    ...extra,
  };
}

// User administration is the Data Engineer's (API: Policies.DataEngineer); the dispatcher is denied below.
describe('admin users (roles from the API)', () => {
  it('lists users with their roles array and offers the role filter from /admin/roles', async () => {
    const fetchMock = mockFetch(routes());
    renderApp(<AppRoutes />, { route: '/admin/users', user: USERS.DataEngineer });
    expect(await screen.findByText('New Hire')).toBeInTheDocument();
    expect(screen.getByText('tom.tech')).toBeInTheDocument();
    const roleFilter = screen.getByLabelText('Role');
    await waitFor(() => expect(within(roleFilter).getByRole('option', { name: 'Quality Inspector' })).toBeInTheDocument());
    expect(within(roleFilter).getAllByRole('option').map((o) => o.textContent)).toEqual(['All', 'Flight Dispatcher', 'Maintenance Technician', 'Quality Inspector']);
    expect(fetchMock.mock.calls.some(([u]) => String(u).endsWith('/api/v1/admin/roles'))).toBe(true);
  });

  it('approves with a multi-select of roles and posts { roles: [...] }', async () => {
    let posted = null;
    mockFetch(
      routes({
        'POST /admin/users/42/approve': ({ body }) => {
          posted = body;
          return { ...pendingUser, status: 'Active', roles: body.roles };
        },
      }),
    );
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/admin/users', user: USERS.DataEngineer });
    await user.click(await screen.findByRole('button', { name: 'Approve' }));
    const dialog = await screen.findByRole('dialog');
    // checkboxes come from the API catalogue
    const boxes = within(dialog).getAllByRole('checkbox');
    expect(boxes.map((b) => b.closest('label').textContent)).toEqual(['Flight Dispatcher', 'Maintenance Technician', 'Quality Inspector']);
    // at least one role is required
    await user.click(within(dialog).getByRole('button', { name: 'Approve' }));
    expect(within(dialog).getByText('Choose at least one role.')).toBeInTheDocument();
    await user.click(within(dialog).getByLabelText('Maintenance Technician'));
    await user.click(within(dialog).getByLabelText('Flight Dispatcher'));
    await user.click(within(dialog).getByRole('button', { name: 'Approve' }));
    await waitFor(() => expect(posted).toEqual({ roles: ['MaintenanceTechnician', 'FlightDispatcher'] }));
  });

  it('changes roles with PUT /admin/users/{id}/roles', async () => {
    let put = null;
    mockFetch(
      routes({
        'PUT /admin/users/43/roles': ({ body }) => {
          put = body;
          return { ...activeTech, roles: body.roles };
        },
      }),
    );
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/admin/users', user: USERS.DataEngineer });
    await user.click(await screen.findByRole('button', { name: 'Change roles' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByLabelText('Maintenance Technician')).toBeChecked();
    await user.click(within(dialog).getByLabelText('Quality Inspector'));
    await user.click(within(dialog).getByRole('button', { name: 'Save roles' }));
    await waitFor(() => expect(put).toEqual({ roles: ['MaintenanceTechnician', 'QualityInspector'] }));
  });
});

describe('admin users access', () => {
  it('sends a dispatcher to access denied: user administration is not theirs', async () => {
    mockFetch({ 'GET /auth/me': USERS.FlightDispatcher });
    renderApp(<AppRoutes />, { route: '/admin/users', user: USERS.FlightDispatcher });
    await waitFor(() => expect(window.location.pathname).toBe('/access-denied'));
  });
});
