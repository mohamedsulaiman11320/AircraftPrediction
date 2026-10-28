import { describe, expect, it } from 'vitest';
import { demoRequest } from '../demo/adapter.js';
import { setSession } from '../auth/session.js';

async function signInDemo(username) {
  const tokens = await demoRequest('POST', '/auth/login', { body: { username, password: 'anything' } });
  setSession(tokens);
  return tokens;
}

describe('demo adapter (RBAC v2)', () => {
  it('signs in the two demo users by username and returns a TokenResponse', async () => {
    const d = await demoRequest('POST', '/auth/login', { body: { username: 'dispatcher', password: 'x' } });
    expect(d.user.roles).toEqual(['FlightDispatcher']);
    expect(typeof d.user.id).toBe('number');
    expect(d.refreshToken).toBeTruthy();
    const t = await demoRequest('POST', '/auth/login', { body: { username: 'technician', password: 'x' } });
    expect(t.user.roles).toEqual(['MaintenanceTechnician']);
    const pending = await demoRequest('POST', '/auth/login', { body: { username: 'pat.pending', password: 'x' } }).catch((e) => e);
    expect(pending.code).toBe('account_pending');
    const refreshed = await demoRequest('POST', '/auth/refresh', { body: { refreshToken: t.refreshToken } });
    expect(refreshed.user.username).toBe('technician');
  });

  it('serves the role catalogue and never fabricates probabilities', async () => {
    await signInDemo('dispatcher');
    expect(await demoRequest('GET', '/admin/roles')).toEqual([
      { roleId: 1, roleName: 'FlightDispatcher' },
      { roleId: 2, roleName: 'MaintenanceTechnician' },
      { roleId: 3, roleName: 'DataEngineer' },
    ]);
    const page = await demoRequest('GET', '/predictions', { query: { pageSize: 100 } });
    page.items.forEach((p) => {
      expect(p.probability).toBeNull();
      expect(p.probabilityValidated).toBe(false);
      expect(p.registrationNumber).toMatch(/^DEMO-A\d{2}$/);
    });
  });

  it('dispatcher: business rules, role-filtered transitions and concurrency', async () => {
    await signInDemo('dispatcher');
    const blocked = await demoRequest('POST', '/aircraft/ac-05/status', { body: { status: 'Available', reason: 'test' } }).catch((e) => e);
    expect(blocked.code).toBe('business_rule_violation');
    const wo = await demoRequest('GET', '/work-orders/wo-5');
    expect(wo.allowedTransitions).toEqual(['assign', 'cancel']);
    const assigned = await demoRequest('POST', '/work-orders/wo-5/assign', { body: { technicianId: 2 } });
    expect(assigned.status).toBe('Assigned');
    expect(assigned.allowedTransitions).toEqual(['assign', 'cancel']); // start-inspection is the technician's
    const conflict = await demoRequest('PATCH', '/work-orders/wo-5', { body: { concurrencyStamp: 'stale', title: 'x' } }).catch((e) => e);
    expect(conflict.status).toBe(409);
  });

  it('technician: only own tickets with embedded diagnosis; everything else is 403', async () => {
    await signInDemo('technician');
    const list = await demoRequest('GET', '/work-orders', { query: { pageSize: 100 } });
    expect(list.items.length).toBeGreaterThan(0);
    list.items.forEach((w) => expect(w.assignedTechnicianId).toBe(2));
    const ticket = await demoRequest('GET', `/work-orders/${list.items[0].id}`);
    expect(ticket.aircraft.registrationNumber).toMatch(/^DEMO-A/);
    expect(ticket).toHaveProperty('prediction');
    const dash = await demoRequest('GET', '/dashboards/technician');
    expect(Object.keys(dash.counts).sort()).toEqual(['assignedOpen', 'awaitingClosure', 'inInspection', 'overdue']);
    expect((await demoRequest('GET', '/aircraft').catch((e) => e)).status).toBe(403);
    expect((await demoRequest('GET', '/work-orders/wo-1').catch((e) => e)).status).toBe(404);
  });

  it('dataengineer: data/model endpoints, raw JSON upload through ETL stages with retry; operations are 403', async () => {
    const t = await signInDemo('dataengineer');
    expect(t.user.roles).toEqual(['DataEngineer']);
    expect((await demoRequest('GET', '/aircraft')).items.length).toBeGreaterThan(0);
    for (const path of ['/work-orders', '/aircraft/ac-01', '/admin/users', '/audit/actions', '/dashboards/dispatcher']) {
      expect((await demoRequest('GET', path).catch((e) => e)).status).toBe(403);
    }
    expect((await demoRequest('POST', '/predictions/pr-1/review', { body: {} }).catch((e) => e)).status).toBe(403);
    expect((await demoRequest('POST', '/models/x/approve', { body: {} }).catch((e) => e)).status).toBe(403);
    const rec = (await demoRequest('GET', '/telemetry', { query: { aircraftId: 'ac-01', pageSize: 5 } })).items;
    expect(rec.some((r) => r.sourceRecordId && typeof r.vibrationIndex === 'number' && r.sourceStatusFlag)).toBe(true);
    const fd = new FormData();
    fd.append('file', new File(['[]'], 'etl_fail_demo.json'));
    const accepted = await demoRequest('POST', '/telemetry/raw-uploads', { formData: fd });
    expect(accepted.status).toBe('Queued');
    const seen = [];
    for (let i = 0; i < 3; i++) seen.push((await demoRequest('GET', `/telemetry/imports/${accepted.id}`)).status);
    expect(seen).toEqual(['Processing', 'Processing', 'Failed']);
    const retried = await demoRequest('POST', `/telemetry/imports/${accepted.id}/retry`);
    expect(retried).toMatchObject({ status: 'Queued', source: 'RawJson' });
    expect((await demoRequest('POST', '/telemetry/raw-uploads', { formData: fd }).catch((e) => e)).status).toBe(409);
  });
});
