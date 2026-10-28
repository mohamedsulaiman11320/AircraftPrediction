import { describe, expect, it } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AppRoutes } from '../App.jsx';
import { AuditReadinessPanel, getAuditReadiness } from '../components/AuditReadiness.jsx';
import { maintenanceEventBody, validateMaintenanceEvent } from '../pages/fleet/AircraftModals.jsx';
import { demoRequest } from '../demo/adapter.js';
import { USERS, jsonResponse, mockFetch, problem, renderApp } from '../test/utils.jsx';

const aircraft = {
  id: 'a1',
  registrationNumber: 'DEMO-A01',
  model: 'A320neo',
  manufacturer: 'Airbus',
  fleetGroup: 'Narrowbody',
  serialNumber: null,
  operationalStatus: 'Available',
  statusReason: null,
  statusChangedAtUtc: '2026-08-01T00:00:00Z',
  createdAtUtc: '2026-01-01T00:00:00Z',
  lastTelemetryAtUtc: null,
  lastInspectionAtUtc: null,
  openAnomalyCount: 0,
  activeInspectionFlagCount: 0,
  openWorkOrderCount: 0,
  latestRisk: null,
  dispatchAdvisory: { readiness: 'NoKnownRestrictions', reasons: [], disclaimer: 'Advisory only.' },
  activeInspectionFlags: [],
  recentMaintenance: [],
  recentAnomalies: [],
  latestPredictions: [],
  openWorkOrders: [],
};
const emptyPage = { items: [], page: 1, pageSize: 10, totalCount: 0, totalPages: 0 };

function detailRoutes(user, extra = {}) {
  return {
    'GET /auth/me': user,
    'GET /aircraft/a1': aircraft,
    'GET /aircraft/a1/maintenance-history': emptyPage,
    'GET /telemetry/series': { aircraftId: 'a1', fromUtc: '', toUtc: '', bucketMinutes: 60, points: [] },
    ...extra,
  };
}

const BUTTON = 'Record maintenance event';

describe('Record maintenance event', () => {
  it('is offered on the aircraft page to dispatchers; technicians cannot open aircraft pages', async () => {
    mockFetch(detailRoutes(USERS.FlightDispatcher));
    const { unmount } = renderApp(<AppRoutes />, { route: '/fleet/a1', user: USERS.FlightDispatcher });
    expect(await screen.findByRole('button', { name: BUTTON })).toBeInTheDocument();
    unmount();
    window.sessionStorage.clear();
    mockFetch(detailRoutes(USERS.MaintenanceTechnician));
    renderApp(<AppRoutes />, { route: '/fleet/a1', user: USERS.MaintenanceTechnician });
    await waitFor(() => expect(window.location.pathname).toBe('/access-denied'));
    expect(screen.queryByRole('button', { name: BUTTON })).not.toBeInTheDocument();
  });

  it('validates required description and rejects future timestamps without calling the API', async () => {
    const fetchMock = mockFetch(detailRoutes(USERS.FlightDispatcher));
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/fleet/a1', user: USERS.FlightDispatcher });
    await user.click(await screen.findByRole('button', { name: BUTTON }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/label source for future failure-prediction training/)).toBeInTheDocument();
    fireEvent.change(within(dialog).getByLabelText(/^Occurred \(UTC\)/), { target: { value: '2099-01-01T00:00' } });
    await user.click(within(dialog).getByRole('button', { name: 'Record event' }));
    expect(within(dialog).getByText('A description is required.')).toBeInTheDocument();
    expect(within(dialog).getByText('The event time cannot be in the future.')).toBeInTheDocument();
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false);
  });

  it('posts the expected payload, toasts and refreshes the maintenance history', async () => {
    let posted = null;
    const fetchMock = mockFetch(
      detailRoutes(USERS.FlightDispatcher, {
        'POST /maintenance-logs': ({ body }) => {
          posted = body;
          return jsonResponse(201, { id: 'ml1', ...body, registrationNumber: 'DEMO-A01', source: 'Manual', workOrderId: null });
        },
      }),
    );
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/fleet/a1', user: USERS.FlightDispatcher });
    await user.click(await screen.findByRole('button', { name: BUTTON }));
    const dialog = await screen.findByRole('dialog');
    await user.selectOptions(within(dialog).getByLabelText(/^Event type/), 'Unscheduled');
    fireEvent.change(within(dialog).getByLabelText(/^Occurred \(UTC\)/), { target: { value: '2026-08-14T06:30' } });
    await user.type(within(dialog).getByLabelText(/^Description/), 'Oil pressure transducer replaced');
    await user.type(within(dialog).getByLabelText(/^Component ID/), 'ENG1-OPT');
    await user.selectOptions(within(dialog).getByLabelText(/^Actual failure confirmed/), 'true');
    await user.click(within(dialog).getByLabelText('Verified against maintenance records'));
    const historyCallsBefore = fetchMock.mock.calls.filter(([u]) => String(u).includes('/maintenance-history')).length;
    await user.click(within(dialog).getByRole('button', { name: 'Record event' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(posted).toEqual({
      aircraftId: 'a1',
      eventType: 'Unscheduled',
      occurredAtUtc: '2026-08-14T06:30:00.000Z',
      description: 'Oil pressure transducer replaced',
      componentId: 'ENG1-OPT',
      actualFailureConfirmed: true,
      isVerified: true,
    });
    expect(await screen.findByText('Maintenance event recorded.')).toBeInTheDocument();
    await waitFor(() =>
      expect(fetchMock.mock.calls.filter(([u]) => String(u).includes('/maintenance-history')).length).toBeGreaterThan(historyCallsBefore),
    );
  });

  it('shows server validation errors next to the fields', async () => {
    mockFetch(
      detailRoutes(USERS.FlightDispatcher, {
        'POST /maintenance-logs': problem(400, 'validation_failed', 'Validation failed', {
          errors: { occurredAtUtc: ['Occurred time cannot be before the aircraft was registered.'] },
        }),
      }),
    );
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/fleet/a1', user: USERS.FlightDispatcher });
    await user.click(await screen.findByRole('button', { name: BUTTON }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText(/^Description/), 'Check');
    await user.click(within(dialog).getByRole('button', { name: 'Record event' }));
    const field = within(dialog).getByLabelText(/^Occurred \(UTC\)/);
    await waitFor(() => expect(field).toHaveAttribute('aria-invalid', 'true'));
    expect(within(dialog).getAllByText('Occurred time cannot be before the aircraft was registered.').length).toBeGreaterThan(0);
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });

  it('maps the tri-state failure flag and validates lengths', () => {
    const base = { eventType: 'Scheduled', occurredAt: '2026-08-01T10:00', description: 'A-check', componentId: '', actualFailureConfirmed: '', isVerified: false };
    expect(maintenanceEventBody('a1', base)).toEqual({
      aircraftId: 'a1',
      eventType: 'Scheduled',
      occurredAtUtc: '2026-08-01T10:00:00.000Z',
      description: 'A-check',
      actualFailureConfirmed: null,
      isVerified: false,
    });
    expect(maintenanceEventBody('a1', { ...base, actualFailureConfirmed: 'false' }).actualFailureConfirmed).toBe(false);
    expect(validateMaintenanceEvent(base)).toEqual({});
    expect(validateMaintenanceEvent({ ...base, description: 'x'.repeat(4001) }).description).toMatch(/4000/);
    expect(validateMaintenanceEvent({ ...base, eventType: 'Other' }).eventType).toBeDefined();
  });

  it('is supported by the demo adapter', async () => {
    const log = await demoRequest('POST', '/maintenance-logs', {
      body: { aircraftId: 'ac-01', eventType: 'Unscheduled', occurredAtUtc: new Date(Date.now() - 3600000).toISOString(), description: 'Demo', actualFailureConfirmed: null, isVerified: true },
    });
    expect(log.source).toBe('Manual');
    const history = await demoRequest('GET', '/aircraft/ac-01/maintenance-history', { query: { pageSize: 50 } });
    expect(history.items.some((m) => m.id === log.id)).toBe(true);
    const err = await demoRequest('POST', '/maintenance-logs', {
      body: { aircraftId: 'ac-01', eventType: 'Scheduled', occurredAtUtc: '2099-01-01T00:00:00Z', description: '' },
    }).catch((e) => e);
    expect(err.status).toBe(400);
    expect(Object.keys(err.errors).sort()).toEqual(['description', 'occurredAtUtc']);
  });
});

describe('dataset-audit readiness', () => {
  const snake = { status: 'Blocked', reasons: ['Too few positive examples.'], horizon_days: 30, positive_examples: 4, negative_examples: 1820, aircraft_with_positives: 3 };

  it('reads the snake_case key first, with camelCase fallback', () => {
    expect(getAuditReadiness({ failure_prediction_readiness: snake, failurePredictionReadiness: { status: 'Ready' } })).toBe(snake);
    expect(getAuditReadiness({ failurePredictionReadiness: { status: 'Ready', reasons: [] } }).status).toBe('Ready');
    expect(getAuditReadiness({ other: 1 })).toBeNull();
    expect(getAuditReadiness(null)).toBeNull();
  });

  it('renders status, reasons and the numeric fields', () => {
    render(<AuditReadinessPanel resultSummary={{ failure_prediction_readiness: snake }} />);
    expect(screen.getByText('Failure prediction readiness: Blocked')).toBeInTheDocument();
    expect(screen.getByText('Too few positive examples.')).toBeInTheDocument();
    const figures = screen.getByLabelText('Readiness figures');
    expect(within(figures).getByText('Horizon days')).toBeInTheDocument();
    expect(within(figures).getByText('Positive examples').nextSibling).toHaveTextContent('4');
    expect(within(figures).getByText('Negative examples').nextSibling).toHaveTextContent('1,820');
    expect(within(figures).getByText('Aircraft with positives')).toBeInTheDocument();
  });

  it('demo fixtures use the snake_case key', async () => {
    const audit = await demoRequest('GET', '/pipelines/dataset-audit');
    expect(audit.resultSummary.failure_prediction_readiness.status).toBe('Blocked');
    expect(audit.resultSummary.failurePredictionReadiness).toBeUndefined();
    expect(typeof audit.resultSummary.failure_prediction_readiness.positive_examples).toBe('number');
  });
});
