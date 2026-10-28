import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { WorkOrderActionButtons } from '../pages/workorders/WorkOrderActions.jsx';
import { allowedActions } from '../pages/workorders/transitions.js';
import { AppRoutes } from '../App.jsx';
import { NOT_VALIDATED_TEXT } from '../components/Probability.jsx';
import { USERS, mockFetch, problem, renderApp } from '../test/utils.jsx';

afterEach(() => vi.unstubAllGlobals());

const aircraftSummary = {
  id: 'a1',
  registrationNumber: 'DEMO-A01',
  model: 'A320neo',
  manufacturer: 'Airbus',
  fleetGroup: 'Narrowbody',
  operationalStatus: 'UnderMaintenance',
  statusReason: 'Oil pressure investigation',
  lastTelemetryAtUtc: '2026-10-20T08:00:00Z',
  lastInspectionAtUtc: null,
  openAnomalyCount: 1,
  activeInspectionFlagCount: 1,
  openWorkOrderCount: 1,
  latestRisk: { riskCategory: 'Elevated', basis: 'DescriptiveIndicator', predictionTimestampUtc: '2026-10-20T06:00:00Z', horizonDays: 30 },
};

const anomaly = {
  id: 'an1',
  aircraftId: 'a1',
  registrationNumber: 'DEMO-A01',
  sensorId: 'ENG1-OIL',
  observedAtUtc: '2026-10-19T10:00:00Z',
  anomalyScore: 0.8123,
  severity: 'High',
  modelName: 'oil-iforest',
  modelVersion: '1.2.0',
  investigationStatus: 'Acknowledged',
  featureValues: { temperature_c: 121.5, pressure_kpa: 288.2 },
  scoreNote: 'Higher means more unusual than the training data. This is not a probability of failure.',
};

const prediction = {
  id: 'p1',
  aircraftId: 'a1',
  registrationNumber: 'DEMO-A01',
  predictionTimestampUtc: '2026-10-20T06:00:00Z',
  horizonDays: 30,
  riskCategory: 'Elevated',
  basis: 'DescriptiveIndicator',
  probability: 0.37,
  probabilityValidated: false,
  predictionState: 'Scored',
  featureCutoffUtc: '2026-10-20T05:00:00Z',
  modelName: null,
  modelVersion: null,
  disclaimer: 'Advisory only; not an airworthiness determination.',
};

const baseWo = {
  id: 'wo1',
  workOrderNumber: 'WO-2026-000123',
  aircraftId: 'a1',
  registrationNumber: 'DEMO-A01',
  componentId: null,
  anomalyId: 'an1',
  predictionId: 'p1',
  priority: 'High',
  title: 'Inspect oil system',
  description: 'Check oil pressure readings.',
  status: 'Open',
  assignedTechnicianId: null,
  assignedTechnicianName: null,
  createdBy: 'Fiona Dispatch',
  createdAtUtc: '2026-08-10T08:00:00Z',
  dueAtUtc: null,
  isOverdue: false,
  inspectionFindings: null,
  correctiveAction: null,
  actualFailureConfirmed: null,
  resolutionNotes: null,
  resolvedAtUtc: null,
  closedAtUtc: null,
  updatedAtUtc: '2026-08-10T08:00:00Z',
  allowedTransitions: ['assign', 'cancel'],
  concurrencyStamp: 'stamp-1',
  history: [{ id: 1, userId: 1, userName: 'Fiona Dispatch', action: 'WorkOrderCreated', entityType: 'WorkOrder', entityId: 'wo1', details: null, occurredAtUtc: '2026-08-10T08:00:00Z', correlationId: null }],
  aircraft: aircraftSummary,
  anomaly,
  prediction,
};

const technicianWo = {
  ...baseWo,
  status: 'Assigned',
  assignedTechnicianId: 2,
  assignedTechnicianName: 'Tom Tech',
  allowedTransitions: ['start-inspection'],
};

const groupButtons = () =>
  within(screen.getByRole('group', { name: 'Work order actions' }))
    .getAllByRole('button')
    .map((b) => b.textContent);

describe('work order action buttons', () => {
  it('renders exactly the server-allowed transitions (+ edit for dispatchers)', () => {
    render(<WorkOrderActionButtons workOrder={baseWo} canAct canEdit onAction={() => {}} />);
    expect(groupButtons()).toEqual(['Assign technician', 'Cancel work order', 'Edit details']);
  });

  it('never offers Edit details without canEdit (technicians)', () => {
    render(<WorkOrderActionButtons workOrder={{ ...baseWo, status: 'InInspection', allowedTransitions: ['inspection', 'resolve'] }} canAct onAction={() => {}} />);
    expect(groupButtons()).toEqual(['Record inspection findings', 'Resolve']);
  });

  it('maps the server transition names', () => {
    const names = ['assign', 'start-inspection', 'inspection', 'resolve', 'reopen', 'close', 'cancel'];
    expect([...allowedActions({ status: 'Open', allowedTransitions: names })]).toEqual([
      'assign',
      'startInspection',
      'recordInspection',
      'resolve',
      'reopen',
      'close',
      'cancel',
    ]);
  });

  it('shows nothing when no transitions are allowed and editing is not permitted', () => {
    const { container, rerender } = render(<WorkOrderActionButtons workOrder={{ ...baseWo, status: 'Closed', allowedTransitions: [] }} canAct canEdit onAction={() => {}} />);
    expect(container).toBeEmptyDOMElement();
    rerender(<WorkOrderActionButtons workOrder={baseWo} canAct={false} onAction={() => {}} />);
    expect(container).toBeEmptyDOMElement();
  });

  it('calls onAction with the action key', async () => {
    const onAction = vi.fn();
    const user = userEvent.setup();
    render(<WorkOrderActionButtons workOrder={baseWo} canAct onAction={onAction} />);
    await user.click(screen.getByRole('button', { name: 'Cancel work order' }));
    expect(onAction).toHaveBeenCalledWith('cancel');
  });
});

describe('work orders list - status labels', () => {
  it('shows work-order display labels while filtering by the unchanged backend enum value', async () => {
    const statuses = [];
    mockFetch({
      'GET /auth/me': USERS.FlightDispatcher,
      'GET /aircraft': { items: [], page: 1, pageSize: 100, totalCount: 0, totalPages: 0 },
      'GET /work-orders': ({ url }) => {
        statuses.push(url.searchParams.get('status'));
        const all = [
          { ...baseWo, id: 'w1', workOrderNumber: 'WO-1', status: 'Resolved' },
          { ...baseWo, id: 'w2', workOrderNumber: 'WO-2', status: 'Closed' },
        ];
        const s = url.searchParams.get('status');
        const items = s ? all.filter((w) => w.status === s) : all;
        return { items, page: 1, pageSize: 25, totalCount: items.length, totalPages: 1 };
      },
    });
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/work-orders', user: USERS.FlightDispatcher });
    const table = await screen.findByRole('table', { name: /Work orders/ });
    expect(within(table).getByText('REPORTED')).toBeInTheDocument();
    expect(within(table).getByText('COMPLETED')).toBeInTheDocument();

    const select = screen.getByLabelText('Status');
    expect(within(select).getAllByRole('option').map((o) => o.textContent)).toEqual(expect.arrayContaining(['NEW', 'ASSIGNED', 'IN_PROGRESS', 'REPORTED', 'COMPLETED', 'CANCELLED']));
    await user.selectOptions(select, 'COMPLETED');
    await waitFor(() => expect(statuses.at(-1)).toBe('Closed'));
    await waitFor(() => expect(within(screen.getByRole('table', { name: /Work orders/ })).queryByText('REPORTED')).not.toBeInTheDocument());
  });
});

describe('work order detail - dispatcher', () => {
  it('assigns a technician (numeric id, picker from /technicians) and reloads', async () => {
    let wo = { ...baseWo };
    mockFetch({
      'GET /auth/me': USERS.FlightDispatcher,
      'GET /work-orders/wo1': () => wo,
      'GET /technicians': [{ id: 2, displayName: 'Tom Tech', username: 'tom.tech' }],
      'POST /work-orders/wo1/assign': ({ body }) => {
        expect(body).toEqual({ technicianId: 2 });
        wo = { ...wo, status: 'Assigned', assignedTechnicianId: 2, assignedTechnicianName: 'Tom Tech', allowedTransitions: ['assign', 'cancel'] };
        return wo;
      },
    });
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/work-orders/wo1', user: USERS.FlightDispatcher });
    await user.click(await screen.findByRole('button', { name: 'Assign technician' }));
    const dialog = await screen.findByRole('dialog');
    await waitFor(() => expect(within(dialog).getByRole('option', { name: 'Tom Tech (tom.tech)' })).toBeInTheDocument());
    await user.selectOptions(within(dialog).getByLabelText(/^Technician/), '2');
    await user.click(within(dialog).getByRole('button', { name: 'Assign technician' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(await screen.findByText('Tom Tech')).toBeInTheDocument();
  });

  it('sends the concurrencyStamp on PATCH and shows 409 conflicts', async () => {
    mockFetch({
      'GET /auth/me': USERS.FlightDispatcher,
      'GET /work-orders/wo1': baseWo,
      'PATCH /work-orders/wo1': ({ body }) => {
        expect(body.concurrencyStamp).toBe('stamp-1');
        expect(body.title).toBe('Inspect oil system now');
        return problem(409, 'concurrency_conflict', 'Conflict');
      },
    });
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/work-orders/wo1', user: USERS.FlightDispatcher });
    await user.click(await screen.findByRole('button', { name: 'Edit details' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText(/^Title/), ' now');
    await user.click(within(dialog).getByRole('button', { name: 'Save changes' }));
    expect(await within(screen.getByRole('dialog')).findByText('This work order was changed by someone else')).toBeInTheDocument();
  });
});

describe('ticket detail - technician', () => {
  it('shows the embedded aircraft summary and AI diagnosis, hides an unvalidated probability', async () => {
    mockFetch({ 'GET /auth/me': USERS.MaintenanceTechnician, 'GET /work-orders/wo1': technicianWo });
    renderApp(<AppRoutes />, { route: '/work-orders/wo1', user: USERS.MaintenanceTechnician });
    expect(await screen.findByRole('heading', { name: /WO-2026-000123/ })).toBeInTheDocument();

    // aircraft summary embedded (no link into fleet screens for technicians)
    expect(screen.getByText('Oil pressure investigation')).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'DEMO-A01' })).not.toBeInTheDocument();

    // anomaly: score with its note - never presented as a probability
    expect(screen.getByText('0.8123')).toBeInTheDocument();
    expect(screen.getByText('Anomaly score (not a probability)')).toBeInTheDocument();
    expect(screen.getByText('temperature_c')).toBeInTheDocument();

    // prediction: probability hidden because probabilityValidated is false
    expect(screen.getByTestId('ticket-probability')).toHaveTextContent(NOT_VALIDATED_TEXT);
    expect(screen.queryByText(/37/)).not.toBeInTheDocument();
    expect(screen.getByText(/not an airworthiness determination/)).toBeInTheDocument();
    expect(screen.getByText('Scored')).toBeInTheDocument();

    // buttons come from allowedTransitions; no dispatcher-only edit
    expect(groupButtons()).toEqual(['Start inspection']);
    expect(screen.queryByRole('button', { name: 'Edit details' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Log maintenance' })).toBeInTheDocument();
    // only My tickets in the navigation
    const nav = screen.getByRole('navigation', { name: 'Primary' });
    expect(within(nav).getAllByRole('link').map((l) => l.textContent)).toEqual(['My tickets']);
  });

  it('shows a validated probability', async () => {
    mockFetch({
      'GET /auth/me': USERS.MaintenanceTechnician,
      'GET /work-orders/wo1': { ...technicianWo, prediction: { ...prediction, basis: 'Model', probability: 0.123, probabilityValidated: true } },
    });
    renderApp(<AppRoutes />, { route: '/work-orders/wo1', user: USERS.MaintenanceTechnician });
    expect(await screen.findByTestId('ticket-probability')).toHaveTextContent('12.3%');
  });

  it('starts the inspection and logs maintenance for the ticket aircraft', async () => {
    let wo = { ...technicianWo };
    let logged = null;
    mockFetch({
      'GET /auth/me': USERS.MaintenanceTechnician,
      'GET /work-orders/wo1': () => wo,
      'POST /work-orders/wo1/start-inspection': () => {
        wo = { ...wo, status: 'InInspection', allowedTransitions: ['inspection', 'resolve'] };
        return wo;
      },
      'POST /maintenance-logs': ({ body }) => {
        logged = body;
        return { id: 'ml1', ...body };
      },
    });
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/work-orders/wo1', user: USERS.MaintenanceTechnician });
    await user.click(await screen.findByRole('button', { name: 'Start inspection' }));
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Start inspection' }));
    await waitFor(() => expect(groupButtons()).toEqual(['Record inspection findings', 'Resolve']));

    await user.click(screen.getByRole('button', { name: 'Log maintenance' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText(/^Description/), 'Borescope inspection, no damage');
    await user.click(within(dialog).getByRole('button', { name: 'Record event' }));
    await waitFor(() => expect(logged).not.toBeNull());
    expect(logged.aircraftId).toBe('a1');
    expect(logged.eventType).toBe('Inspection');
  });

  it('a ticket that is not the technician’s shows the server 404', async () => {
    mockFetch({ 'GET /auth/me': USERS.MaintenanceTechnician, 'GET /work-orders/wo9': problem(404, 'not_found', 'Not found') });
    renderApp(<AppRoutes />, { route: '/work-orders/wo9', user: USERS.MaintenanceTechnician });
    expect(await screen.findByText('Could not load work order')).toBeInTheDocument();
  });
});
