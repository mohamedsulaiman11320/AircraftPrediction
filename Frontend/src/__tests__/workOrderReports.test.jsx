import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AppRoutes } from '../App.jsx';
import { USERS, mockFetch, problem, renderApp } from '../test/utils.jsx';
import { countByStatus, sortMyTickets } from '../pages/workorders/ticketOrder.js';

afterEach(() => vi.unstubAllGlobals());

const ACTION_CODES = [
  { code: 'ENG_INSP', description: 'Engine Inspection' },
  { code: 'OIL_REPL', description: 'Oil Filter Replacement' },
];

const DIAGNOSIS_TEXT = 'DIAGNOSTIC ASSESSMENT - N120UA (deterministic rules; NOT a confirmed fault)\r\n\r\nTrigger\r\n- Oil pressure below 35 PSI.\r\n<b>not html</b>';

const thresholdAnomaly = {
  id: 267,
  aircraftId: 'a1',
  registrationNumber: 'N120UA',
  sensorId: null,
  observedAtUtc: '2026-10-26T18:00:00Z',
  lastObservedAtUtc: '2026-10-26T20:00:00Z',
  anomalyScore: null,
  severity: 'High',
  modelName: 'Unknown',
  modelVersion: 'Unknown',
  modelVersionId: null,
  investigationStatus: 'New',
  scoreNote: 'Raised by an approved BR-01 threshold rule from measured telemetry (see Evidence). It has no score and is not a probability of failure.',
  source: 'ThresholdRule',
  ruleCodes: 'EngineTemperatureHighC,OilPressureLowPsi',
  evidence: {
    latest: { oilPressurePsi: 31.100006988277197, engineTemperatureC: 369.7 },
    breaches: [{ code: 'OilPressureLowPsi', limit: 35, unit: 'psi', comparison: 'LessThan' }],
    ruleReference: 'BR-01',
    readingsInWindow: 24,
  },
  featureValues: null,
};

const aircraft = {
  id: 'a1',
  registrationNumber: 'N120UA',
  model: 'A320',
  manufacturer: 'Airbus',
  fleetGroup: 'Narrowbody',
  operationalStatus: 'Available',
  activeInspectionFlagCount: 0,
  latestRisk: null,
};

const autoWo = {
  id: 'wo1',
  workOrderNumber: 'WO-2026-000001',
  aircraftId: 'a1',
  registrationNumber: 'N120UA',
  anomalyId: 267,
  predictionId: null,
  priority: 'High',
  title: 'BR-01 threshold breach',
  description: 'Raised automatically.',
  status: 'Open',
  assignedTechnicianId: null,
  assignedTechnicianName: null,
  createdBy: 'Flight Dispatcher',
  createdAtUtc: '2026-10-27T14:55:05Z',
  updatedAtUtc: '2026-10-27T14:55:05Z',
  isOverdue: false,
  allowedTransitions: ['assign', 'cancel'],
  concurrencyStamp: 's1',
  source: 'Automatic',
  diagnosisStatus: 'Generated',
  diagnosisText: DIAGNOSIS_TEXT,
  diagnosisMethod: 'deterministic-rules',
  diagnosisRuleVersion: 'health_threshold BR-01/BR-04; diagnosis-v1',
  diagnosisGeneratedAtUtc: '2026-10-27T14:55:06Z',
  diagnosisError: null,
  dispatcherReviewNotes: null,
  reports: [],
  history: [],
  aircraft,
  anomaly: thresholdAnomaly,
  prediction: null,
};

const twoReports = [
  {
    sequence: 1,
    technicianUserId: 2,
    technicianName: 'Tom Tech',
    submittedAtUtc: '2026-10-27T09:00:00Z',
    actionCode: 'ENG_INSP',
    inspectionFindings: 'Low oil pressure confirmed on ground run.',
    correctiveAction: 'Inspected regulator.',
    resolutionNotes: 'First report notes.',
    actualFailureConfirmed: false,
  },
  {
    sequence: 2,
    technicianUserId: 2,
    technicianName: 'Tom Tech',
    submittedAtUtc: '2026-10-27T12:00:00Z',
    actionCode: 'OIL_REPL',
    inspectionFindings: 'Filter clogged.',
    correctiveAction: 'Replaced oil filter.',
    resolutionNotes: 'Second report notes.',
    actualFailureConfirmed: true,
  },
];

const region = (name) => screen.getByRole('region', { name });

describe('work-order detail - diagnosis and evidence', () => {
  it('shows the diagnosis text (as plain text) with method, rule version and the not-a-confirmed-fault caption', async () => {
    mockFetch({ 'GET /auth/me': USERS.FlightDispatcher, 'GET /work-orders/wo1': autoWo });
    renderApp(<AppRoutes />, { route: '/work-orders/wo1', user: USERS.FlightDispatcher });
    await screen.findByRole('heading', { name: /WO-2026-000001/ });
    const card = region('Diagnosis');
    expect(within(card).getByText(/Deterministic rules — not a confirmed fault/)).toBeInTheDocument();
    expect(within(card).getByTestId('diagnosis-method')).toHaveTextContent('deterministic-rules');
    expect(within(card).getByText('health_threshold BR-01/BR-04; diagnosis-v1')).toBeInTheDocument();
    const text = within(card).getByTestId('diagnosis-text');
    expect(text.textContent).toContain('Oil pressure below 35 PSI.');
    // Rendered as text, never as HTML.
    expect(text.textContent).toContain('<b>not html</b>');
    expect(text.querySelector('b')).toBeNull();
    // Source + diagnosis indicators in the details.
    expect(screen.getByText('Automatic')).toBeInTheDocument();
    expect(screen.getByText('Diagnosis ready')).toBeInTheDocument();
  });

  it('shows a failed diagnosis as an error and says the ticket is unaffected', async () => {
    mockFetch({
      'GET /auth/me': USERS.FlightDispatcher,
      'GET /work-orders/wo1': { ...autoWo, diagnosisStatus: 'Failed', diagnosisText: null, diagnosisError: 'Telemetry window could not be read.' },
    });
    renderApp(<AppRoutes />, { route: '/work-orders/wo1', user: USERS.FlightDispatcher });
    await screen.findByRole('heading', { name: /WO-2026-000001/ });
    const alert = within(region('Diagnosis')).getByRole('alert');
    expect(alert).toHaveTextContent('Telemetry window could not be read.');
    expect(alert).toHaveTextContent(/work order itself is unaffected/);
    expect(screen.queryByTestId('diagnosis-text')).not.toBeInTheDocument();
  });

  it('says so when no diagnosis was generated', async () => {
    mockFetch({ 'GET /auth/me': USERS.FlightDispatcher, 'GET /work-orders/wo1': { ...autoWo, diagnosisStatus: 'NotGenerated', diagnosisText: null } });
    renderApp(<AppRoutes />, { route: '/work-orders/wo1', user: USERS.FlightDispatcher });
    await screen.findByRole('heading', { name: /WO-2026-000001/ });
    expect(within(region('Diagnosis')).getByText('No diagnosis has been generated for this work order.')).toBeInTheDocument();
  });

  it('shows the anomaly evidence: source label, rule codes, readable key/values, times, and no score as a probability', async () => {
    mockFetch({ 'GET /auth/me': USERS.MaintenanceTechnician, 'GET /work-orders/wo1': { ...autoWo, status: 'Assigned', allowedTransitions: ['start-inspection'] } });
    renderApp(<AppRoutes />, { route: '/work-orders/wo1', user: USERS.MaintenanceTechnician });
    await screen.findByRole('heading', { name: /WO-2026-000001/ });
    const ev = region('Evidence');
    expect(within(ev).getByText('Threshold rule')).toBeInTheDocument();
    const codes = within(ev).getByTestId('evidence-rule-codes');
    expect(within(codes).getByText('EngineTemperatureHighC')).toBeInTheDocument();
    expect(within(codes).getByText('OilPressureLowPsi')).toBeInTheDocument();
    const kv = within(ev).getByTestId('anomaly-evidence');
    expect(within(kv).getByText('oilPressurePsi')).toBeInTheDocument();
    expect(within(kv).getByText('31.1')).toBeInTheDocument();
    expect(within(kv).getByText('readingsInWindow')).toBeInTheDocument();
    expect(within(kv).getByText('BR-01')).toBeInTheDocument();
    expect(within(ev).getByText('Last observed')).toBeInTheDocument();
    expect(within(ev).queryByText('About this evidence')).not.toBeInTheDocument();      // the explanatory box was removed (2026-10-28)
    expect(within(ev).queryByText(/Anomaly score/)).not.toBeInTheDocument();
    expect(within(ev).queryByText('%', { exact: false })).not.toBeInTheDocument();
  });
});

describe('work-order detail - technician report history and dispatcher review', () => {
  it('lists every report version with its action code and description, read-only', async () => {
    mockFetch({
      'GET /auth/me': USERS.FlightDispatcher,
      'GET /work-orders/wo1': { ...autoWo, status: 'Resolved', allowedTransitions: ['reopen', 'close'], reports: [twoReports[1], twoReports[0]] },
      'GET /maintenance-action-codes': ACTION_CODES,
    });
    renderApp(<AppRoutes />, { route: '/work-orders/wo1', user: USERS.FlightDispatcher });
    await screen.findByRole('heading', { name: /WO-2026-000001/ });
    const reports = within(region('Technician reports')).getAllByTestId('technician-report');
    expect(reports).toHaveLength(2);
    expect(within(reports[0]).getByRole('heading', { name: 'Report 1' })).toBeInTheDocument();
    expect(within(reports[1]).getByRole('heading', { name: 'Report 2' })).toBeInTheDocument();
    await waitFor(() => expect(reports[0]).toHaveTextContent('ENG_INSP — Engine Inspection'));
    expect(reports[1]).toHaveTextContent('OIL_REPL — Oil Filter Replacement');
    expect(within(reports[0]).getByText('Inspected regulator.')).toBeInTheDocument();
    expect(within(reports[1]).getByText('Replaced oil filter.')).toBeInTheDocument();
    expect(within(reports[1]).getByText('Second report notes.')).toBeInTheDocument();
    expect(within(reports[0]).getAllByText('Tom Tech')).toHaveLength(1);
    expect(within(region('Technician reports')).queryByRole('textbox')).not.toBeInTheDocument();
  });

  it('shows the dispatcher review notes in their own block, separate from the technician report', async () => {
    mockFetch({
      'GET /auth/me': USERS.FlightDispatcher,
      'GET /work-orders/wo1': { ...autoWo, status: 'Closed', allowedTransitions: [], reports: [twoReports[0]], dispatcherReviewNotes: 'Accepted after borescope review.' },
      'GET /maintenance-action-codes': ACTION_CODES,
    });
    renderApp(<AppRoutes />, { route: '/work-orders/wo1', user: USERS.FlightDispatcher });
    await screen.findByRole('heading', { name: /WO-2026-000001/ });
    const review = region('Dispatcher review');
    expect(within(review).getByTestId('dispatcher-review-notes')).toHaveTextContent('Accepted after borescope review.');
    expect(within(region('Technician reports')).queryByText('Accepted after borescope review.')).not.toBeInTheDocument();
    expect(within(review).queryByText('First report notes.')).not.toBeInTheDocument();
  });

  it('has no dispatcher review block when there are no review notes', async () => {
    mockFetch({ 'GET /auth/me': USERS.FlightDispatcher, 'GET /work-orders/wo1': autoWo });
    renderApp(<AppRoutes />, { route: '/work-orders/wo1', user: USERS.FlightDispatcher });
    await screen.findByRole('heading', { name: /WO-2026-000001/ });
    expect(screen.queryByRole('region', { name: 'Dispatcher review' })).not.toBeInTheDocument();
    expect(within(region('Technician reports')).getByText('No technician report has been submitted yet.')).toBeInTheDocument();
  });
});

describe('technician report form (resolve)', () => {
  const inInspection = { ...autoWo, status: 'InInspection', assignedTechnicianId: 2, assignedTechnicianName: 'Tom Tech', inspectionFindings: 'Filter clogged.', allowedTransitions: ['inspection', 'resolve'] };

  async function openResolve(routes) {
    let posted = null;
    mockFetch({
      'GET /auth/me': USERS.MaintenanceTechnician,
      'GET /work-orders/wo1': () => (posted ? { ...inInspection, status: 'Resolved', allowedTransitions: [], reports: [{ ...twoReports[0], actionCode: posted.actionCode }] } : inInspection),
      'GET /maintenance-action-codes': ACTION_CODES,
      'POST /work-orders/wo1/resolve': ({ body }) => {
        posted = body;
        return { ...inInspection, status: 'Resolved' };
      },
      ...routes,
    });
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/work-orders/wo1', user: USERS.MaintenanceTechnician });
    await user.click(await screen.findByRole('button', { name: 'Resolve' }));
    const dialog = await screen.findByRole('dialog');
    return { user, dialog, getPosted: () => posted };
  }

  it('requires an action code from GET /maintenance-action-codes and notes of at least 10 characters, then posts { actionCode }', async () => {
    const { user, dialog, getPosted } = await openResolve();
    const select = within(dialog).getByLabelText(/^Action code/);
    await waitFor(() => expect(within(select).getByRole('option', { name: 'OIL_REPL — Oil Filter Replacement' })).toBeInTheDocument());

    await user.click(within(dialog).getByRole('button', { name: 'Resolve' }));
    expect(within(dialog).getByText('Choose the maintenance action code.')).toBeInTheDocument();
    expect(getPosted()).toBeNull();

    await user.type(within(dialog).getByLabelText(/^Work performed/), 'Replaced oil filter.');
    await user.click(within(dialog).getByLabelText(/^No/));
    await user.type(within(dialog).getByLabelText(/^Resolution notes/), 'too short');
    await user.click(within(dialog).getByRole('button', { name: 'Resolve' }));
    expect(within(dialog).getByText('Resolution notes must be at least 10 characters.')).toBeInTheDocument();
    expect(getPosted()).toBeNull();

    await user.selectOptions(select, 'OIL_REPL');
    await user.type(within(dialog).getByLabelText(/^Resolution notes/), ' - pressure normal on ground run');
    await user.click(within(dialog).getByRole('button', { name: 'Resolve' }));
    await waitFor(() => expect(getPosted()).not.toBeNull());
    expect(getPosted()).toEqual({
      actionCode: 'OIL_REPL',
      correctiveAction: 'Replaced oil filter.',
      actualFailureConfirmed: false,
      resolutionNotes: 'too short - pressure normal on ground run',
    });
    // Refreshed after success: the new report appears in the history.
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(await within(region('Technician reports')).findByText(/OIL_REPL/)).toBeInTheDocument();
  });

  it('shows server validation errors (ProblemDetails errors) next to the field', async () => {
    const { user, dialog } = await openResolve({
      'POST /work-orders/wo1/resolve': problem(400, 'validation_failed', 'Validation failed', { errors: { actionCode: ['Action code ENG_INSP is not active.'] } }),
    });
    const select = within(dialog).getByLabelText(/^Action code/);
    await waitFor(() => expect(within(select).getByRole('option', { name: 'ENG_INSP — Engine Inspection' })).toBeInTheDocument());
    await user.selectOptions(select, 'ENG_INSP');
    await user.type(within(dialog).getByLabelText(/^Work performed/), 'Inspected engine.');
    await user.click(within(dialog).getByLabelText(/^Yes/));
    await user.type(within(dialog).getByLabelText(/^Resolution notes/), 'Long enough notes here.');
    await user.click(within(dialog).getByRole('button', { name: 'Resolve' }));
    await waitFor(() => expect(within(dialog).getAllByText('Action code ENG_INSP is not active.').length).toBeGreaterThan(0));
    expect(select).toHaveAttribute('aria-invalid', 'true');
  });
});

describe('dispatcher review actions', () => {
  const reported = { ...autoWo, status: 'Resolved', assignedTechnicianId: 2, assignedTechnicianName: 'Tom Tech', allowedTransitions: ['reopen', 'close'], reports: [twoReports[0]] };

  it('"Send back to technician" requires a reason, then posts it', async () => {
    let body = null;
    mockFetch({
      'GET /auth/me': USERS.FlightDispatcher,
      'GET /work-orders/wo1': () => (body ? { ...reported, status: 'Assigned', allowedTransitions: ['assign', 'cancel'] } : reported),
      'GET /maintenance-action-codes': ACTION_CODES,
      'POST /work-orders/wo1/reopen': ({ body: b }) => {
        body = b;
        return { ...reported, status: 'Assigned' };
      },
    });
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/work-orders/wo1', user: USERS.FlightDispatcher });
    await user.click(await screen.findByRole('button', { name: 'Send back to technician' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Send back to technician' }));
    expect(within(dialog).getByText('A reason is required.')).toBeInTheDocument();
    expect(body).toBeNull();
    await user.type(within(dialog).getByLabelText(/^Reason for sending back/), 'Attach the borescope images.');
    await user.click(within(dialog).getByRole('button', { name: 'Send back to technician' }));
    await waitFor(() => expect(body).toEqual({ reason: 'Attach the borescope images.' }));
  });

  it('"Complete" offers optional dispatcher review notes and posts them as resolutionNotes', async () => {
    let body = null;
    mockFetch({
      'GET /auth/me': USERS.FlightDispatcher,
      'GET /work-orders/wo1': reported,
      'GET /maintenance-action-codes': ACTION_CODES,
      'POST /work-orders/wo1/close': ({ body: b }) => {
        body = b;
        return { ...reported, status: 'Closed' };
      },
    });
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/work-orders/wo1', user: USERS.FlightDispatcher });
    await user.click(await screen.findByRole('button', { name: 'Complete' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText(/^Dispatcher review notes/), 'Report accepted.');
    await user.click(within(dialog).getByRole('button', { name: 'Complete' }));
    await waitFor(() => expect(body).toEqual({ resolutionNotes: 'Report accepted.' }));
  });
});

describe('work orders list - source', () => {
  it('shows a Source column distinguishing Automatic and Manual tickets', async () => {
    mockFetch({
      'GET /auth/me': USERS.FlightDispatcher,
      'GET /aircraft': { items: [], page: 1, pageSize: 100, totalCount: 0, totalPages: 0 },
      'GET /work-orders': {
        items: [
          { ...autoWo, id: 'w1', workOrderNumber: 'WO-1' },
          { ...autoWo, id: 'w2', workOrderNumber: 'WO-2', source: 'Manual' },
        ],
        page: 1,
        pageSize: 25,
        totalCount: 2,
        totalPages: 1,
      },
    });
    renderApp(<AppRoutes />, { route: '/work-orders', user: USERS.FlightDispatcher });
    const table = await screen.findByRole('table', { name: /Work orders/ });
    expect(within(table).getByRole('columnheader', { name: /Source/ })).toBeInTheDocument();
    const rows = within(table).getAllByRole('row');
    const auto = within(rows.find((r) => r.textContent.includes('WO-1'))).getByText('Automatic').closest('.badge');
    const manual = within(rows.find((r) => r.textContent.includes('WO-2'))).getByText('Manual').closest('.badge');
    expect(auto.dataset.tone).not.toBe(manual.dataset.tone);
  });
});

describe('My tickets', () => {
  const t = (id, status, priority, createdAtUtc) => ({ ...autoWo, id, workOrderNumber: id, title: `Ticket ${id}`, status, priority, createdAtUtc, assignedTechnicianId: 2, allowedTransitions: [] });
  const tickets = [
    t('C-closed', 'Closed', 'Critical', '2026-10-01T00:00:00Z'),
    t('R-reported', 'Resolved', 'Low', '2026-10-02T00:00:00Z'),
    t('I-progress', 'InInspection', 'Medium', '2026-10-03T00:00:00Z'),
    t('A-low-old', 'Assigned', 'Low', '2026-10-01T00:00:00Z'),
    t('A-high-new', 'Assigned', 'High', '2026-10-05T00:00:00Z'),
    t('A-high-old', 'Assigned', 'High', '2026-10-04T00:00:00Z'),
    { ...t('X-manual', 'Closed', 'Low', '2026-10-06T00:00:00Z'), source: 'Manual', diagnosisStatus: 'Failed' },
  ];

  it('orders ASSIGNED, IN_PROGRESS, REPORTED, then COMPLETED; by priority then oldest first within a group', () => {
    expect(sortMyTickets(tickets).map((w) => w.id)).toEqual(['A-high-old', 'A-high-new', 'A-low-old', 'I-progress', 'R-reported', 'C-closed', 'X-manual']);
    expect(countByStatus(tickets)).toEqual({ Closed: 2, Resolved: 1, InInspection: 1, Assigned: 3 });
  });

  it('shows the sorted tickets with source and diagnosis, and counts derived from the API data', async () => {
    mockFetch({
      'GET /auth/me': USERS.MaintenanceTechnician,
      'GET /dashboards/technician': { counts: { assignedOpen: 4, inInspection: 1, overdue: 0, awaitingClosure: 1 }, assignedWorkOrders: [], recentlyResolved: [] },
      'GET /work-orders': { items: tickets, page: 1, pageSize: 200, totalCount: tickets.length, totalPages: 1 },
    });
    renderApp(<AppRoutes />, { route: '/my-tickets', user: USERS.MaintenanceTechnician });
    const table = await screen.findByRole('table', { name: 'My tickets' });
    const order = within(table)
      .getAllByRole('row')
      .slice(1)
      .map((r) => within(r).getAllByRole('cell')[0].textContent);
    expect(order).toEqual(['A-high-old', 'A-high-new', 'A-low-old', 'I-progress', 'R-reported', 'C-closed', 'X-manual']);
    expect(within(table).getByRole('columnheader', { name: /Source/ })).toBeInTheDocument();
    expect(within(table).getByRole('columnheader', { name: /Diagnosis/ })).toBeInTheDocument();
    const manualRow = within(table).getAllByRole('row').find((r) => r.textContent.includes('X-manual'));
    expect(within(manualRow).getByText('Manual')).toBeInTheDocument();
    expect(within(manualRow).getByText('Diagnosis failed')).toBeInTheDocument();

    const kpi = (label) => screen.getByText(label, { selector: '.kpi__label' }).closest('.kpi').querySelector('.kpi__value').textContent;
    expect(kpi('ASSIGNED')).toBe('3');
    expect(kpi('IN_PROGRESS')).toBe('1');
    expect(kpi('REPORTED')).toBe('1');
    expect(kpi('COMPLETED')).toBe('2');
  });
});

describe('Maintenance Log - reports awaiting review', () => {
  it('lists REPORTED work orders separately from the accepted maintenance history', async () => {
    const statuses = [];
    mockFetch({
      'GET /auth/me': USERS.FlightDispatcher,
      'GET /aircraft': { items: [], page: 1, pageSize: 100, totalCount: 0, totalPages: 0 },
      'GET /work-orders': ({ url }) => {
        statuses.push(url.searchParams.get('status'));
        return { items: [{ ...autoWo, status: 'Resolved', resolvedAtUtc: '2026-10-27T12:00:00Z', assignedTechnicianName: 'Tom Tech' }], page: 1, pageSize: 100, totalCount: 1, totalPages: 1 };
      },
      'GET /maintenance-logs': {
        items: [{ id: 'ml1', aircraftId: 'a1', registrationNumber: 'N120UA', eventType: 'Inspection', occurredAtUtc: '2026-10-07T00:00:00Z', description: 'Accepted engine inspection', isVerified: true, workOrderId: null }],
        page: 1,
        pageSize: 25,
        totalCount: 1,
        totalPages: 1,
      },
    });
    renderApp(<AppRoutes />, { route: '/maintenance-log', user: USERS.FlightDispatcher });
    const review = await screen.findByRole('region', { name: 'Reports awaiting your review' });
    const reviewTable = await within(review).findByRole('table', { name: 'Reports awaiting your review' });
    expect(statuses).toEqual(['Resolved']);
    expect(within(reviewTable).getByText('REPORTED')).toBeInTheDocument();
    expect(within(reviewTable).getByRole('link', { name: 'Review WO-2026-000001' })).toHaveAttribute('href', '/work-orders/wo1');

    const history = screen.getByRole('region', { name: /Maintenance history/ });
    const historyTable = await within(history).findByRole('table', { name: 'Maintenance log' });
    expect(within(historyTable).getByText('Accepted engine inspection')).toBeInTheDocument();
    expect(within(historyTable).queryByText('WO-2026-000001')).not.toBeInTheDocument();
    expect(within(reviewTable).queryByText('Accepted engine inspection')).not.toBeInTheDocument();
  });

  it('shows an empty state when nothing is waiting for review', async () => {
    mockFetch({
      'GET /auth/me': USERS.FlightDispatcher,
      'GET /aircraft': { items: [], page: 1, pageSize: 100, totalCount: 0, totalPages: 0 },
      'GET /work-orders': { items: [], page: 1, pageSize: 100, totalCount: 0, totalPages: 0 },
      'GET /maintenance-logs': { items: [], page: 1, pageSize: 25, totalCount: 0, totalPages: 0 },
    });
    renderApp(<AppRoutes />, { route: '/maintenance-log', user: USERS.FlightDispatcher });
    expect(await screen.findByText('No reports are waiting for review')).toBeInTheDocument();
  });
});

describe('work-order list source filter', () => {
  it('filters on the server, so paging and counts stay correct', async () => {
    const queries = [];
    mockFetch({
      'GET /auth/me': USERS.FlightDispatcher,
      'GET /aircraft': { items: [], page: 1, pageSize: 25, totalCount: 0, totalPages: 0 },
      'GET /work-orders': ({ url }) => {
        queries.push(url.searchParams.get('source'));
        return { items: [], page: 1, pageSize: 25, totalCount: 0, totalPages: 0 };
      },
    });
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/work-orders', user: USERS.FlightDispatcher });
    const select = await screen.findByLabelText('Source');
    await user.selectOptions(select, 'Automatic');
    await waitFor(() => expect(queries).toContain('Automatic'));
  });
});
