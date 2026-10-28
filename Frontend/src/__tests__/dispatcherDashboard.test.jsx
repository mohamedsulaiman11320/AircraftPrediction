import { describe, expect, it } from 'vitest';
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AppRoutes } from '../App.jsx';
import { USERS, mockFetch, renderApp } from '../test/utils.jsx';
import { anomalyForBreach, forecastAnomalyForBreach, filterAircraft, isBreach, aircraftCards } from '../pages/dashboards/health/healthModel.js';

/*
 * Health Dashboard. Invariants: every number comes from the API; the chart uses only GET /aircraft/{id}/sensor-forecast -
 * recorded telemetry is never shown as a forecast; a forecast breach opens the ForecastModel anomaly behind it; only a validated probability is shown as a probability; a provisional score stays
 * provisional with no tier; "not assessed" is never counted as low risk.
 */

const THRESHOLDS = [
  { code: 'OilPressureLowPsi', measurement: 'OilPressure', comparison: 'LessThan', value: 35, unit: 'psi', isValidated: true, description: 'Oil pressure falls below 35 PSI.' },
  { code: 'EngineTemperatureHighC', measurement: 'EngineTemperature', comparison: 'GreaterThan', value: 700, unit: 'C', isValidated: true, description: 'Engine temperature exceeds 700 C.' },
];

const row = (over) => ({
  aircraftId: 'ac-1', registrationNumber: 'N120UA', fleetGroup: 'Unassigned', isHighRisk: true, isStale: false,
  latestReadingAtUtc: '2026-10-26T18:00:00Z', recommendedInspection: null, failureProbability: null, riskTier: null,
  predictionStatus: 'NoModelPrediction', predictionCategory: null, predictionAtUtc: null, featureCutoffUtc: null,
  openWorkOrders: 0, breachCodes: [], ...over,
});

function dashboard(over = {}) {
  return {
    fleetCounts: { total: 3, available: 1, underMaintenance: 2, grounded: 0 },
    readiness: [
      { aircraftId: 'a', registrationNumber: 'N120UA', model: 'A320', derivedState: 'AwaitingRelease' },
      { aircraftId: 'b', registrationNumber: 'N237UA', model: 'A320', derivedState: 'Available' },
      { aircraftId: 'c', registrationNumber: 'N512UA', model: 'B737', derivedState: 'UnderMaintenance' },
    ],
    riskOverview: [], recentAlerts: [], activeRestrictions: [], aircraftWithoutPrediction: 1, selectedFleetGroup: null,
    fleets: [{ fleetGroup: 'Fleet East', aircraftCount: 2 }, { fleetGroup: 'Unassigned', aircraftCount: 1 }],
    health: { aircraft: 3, assessable: 2, highRisk: 1, withinLimits: 1, notAssessable: 1, stale: 1, latestReadingAtUtc: '2026-10-26T18:00:00Z', thresholdsValidated: true },
    prediction: { horizonDays: 30, validated: 1, low: 0, medium: 0, critical: 1, notValidated: 1, noModelPrediction: 1, latestPredictionAtUtc: '2026-10-27T00:00:00Z' },
    workOrders: { open: 2, assigned: 1, inInspection: 0, resolved: 3, closed: 4, cancelled: 0 },
    aircraftRisk: [
      row({ aircraftId: 'a', registrationNumber: 'N120UA', fleetGroup: 'Fleet East', failureProbability: 0.823, riskTier: 'Critical', predictionStatus: 'Validated', featureCutoffUtc: '2026-10-27T00:00:00Z', breachCodes: ['OilPressureLowPsi', 'EngineTemperatureHighC'] }),
      row({ aircraftId: 'b', registrationNumber: 'N237UA', fleetGroup: 'Fleet East', isHighRisk: false, isStale: true, predictionStatus: 'NotValidated', provisionalScore: 0.934, modelVersion: '20261027094704' }),
      row({ aircraftId: 'c', registrationNumber: 'N512UA', isHighRisk: null, latestReadingAtUtc: null }),
    ],
    lastScoringRunAtUtc: '2026-10-27T07:42:14Z',
    thresholds: THRESHOLDS,
    ...over,
  };
}

/** RECORDED readings (GET /telemetry/series); two breach the oil-pressure limit. */
const SERIES = {
  aircraftId: 'a', bucketMinutes: 60,
  points: [
    { timestampUtc: '2026-10-26T15:00:00Z', temperatureC: 650, pressurePsi: 40, vibrationIndex: 2.1 },
    { timestampUtc: '2026-10-26T16:00:00Z', temperatureC: 655, pressurePsi: 30.5, vibrationIndex: 2.2 },
    { timestampUtc: '2026-10-26T17:00:00Z', temperatureC: 660, pressurePsi: 41, vibrationIndex: 2.4 },
    { timestampUtc: '2026-10-26T18:00:00Z', temperatureC: 662, pressurePsi: 31.1, vibrationIndex: 2.3 },
  ],
};

/** What the real API returns today: no forecasting model exists. */
const NOT_AVAILABLE = (horizon) => ({
  aircraftId: 'a', registrationNumber: 'N120UA', horizon, status: 'NotAvailable',
  reason: 'No sensor-forecasting model exists. The platform\'s models detect anomalies and estimate one 30-day failure risk per aircraft.',
  supportedHorizons: [], modelName: null, modelVersion: null, generatedAtUtc: null, dataAsOfUtc: null, points: [],
});

/**
 * TEST FIXTURE ONLY - a hypothetical forecast in the API contract's shape, to exercise the UI path that genuine
 * forecasts will take. It never reaches the application outside these tests.
 */
const FIXTURE_POINTS = {
  OneDay: [
    { timestampUtc: '2026-10-27T01:00:00Z', temperatureC: 690, pressurePsi: 36, vibrationIndex: 2.0 },
    { timestampUtc: '2026-10-27T02:00:00Z', temperatureC: 712, pressurePsi: 34, vibrationIndex: null },
  ],
  SevenDays: [
    { timestampUtc: '2026-10-28T00:00:00Z', temperatureC: 650, pressurePsi: 40, vibrationIndex: 2.1 },
    { timestampUtc: '2026-10-29T00:00:00Z', temperatureC: 660, pressurePsi: 39, vibrationIndex: 2.2 },
    { timestampUtc: '2026-10-30T00:00:00Z', temperatureC: 670, pressurePsi: 38, vibrationIndex: 2.3 },
  ],
};
const FIXTURE = (horizon) => ({
  ...NOT_AVAILABLE(horizon), status: 'Available', reason: null, supportedHorizons: ['OneDay', 'SevenDays'],
  modelName: 'fixture-forecaster', modelVersion: 'T1', generatedAtUtc: '2026-10-27T00:00:00Z', dataAsOfUtc: '2026-10-26T18:00:00Z',
  points: FIXTURE_POINTS[horizon] || [],
});

function routes(over = {}) {
  return {
    'GET /auth/me': USERS.FlightDispatcher,
    'GET /dashboards/dispatcher': dashboard(),
    'GET /telemetry/series': SERIES,
    'GET /aircraft/a/sensor-forecast': ({ url }) => NOT_AVAILABLE(url.searchParams.get('horizon')),
    'GET /aircraft/b/sensor-forecast': ({ url }) => NOT_AVAILABLE(url.searchParams.get('horizon')),
    'GET /aircraft/c/sensor-forecast': ({ url }) => NOT_AVAILABLE(url.searchParams.get('horizon')),
    'GET /anomalies': { items: [], totalCount: 0 },
    ...over,
  };
}

const FORECAST_ANOMALY = {
  id: 273, aircraftId: 'a', registrationNumber: 'N120UA', source: 'ForecastModel', ruleCodes: 'EngineTemperatureHighC,OilPressureLowPsi',
  observedAtUtc: '2026-10-27T02:00:00Z', lastObservedAtUtc: '2026-10-27T05:00:00Z', featureValues: { horizon: 'OneDay' },
};

const THRESHOLD_ANOMALY = {
  id: 267, aircraftId: 'a', registrationNumber: 'N120UA', source: 'ThresholdRule', ruleCodes: 'OilPressureLowPsi',
  observedAtUtc: '2026-10-26T18:00:00Z', lastObservedAtUtc: null,
  featureValues: { windowFromUtc: '2026-10-25T18:00:00Z', windowToUtc: '2026-10-26T18:00:00Z' },
};

const open = (route = '/dashboard/dispatcher') => renderApp(<AppRoutes />, { route, user: USERS.FlightDispatcher });
const requested = (fetchMock, path) => fetchMock.mock.calls.map(([u]) => new URL(u)).filter((u) => u.pathname.endsWith(path));

describe('Health Dashboard layout', () => {
  it('builds the KPI cards from the API, links each to an existing screen, and shows no provisional-score card', async () => {
    mockFetch(routes());
    open();
    expect(await screen.findByRole('heading', { level: 1, name: 'Health Dashboard' })).toBeInTheDocument();
    const threshold = screen.getByTestId('kpi-threshold');
    expect(within(threshold).getByRole('group')).toHaveAccessibleName(/Threshold health of 3 aircraft: Breaching a limit 1, Within limits 1, No recent data 1/);
    expect(threshold).toHaveAttribute('href', '/fleet');
    expect(screen.queryByTestId('kpi-risk')).not.toBeInTheDocument();
    expect(screen.queryByText(/provisional/i)).not.toBeInTheDocument();
    expect(within(screen.getByTestId('kpi-work-orders')).getByRole('group')).toHaveAccessibleName('Work orders: New 2, Assigned 1, In progress 0, Reported 3, Completed 4');
  });

  it('puts the sidebar collapse toggle in the sidebar brand bar and the profile avatar in the dashboard card', async () => {
    mockFetch(routes());
    const user = userEvent.setup();
    open();
    await screen.findByTestId('kpi-threshold');
    expect(screen.queryByTestId('app-title')).not.toBeInTheDocument();
    const sidebar = screen.getByRole('complementary', { name: 'Main navigation' });
    const toggle = within(sidebar).getByTestId('sidebar-toggle');                 // beside the sidebar brand, not the card
    const overview = screen.getByRole('region', { name: 'Overview' });
    expect(within(overview).queryByTestId('sidebar-toggle')).not.toBeInTheDocument();
    expect(within(overview).getByTestId('profile-avatar')).toHaveTextContent('FD');
    expect(screen.getAllByTestId('profile-avatar')).toHaveLength(1);              // no second avatar in a top bar
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByTestId('overview-details')).toHaveTextContent('Oil pressure below 35 PSI');
    await user.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(toggle).toHaveAccessibleName('Expand sidebar');
    expect(sidebar.className).toContain('sidebar--collapsed');
    expect(within(sidebar).getByRole('link', { name: /Fleet Health/ })).toBeInTheDocument();   // labels stay accessible
    await user.click(toggle);
    expect(sidebar.className).not.toContain('sidebar--collapsed');
  });

  it('lists aircraft vertically with search above, and the selection drives the forecast request and the URL', async () => {
    const fetchMock = mockFetch(routes());
    const user = userEvent.setup();
    open();
    const list = await screen.findByTestId('aircraft-list');
    expect(list.className).toMatch(/overflow-y-auto/);                             // scrolls vertically
    expect(list.className).toMatch(/flex-col/);
    expect(within(list).getAllByRole('button').map((b) => b.dataset.testid)).toEqual(['aircraft-card-N120UA', 'aircraft-card-N237UA', 'aircraft-card-N512UA']);
    expect(list).not.toHaveTextContent('Fleet East');                             // no fleet label
    expect(list).not.toHaveTextContent('Unassigned');
    expect(within(screen.getByTestId('aircraft-card-N512UA')).getByTestId('state-badge')).toHaveTextContent('Under maintenance');
    await waitFor(() => expect(requested(fetchMock, '/aircraft/a/sensor-forecast')).toHaveLength(1));

    const search = screen.getByRole('searchbox', { name: 'Search aircraft' });
    expect(search.compareDocumentPosition(list) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();   // search is above
    await user.type(search, 'b737');
    expect(within(screen.getByTestId('aircraft-list')).getAllByRole('button')).toHaveLength(1);
    await user.clear(search);
    await user.type(search, 'zzz');
    expect(screen.getByText('No aircraft match "zzz".')).toBeInTheDocument();
    await user.clear(search);

    await user.click(screen.getByTestId('aircraft-card-N237UA'));
    expect(screen.getByTestId('aircraft-card-N237UA')).toHaveAttribute('aria-pressed', 'true');
    await waitFor(() => expect(requested(fetchMock, '/aircraft/b/sensor-forecast')).toHaveLength(1));
    expect(window.location.search).toContain('aircraft=N237UA');
    expect(screen.getByRole('heading', { level: 2, name: 'N237UA' })).toBeInTheDocument();
  });

  it('moves between aircraft with the arrow keys', async () => {
    mockFetch(routes());
    open();
    const first = await screen.findByTestId('aircraft-card-N120UA');
    first.focus();
    fireEvent.keyDown(first, { key: 'ArrowDown' });
    expect(screen.getByTestId('aircraft-card-N237UA')).toHaveFocus();
    fireEvent.keyDown(screen.getByTestId('aircraft-card-N237UA'), { key: 'End' });
    expect(screen.getByTestId('aircraft-card-N512UA')).toHaveFocus();
  });

  it('shows an uncalibrated model score as a 0-100 risk score, never as a probability or tier', async () => {
    mockFetch(routes());
    open('/dashboard/dispatcher?aircraft=N237UA');
    const risk = await screen.findByTestId('risk-summary');
    expect(within(risk).getByTestId('risk-score')).toHaveTextContent('93 / 100');
    expect(risk).not.toHaveTextContent('%');
    for (const tier of ['Critical', 'Medium', 'Low']) expect(within(risk).queryByText(new RegExp(tier))).not.toBeInTheDocument();
  });

  it('shows an error with retry when the dashboard cannot load', async () => {
    let fail = true;
    mockFetch(routes({
      'GET /dashboards/dispatcher': () => (fail ? new Response(JSON.stringify({ title: 'boom' }), { status: 500, headers: { 'Content-Type': 'application/problem+json' } }) : dashboard()),
    }));
    const user = userEvent.setup();
    open();
    const retry = await screen.findByRole('button', { name: /try again|retry/i });
    fail = false;
    await user.click(retry);
    expect(await screen.findByTestId('kpi-threshold')).toBeInTheDocument();
  });
});

describe('Forecast chart', () => {
  it('shows the API reason when no forecast exists and never falls back to recorded telemetry', async () => {
    const fetchMock = mockFetch(routes());
    const user = userEvent.setup();
    open();
    const unavailable = await screen.findByTestId('forecast-unavailable');
    expect(unavailable).toHaveTextContent('No sensor forecast available for 1 Month');
    expect(unavailable).toHaveTextContent('No sensor-forecasting model exists');
    expect(screen.queryByTestId('chart-forecast')).not.toBeInTheDocument();
    expect(screen.queryAllByTestId('breach-marker')).toHaveLength(0);
    expect(requested(fetchMock, '/telemetry/series')).toHaveLength(0);              // recorded data not even fetched
    await user.click(within(screen.getByTestId('view-toggle')).getByRole('button', { name: 'Table View' }));
    expect(screen.getByTestId('forecast-unavailable')).toBeInTheDocument();
    expect(screen.queryByTestId('forecast-table')).not.toBeInTheDocument();
    expect(screen.queryByTestId('forecast-freshness')).not.toBeInTheDocument();     // no invented timestamps
  });

  it('disables the horizons the API does not support', async () => {
    mockFetch(routes());
    open();
    await screen.findByTestId('forecast-unavailable');
    const filters = screen.getByTestId('horizon-filters');
    expect(within(filters).getByRole('button', { name: /1 Day/ })).toBeDisabled();
    expect(within(filters).getByRole('button', { name: /7 Days/ })).toBeDisabled();
    expect(within(filters).getByRole('button', { name: /1 Month/ })).toHaveAttribute('aria-pressed', 'true');
  });

  it('shows an error with retry when the forecast cannot load', async () => {
    let fail = true;
    mockFetch(routes({
      'GET /aircraft/a/sensor-forecast': ({ url }) => (fail ? new Response('{}', { status: 500 }) : NOT_AVAILABLE(url.searchParams.get('horizon'))),
    }));
    const user = userEvent.setup();
    open();
    const err = await screen.findByTestId('forecast-error');
    fail = false;
    await user.click(within(err).getByRole('button', { name: 'Retry' }));
    expect(await screen.findByTestId('forecast-unavailable')).toBeInTheDocument();
  });

  it('[fixture] switches horizon, keeps chart and table on the same dataset, and toggles each series', async () => {
    const fetchMock = mockFetch(routes({ 'GET /aircraft/a/sensor-forecast': ({ url }) => FIXTURE(url.searchParams.get('horizon')) }));
    const user = userEvent.setup();
    open('/dashboard/dispatcher?horizon=SevenDays');
    const chart = await screen.findByTestId('chart-forecast');
    expect(screen.getByTestId('forecast-freshness')).toHaveTextContent('Generated');
    // Colours: temperature red, pressure green, vibration blue - in the chart and in the legend.
    expect(within(chart).getByTestId('series-temperatureC')).toHaveAttribute('data-color', '#dc2626');
    expect(within(chart).getByTestId('series-pressurePsi')).toHaveAttribute('data-color', '#15803d');
    expect(within(chart).queryByTestId('series-vibrationIndex')).not.toBeInTheDocument();    // hidden until ticked
    await user.click(screen.getByTestId('toggle-forecast-vibrationIndex'));
    expect(within(screen.getByTestId('chart-forecast')).getByTestId('series-vibrationIndex')).toHaveAttribute('data-color', '#2563eb');
    const legend = screen.getByTestId('legend-forecast');
    expect(legend).toHaveTextContent('Engine temperature (°C)');
    expect(legend).toHaveTextContent('Oil pressure (PSI)');
    expect(legend).toHaveTextContent('Vibration (index)');

    await user.click(screen.getByTestId('toggle-forecast-pressurePsi'));            // hide one series
    expect(within(screen.getByTestId('chart-forecast')).queryByTestId('panel-pressurePsi')).not.toBeInTheDocument();
    expect(within(screen.getByTestId('chart-forecast')).getByTestId('panel-temperatureC')).toBeInTheDocument();
    await user.click(screen.getByTestId('toggle-forecast-pressurePsi'));            // and back - data was kept
    expect(within(screen.getByTestId('chart-forecast')).getByTestId('panel-pressurePsi')).toBeInTheDocument();

    await user.click(within(screen.getByTestId('horizon-filters')).getByRole('button', { name: '1 Day' }));
    await waitFor(() => expect(requested(fetchMock, '/aircraft/a/sensor-forecast').map((u) => u.searchParams.get('horizon'))).toContain('OneDay'));
    expect(window.location.search).toContain('horizon=OneDay');
    const markers = await screen.findAllByTestId('breach-marker');                 // 712 °C and 34 PSI cross the limits
    expect(markers.map((m) => [m.dataset.kind, m.dataset.metric])).toEqual([['Forecast', 'temperatureC'], ['Forecast', 'pressurePsi']]);
    expect(screen.queryByTestId('limit-vibrationIndex')).not.toBeInTheDocument();  // no approved vibration limit

    await user.click(within(screen.getByTestId('view-toggle')).getByRole('button', { name: 'Table View' }));
    const table = screen.getByTestId('forecast-table');
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(FIXTURE_POINTS.OneDay.length);                        // same dataset as the chart
    expect(rows[1]).toHaveTextContent('Unavailable');                               // vibration not supplied
    expect(rows[1]).toHaveTextContent('Beyond limit');
    expect(rows[0]).toHaveTextContent('Within approved limits');
    expect(rows[0]).toHaveTextContent('fixture-forecaster T1');
    expect(within(screen.getByTestId('horizon-filters')).getByRole('button', { name: /1 Month/ })).toBeDisabled();
  });

  it('[fixture] opens the forecast anomaly behind a breach marker', async () => {
    const asked = [];
    mockFetch(routes({
      'GET /aircraft/a/sensor-forecast': () => FIXTURE('OneDay'),
      'GET /anomalies': ({ url }) => (asked.push(Object.fromEntries(url.searchParams)), { items: [FORECAST_ANOMALY], totalCount: 1 }),
      'GET /anomalies/273': { ...FORECAST_ANOMALY, telemetry: null, severity: 'High', investigationStatus: 'New', scoredAtUtc: '2026-10-27T00:00:00Z', modelName: 'sensor-forecaster', modelVersion: 'T1' },
      'GET /work-orders': { items: [], totalCount: 0 },
    }));
    const user = userEvent.setup();
    open('/dashboard/dispatcher?horizon=OneDay');
    const [temperature] = await screen.findAllByTestId('breach-marker');
    await user.click(temperature);
    await waitFor(() => expect(window.location.pathname).toBe('/anomalies/273'));
    expect(asked[0]).toMatchObject({ aircraftId: 'a', source: 'ForecastModel' });
  });

  it('[fixture] rests with plain lines and reveals a breach node only on hover; clicking it opens the anomaly', async () => {
    mockFetch(routes({
      'GET /aircraft/a/sensor-forecast': () => FIXTURE('OneDay'),
      'GET /anomalies': { items: [FORECAST_ANOMALY], totalCount: 1 },
      'GET /anomalies/273': { ...FORECAST_ANOMALY, telemetry: null, severity: 'High', investigationStatus: 'New', scoredAtUtc: '2026-10-27T00:00:00Z', modelName: 'sensor-forecaster', modelVersion: 'T1' },
      'GET /work-orders': { items: [], totalCount: 0 },
    }));
    open('/dashboard/dispatcher?horizon=OneDay');
    const chart = await screen.findByTestId('chart-forecast');
    const svg = chart.querySelector('svg');
    // At rest: no dots at all; the keyboard diamonds are invisible until focused.
    expect(within(chart).queryAllByTestId('breach-node')).toHaveLength(0);
    expect(svg.querySelectorAll('circle')).toHaveLength(0);
    for (const m of within(chart).getAllByTestId('breach-marker')) expect(m.getAttribute('class')).toContain('opacity-0');

    fireEvent.mouseMove(svg, { clientX: 100 });                                       // 01:00 - within limits
    expect(within(chart).queryAllByTestId('breach-node')).toHaveLength(0);
    expect(screen.queryByTestId('tooltip-breach')).not.toBeInTheDocument();

    fireEvent.mouseMove(svg, { clientX: 670 });                                       // 02:00 - 712 °C and 34 PSI
    const nodes = within(chart).getAllByTestId('breach-node');
    expect(nodes.map((n) => [n.dataset.metric, n.getAttribute('fill')])).toEqual([['temperatureC', '#dc2626'], ['pressurePsi', '#15803d']]);
    expect(screen.getAllByTestId('tooltip-breach')[0]).toHaveTextContent('Click to view anomaly');

    fireEvent.mouseLeave(svg);
    expect(within(chart).queryAllByTestId('breach-node')).toHaveLength(0);           // gone again on leave

    fireEvent.mouseMove(svg, { clientX: 670 });
    // A direct click: userEvent would first replay a pointer move at x=0, which (correctly) clears the hover.
    await act(async () => {
      fireEvent.click(within(chart).getAllByTestId('breach-node')[0]);
    });
    await waitFor(() => expect(window.location.pathname).toBe('/anomalies/273'));
  });

  it('[fixture] labels a shorter horizon served from the 30-day forecast', async () => {
    mockFetch(routes({ 'GET /aircraft/a/sensor-forecast': () => ({ ...FIXTURE('OneDay'), slicedFromHorizon: 'ThirtyDays' }) }));
    open('/dashboard/dispatcher?horizon=OneDay');
    expect(await screen.findByTestId('forecast-freshness')).toHaveTextContent('first 1 day of the 1 month forecast');
  });

  it('[fixture] explains a forecast breach with no recorded anomaly yet and creates nothing', async () => {
    const fetchMock = mockFetch(routes({ 'GET /aircraft/a/sensor-forecast': () => FIXTURE('OneDay') }));
    open('/dashboard/dispatcher?horizon=OneDay');
    const [first] = await screen.findAllByTestId('breach-marker');
    first.focus();
    await act(async () => {
      fireEvent.keyDown(first, { key: 'Enter' });
    });
    const notice = await screen.findByTestId('forecast-breach-notice');
    await waitFor(() => expect(notice).toHaveTextContent('No forecast anomaly has been recorded for this point yet'));
    expect(window.location.pathname).toBe('/dashboard/dispatcher');
    expect(fetchMock.mock.calls.filter(([, init]) => (init?.method || 'GET') !== 'GET')).toEqual([]);
  });
});

describe('Forecast uncertainty and validation [fixture]', () => {
  const WITH_INTERVALS = () => ({
    ...FIXTURE('OneDay'),
    supportedMetrics: ['temperatureC', 'pressurePsi'],
    points: [
      { timestampUtc: '2026-10-27T01:00:00Z', temperatureC: 650, temperatureCLow: 610, temperatureCHigh: 690, pressurePsi: 40, pressurePsiLow: 37, pressurePsiHigh: 43, vibrationIndex: null },
      { timestampUtc: '2026-10-27T02:00:00Z', temperatureC: 655, temperatureCLow: 612, temperatureCHigh: 698, pressurePsi: 41, pressurePsiLow: 38, pressurePsiHigh: 44, vibrationIndex: null },
    ],
  });

  it('draws no interval bands on a white chart, keeps the 80% interval in the table, and names the unvalidated metric', async () => {
    mockFetch(routes({ 'GET /aircraft/a/sensor-forecast': WITH_INTERVALS }));
    const user = userEvent.setup();
    open('/dashboard/dispatcher?horizon=OneDay');
    const chart = await screen.findByTestId('chart-forecast');
    expect(within(chart).queryByTestId(/^band-/)).not.toBeInTheDocument();
    expect(within(chart).getByTestId('chart-background')).toHaveAttribute('fill', '#ffffff');
    expect(within(chart).queryAllByTestId('breach-node')).toHaveLength(0);           // within limits: no nodes at all
    expect(within(chart).queryByTestId('panel-vibrationIndex')).not.toBeInTheDocument();   // vibration starts hidden
    expect(screen.getByTestId('toggle-forecast-vibrationIndex')).not.toBeChecked();
    await user.click(screen.getByTestId('toggle-forecast-vibrationIndex'));
    expect(within(screen.getByTestId('chart-forecast')).getByTestId('missing-series-note')).toHaveTextContent('Vibration is not validated for this horizon - not forecast');

    await user.click(within(screen.getByTestId('view-toggle')).getByRole('button', { name: 'Table View' }));
    const [first] = within(screen.getByTestId('forecast-table')).getAllByRole('row').slice(1);
    expect(first).toHaveTextContent('650 °C(610–690)');
    expect(first).toHaveTextContent('Not validated');                               // vibration, never 'Unavailable'
    expect(screen.getByTestId('forecast-table')).toHaveTextContent('Values in brackets: the 80% interval');
  });
});

describe('health dashboard model', () => {
  it('compares against lower and upper limits correctly', () => {
    expect(isBreach(34.9, THRESHOLDS[0])).toBe(true);
    expect(isBreach(35, THRESHOLDS[0])).toBe(false);
    expect(isBreach(700, THRESHOLDS[1])).toBe(false);
    expect(isBreach(700.1, THRESHOLDS[1])).toBe(true);
    expect(isBreach(null, THRESHOLDS[0])).toBe(false);
  });

  it('matches a recorded breach only to a threshold incident that covers its time and names its limit', () => {
    const base = { timestampUtc: '2026-10-26T16:00:00Z', limitCode: 'OilPressureLowPsi' };
    expect(anomalyForBreach([THRESHOLD_ANOMALY], base)?.id).toBe(267);
    expect(anomalyForBreach([THRESHOLD_ANOMALY], { ...base, limitCode: 'EngineTemperatureHighC' })).toBeNull();
    expect(anomalyForBreach([THRESHOLD_ANOMALY], { ...base, timestampUtc: '2026-10-24T16:00:00Z' })).toBeNull();
    expect(anomalyForBreach([{ ...THRESHOLD_ANOMALY, source: 'IsolationForest' }], base)).toBeNull();
    const continuing = { ...THRESHOLD_ANOMALY, lastObservedAtUtc: '2026-10-28T18:00:00Z' };
    expect(anomalyForBreach([continuing], { ...base, timestampUtc: '2026-10-28T10:00:00Z' })?.id).toBe(267);
  });

  it('matches a forecast breach only to a forecast incident that covers its time and names its limit', () => {
    const base = { timestampUtc: '2026-10-27T03:00:00Z', limitCode: 'EngineTemperatureHighC' };
    expect(forecastAnomalyForBreach([FORECAST_ANOMALY], base)?.id).toBe(273);
    expect(forecastAnomalyForBreach([FORECAST_ANOMALY], { ...base, timestampUtc: '2026-10-27T06:00:00Z' })).toBeNull();
    expect(forecastAnomalyForBreach([{ ...FORECAST_ANOMALY, source: 'ThresholdRule' }], base)).toBeNull();
    expect(forecastAnomalyForBreach([{ ...FORECAST_ANOMALY, ruleCodes: 'OilPressureLowPsi' }], base)).toBeNull();
  });

  it('searches every identifying field the API returns', () => {
    const cards = aircraftCards(dashboard());
    expect(filterAircraft(cards, 'b737').map((c) => c.registration)).toEqual(['N512UA']);
    expect(filterAircraft(cards, '  ').length).toBe(3);
  });
});
