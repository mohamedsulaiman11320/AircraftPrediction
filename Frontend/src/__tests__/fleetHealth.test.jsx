import { describe, expect, it } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { AppRoutes } from '../App.jsx';
import { USERS, mockFetch, renderApp } from '../test/utils.jsx';

const BREACH = {
  code: 'EngineTemperatureHighC',
  ruleReference: 'BR-01',
  limit: 'Temp > 700 °C',
  description: 'Engine temperature exceeds 700 C within the evaluation window.',
  recommendedInspection: 'Immediate thermal shroud and turbine inspection.',
};

const breached = {
  id: 'ac-1',
  registrationNumber: 'TEST-A01',
  manufacturer: 'Airbus',
  model: 'A320neo',
  fleetGroup: 'Narrowbody',
  operationalStatus: 'Available',
  statusReason: null,
  openAnomalyCount: 2,
  activeInspectionFlagCount: 0,
  openWorkOrderCount: 1,
  latestRisk: null,
  lastTelemetryAtUtc: '2026-10-21T08:00:00Z',
  health: {
    latestReadingAtUtc: '2026-10-21T08:00:00Z',
    engineTemperatureC: 912.4,
    oilPressurePsi: 28.5,
    vibrationIndex: 0.812,
    windowFromUtc: '2026-09-21T08:00:00Z',
    readingsInWindow: 1440,
    isAssessable: true,
    isHighRisk: true,
    breaches: [BREACH],
    recommendedInspection: BREACH.recommendedInspection,
    thresholdsValidated: false,
    thresholdSource: 'The Hub BRD v0.4 (21 October 2026) section 8',
    failureRiskScore: 0.73,
    failureRiskTier: 'High',
  },
};

const notAssessable = {
  ...breached,
  id: 'ac-2',
  registrationNumber: 'TEST-A02',
  openAnomalyCount: 0,
  openWorkOrderCount: 0,
  lastTelemetryAtUtc: null,
  health: {
    latestReadingAtUtc: null,
    engineTemperatureC: null,
    oilPressurePsi: null,
    vibrationIndex: null,
    windowFromUtc: '2026-09-21T08:00:00Z',
    readingsInWindow: 0,
    isAssessable: false,
    isHighRisk: false,
    breaches: [],
    recommendedInspection: null,
    thresholdsValidated: true,
    thresholdSource: null,
    failureRiskScore: null,
    failureRiskTier: null,
  },
};

const page = (items) => ({ items, page: 1, pageSize: 25, totalCount: items.length, totalPages: 1 });

function openFleet(items) {
  mockFetch({
    'GET /auth/me': USERS.FlightDispatcher,
    'GET /aircraft/fleet-groups': ['Narrowbody'],
    'GET /aircraft': page(items),
    // The page also carries the telemetry-history graph; these tests only assert the table.
    'GET /telemetry/series': { aircraftId: items[0]?.id, bucketMinutes: 60, points: [] },
  });
  renderApp(<AppRoutes />, { route: '/fleet', user: USERS.FlightDispatcher });
  return screen.findByRole('table', { name: 'Fleet health' });
}

// Six columns sharing the full width (2026-10-28); readings, threshold status and counts are folded into them.
const FLEET_HEADERS = ['Aircraft', 'Status', 'Risk indicator', 'Anomalies / work orders', 'Latest telemetry', 'Actions'];

const rowFor = (table, registration) => within(table).getByRole('link', { name: registration }).closest('tr');

/** The single cell under `header` on the row for `registration` - threshold and model columns are separate. */
function cellFor(table, registration, header) {
  const headers = within(table)
    .getAllByRole('columnheader')
    .map((h) => h.textContent.replace(/[↕▲▼]/g, '').trim());
  const index = headers.indexOf(header);
  expect(index).toBeGreaterThan(-1);
  return rowFor(table, registration).cells[index];
}

describe('Fleet Health list', () => {
  it('renders the six columns from the API payload, readings folded into Latest telemetry', async () => {
    const table = await openFleet([breached]);
    expect(within(table).getAllByRole('columnheader').map((h) => h.textContent.replace(/[↕▲▼]/g, '').trim())).toEqual(FLEET_HEADERS);
    expect(table.className).toContain('data-table--fixed');
    expect(screen.getByRole('heading', { level: 1, name: 'Fleet Health' })).toBeInTheDocument();
    const row = rowFor(table, 'TEST-A01');
    expect(within(row).getByText('912.4')).toBeInTheDocument();
    expect(within(row).getByText('28.5')).toBeInTheDocument();
    expect(within(row).getByText('0.81')).toBeInTheDocument();
    expect(within(row).getByRole('link', { name: 'TEST-A01' })).toHaveAttribute('href', '/fleet/ac-1');
    expect(within(row).getByRole('link', { name: 'View TEST-A01' })).toHaveAttribute('href', '/fleet/ac-1');
    expect(within(row).getByTestId('open-items')).toHaveTextContent('2 anomalies · 0 flags · 1 open WO');
  });

  it('states that a threshold breach is a breach and names the limit in plain terms, never the rule reference', async () => {
    const table = await openFleet([breached]);
    const cell = cellFor(table, 'TEST-A01', 'Risk indicator');
    expect(within(cell).getByText('Threshold breach')).toBeInTheDocument();
    expect(within(cell).getByText('Temp > 700 °C')).toBeInTheDocument();
    expect(within(cell).queryByText('BR-01')).not.toBeInTheDocument();
    expect(within(cell).queryByText('Within client-defined thresholds')).not.toBeInTheDocument();
  });

  it('never calls a window without readings healthy', async () => {
    const table = await openFleet([notAssessable]);
    const cell = cellFor(table, 'TEST-A02', 'Risk indicator');
    expect(within(cell).getByText('Not assessable - no reading in window')).toBeInTheDocument();
    expect(within(cell).queryByText('Within client-defined thresholds')).not.toBeInTheDocument();
    expect(within(cell).queryByText('Threshold breach')).not.toBeInTheDocument();
    const row = rowFor(table, 'TEST-A02');
    expect(within(row).getAllByText('No reading').length).toBe(4); // three measurements + the reading time
  });

  it('has no model-risk column and never shows a failure probability', async () => {
    const table = await openFleet([notAssessable, breached]);
    expect(within(table).queryByRole('columnheader', { name: /30-day risk/ })).not.toBeInTheDocument();
    expect(table.textContent).not.toMatch(/\d+(\.\d+)?\s*%/);
  });

  it('shows the unvalidated-threshold disclaimer with its source, and hides it once validated', async () => {
    await openFleet([breached]);
    const note = screen.getByText(/client-defined values recorded in the BRD/);
    expect(note).toHaveTextContent('awaiting stakeholder validation');
    expect(note).toHaveTextContent('not manufacturer limits and not regulatory guidance');
    expect(note).toHaveTextContent('Threshold source: The Hub BRD v0.4 (21 October 2026) section 8.');
  });

  it('shows no disclaimer when every row was scored against validated thresholds', async () => {
    await openFleet([notAssessable]);
    expect(screen.queryByText(/client-defined values recorded in the BRD/)).not.toBeInTheDocument();
  });

  // 2026-10-28: the overhaul brief removes the Fleet column (every aircraft read "Unassigned"); Model stays out.
  it('marks stale telemetry from the seeded rule and does not mark fresh or unknown data', async () => {
    const stale = { ...breached, id: 'ac-stale', registrationNumber: 'TEST-STALE', health: { ...breached.health, isStale: true, staleAfterHours: 48 } };
    const fresh = { ...breached, id: 'ac-fresh', registrationNumber: 'TEST-FRESH', health: { ...breached.health, isStale: false } };
    const table = await openFleet([stale, fresh]);
    const rowOf = (reg) => within(table).getByText(reg).closest('tr');
    expect(within(rowOf('TEST-STALE')).getByText('Stale data')).toHaveAttribute('title', 'Older than 48 h');
    expect(within(rowOf('TEST-FRESH')).queryByText('Stale data')).not.toBeInTheDocument();
  });

  it('has no Fleet or Model column and no fleet-group filter', async () => {
    const table = await openFleet([breached]);
    expect(within(table).queryByRole('columnheader', { name: 'Model' })).not.toBeInTheDocument();
    expect(within(table).queryByRole('columnheader', { name: /^Fleet/ })).not.toBeInTheDocument();
    expect(within(table).queryByText('Narrowbody')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Fleet group')).not.toBeInTheDocument();
    for (const header of FLEET_HEADERS) {
      expect(within(table).getByRole('columnheader', { name: new RegExp(`^${header.replace(/[().°]/g, '\\$&')}`) })).toBeInTheDocument();
    }
  });

  it('shows each breached limit once in plain terms - two limits joined, a repeated one not duplicated', async () => {
    const OIL = { code: 'OilPressureLowPsi', ruleReference: 'BR-01', limit: 'Oil < 35 PSI', description: 'Oil pressure below 35 PSI.', recommendedInspection: 'Oil system inspection.' };
    const table = await openFleet([{ ...breached, health: { ...breached.health, breaches: [BREACH, OIL, { ...OIL, code: 'OilPressureLowPsi2' }] } }]);
    const cell = cellFor(table, 'TEST-A01', 'Risk indicator');
    expect(within(cell).getByText('Temp > 700 °C · Oil < 35 PSI')).toBeInTheDocument();
    expect(cell.textContent).not.toMatch(/BR-0/);
  });
});

describe('Aircraft health condition card', () => {
  const ac = (id, reg, over) => ({ ...breached, id, registrationNumber: reg, derivedState: 'Available', forecastCondition: null, ...over });
  const fleet = [
    ac('a1', 'TEST-A01'), ac('a2', 'TEST-A02'), ac('a3', 'TEST-A03'), ac('a4', 'TEST-A04'),
    ac('a5', 'TEST-A05', { derivedState: 'UnderMaintenance', forecastCondition: 'Critical' }),
    ac('a6', 'TEST-A06', { derivedState: 'UnderMaintenance', forecastCondition: 'HighCritical' }),
  ];

  it('counts Available, Critical and High Critical from the API and filters the table on click', async () => {
    const table = await openFleet(fleet);
    const card = screen.getByTestId('fleet-condition');
    expect(within(card).getByTestId('condition-available')).toHaveTextContent('4Available');
    expect(within(card).getByTestId('condition-critical')).toHaveTextContent('1Critical');
    expect(within(card).getByTestId('condition-highCritical')).toHaveTextContent('1High Critical');
    expect(within(card).queryByTestId('condition-other')).not.toBeInTheDocument();
    expect(within(table).getAllByRole('row')).toHaveLength(7);
    fireEvent.click(within(card).getByTestId('condition-highCritical'));
    expect(within(card).getByTestId('condition-highCritical')).toHaveAttribute('aria-pressed', 'true');
    expect(within(table).getAllByRole('row')).toHaveLength(2);
    expect(within(table).getByText('TEST-A06')).toBeInTheDocument();
    fireEvent.click(within(card).getByTestId('condition-highCritical'));           // pressing again clears the filter
    expect(within(table).getAllByRole('row')).toHaveLength(7);
  });
});

describe('derived aircraft state', () => {
  it('renders AwaitingRelease as "Awaiting release" in the Fleet Health status column', async () => {
    const table = await openFleet([{ ...breached, operationalStatus: 'UnderMaintenance', derivedState: 'AwaitingRelease' }]);
    const cell = cellFor(table, 'TEST-A01', 'Status');
    expect(within(cell).getByText('Awaiting release')).toBeInTheDocument();
    expect(within(cell).queryByText('Under maintenance')).not.toBeInTheDocument();
  });

  it('falls back to operationalStatus when derivedState is absent from an older payload', async () => {
    const table = await openFleet([{ ...breached, operationalStatus: 'UnderMaintenance' }]);
    const cell = cellFor(table, 'TEST-A01', 'Status');
    expect(within(cell).getByText('Under maintenance')).toBeInTheDocument();
    expect(cell.textContent.trim()).not.toBe('');
  });
});

describe('Dispatch readiness', () => {
  const readinessRow = (overrides = {}) => ({
    aircraftId: 'ac-1',
    registrationNumber: 'TEST-A01',
    model: 'A320neo',
    fleetGroup: 'Narrowbody',
    operationalStatus: 'Available',
    dispatchReadiness: 'NoKnownRestrictions',
    restrictions: [],
    latestRiskCategory: 'Low',
    openAnomalyCount: 0,
    lastTelemetryAtUtc: '2026-10-21T08:00:00Z',
    ...overrides,
  });

  /*
   * The readiness table left the dashboard in the Phase 7 redesign; its invariants now hold on the aircraft cards.
   */
  function openDashboard(readiness) {
    mockFetch({
      'GET /auth/me': USERS.FlightDispatcher,
      'GET /dashboards/dispatcher': {
        fleetCounts: { total: 3, available: 1, underMaintenance: 1, grounded: 1 },
        readiness,
        riskOverview: [],
        recentAlerts: [],
        activeRestrictions: [],
      },
      'GET /telemetry/series': { points: [] },
    });
    renderApp(<AppRoutes />, { route: '/dashboard/dispatcher', user: USERS.FlightDispatcher });
    return screen.findByTestId('aircraft-list');
  }

  const grounded = readinessRow({
    aircraftId: 'ac-3',
    registrationNumber: 'TEST-A03',
    operationalStatus: 'Grounded',
    derivedState: 'Grounded',
    dispatchReadiness: 'NotAvailable',
    restrictions: ['Grounding inspection flag raised'],
  });

  it('has no Grounded or Under maintenance count on the dashboard', async () => {
    await openDashboard([readinessRow(), grounded]);
    // Such counts once added up the ETL auto-registration placeholder status (all six aircraft).
    for (const id of ['kpi-threshold', 'kpi-work-orders']) {
      expect(screen.getByTestId(id)).not.toHaveTextContent(/Grounded|Under maintenance/);
    }
  });

  it('still identifies a grounded aircraft on its own card', async () => {
    await openDashboard([readinessRow(), grounded]);
    expect(screen.getByTestId('aircraft-card-TEST-A03')).toHaveTextContent('Grounded');
    expect(screen.getByTestId('aircraft-card-TEST-A01')).not.toHaveTextContent('Grounded');
  });

  it('shows derivedState on the card and falls back to operationalStatus', async () => {
    await openDashboard([
      readinessRow({ operationalStatus: 'UnderMaintenance', derivedState: 'AwaitingRelease' }),
      readinessRow({ aircraftId: 'ac-2', registrationNumber: 'TEST-A02', operationalStatus: 'UnderMaintenance' }),
    ]);
    expect(screen.getByTestId('aircraft-card-TEST-A01')).toHaveTextContent('Awaiting release');
    expect(screen.getByTestId('aircraft-card-TEST-A02')).toHaveTextContent('Under maintenance');
  });
});
