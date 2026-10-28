import { describe, expect, it } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { AppRoutes } from '../App.jsx';
import { FleetHealthTelemetryPanel, NO_TELEMETRY_IN_RANGE } from '../pages/fleet/FleetHealthGraph.jsx';
import { formatDateTime } from '../utils/format.js';
import { USERS, mockFetch, problem, renderApp } from '../test/utils.jsx';

const T1 = '2026-10-20T10:00:00Z';
const T2 = '2026-10-20T11:00:00Z';
const T3 = '2026-10-20T12:00:00Z';

const hoursAgo = (h) => new Date(Date.now() - h * 3600000).toISOString();

/** Deliberately out of order: the chart, not the fixture, is responsible for the time axis. */
const POINTS = [
  { timestampUtc: T3, temperatureC: 300, pressureKpa: 200, count: 12, pressurePsi: 29, vibrationIndex: 0.731 },
  { timestampUtc: T1, temperatureC: 111.1, pressureKpa: 100, count: 12, pressurePsi: 14.5, vibrationIndex: 0.512 },
  // A bucket with no reading at all. Nothing here may ever be drawn as a zero.
  { timestampUtc: T2, temperatureC: null, pressureKpa: null, count: 0, pressurePsi: null, vibrationIndex: null },
];

const series = (points, aircraftId = 'ac-1') => ({
  aircraftId,
  fromUtc: '2026-10-20T00:00:00Z',
  toUtc: '2026-10-20T23:59:59Z',
  bucketMinutes: 60,
  points,
});

const health = ({ latest = hoursAgo(1), windowFrom = hoursAgo(24) } = {}) => ({
  latestReadingAtUtc: latest,
  engineTemperatureC: 300,
  oilPressurePsi: 29,
  vibrationIndex: 0.731,
  windowFromUtc: windowFrom,
  readingsInWindow: 24,
  isAssessable: true,
  isHighRisk: false,
  breaches: [],
  recommendedInspection: null,
  thresholdsValidated: true,
  thresholdSource: null,
  failureRiskScore: null,
  failureRiskTier: null,
});

const aircraft = (overrides = {}) => ({
  id: 'ac-1',
  registrationNumber: 'TEST-A01',
  manufacturer: 'Airbus',
  model: 'A320neo',
  fleetGroup: 'Narrowbody',
  operationalStatus: 'Available',
  openAnomalyCount: 0,
  activeInspectionFlagCount: 0,
  openWorkOrderCount: 0,
  lastTelemetryAtUtc: hoursAgo(1),
  latestRisk: { riskCategory: 'Elevated', basis: 'DescriptiveIndicator', predictionTimestampUtc: hoursAgo(2), horizonDays: 30 },
  health: health(),
  ...overrides,
});

function renderPanel({ points = POINTS, ac = aircraft(), routes } = {}) {
  const fetchMock = mockFetch(
    routes || {
      'GET /auth/me': USERS.FlightDispatcher,
      'GET /telemetry/series': series(points, ac.id),
    },
  );
  const view = renderApp(
    <FleetHealthTelemetryPanel aircraft={ac} fromUtc="2026-10-20T00:00:00.000Z" toUtc="2026-10-20T23:59:59.000Z" bucketMinutes={60} />,
    { user: USERS.FlightDispatcher },
  );
  return { ...view, fetchMock };
}

const regionFor = (heading) => screen.getByRole('region', { name: heading });
const TEMPERATURE = 'Engine temperature (°C)';
const PRESSURE = 'Oil pressure (PSI)';
const VIBRATION = 'Vibration index (dimensionless)';

/** The first column of a chart's "View as table" disclosure, in render order. */
const timesIn = (region) => [...region.querySelectorAll('tbody tr')].map((r) => r.cells[0].textContent);

describe('Fleet Health telemetry graph', () => {
  it('plots the buckets in timestamp order on a shared time axis', async () => {
    renderPanel();
    await screen.findByRole('region', { name: TEMPERATURE });
    const expected = [T1, T2, T3].map(formatDateTime);
    for (const heading of [TEMPERATURE, PRESSURE, VIBRATION]) {
      expect(timesIn(regionFor(heading))).toEqual(expected);
    }
  });

  it('renders pressurePsi exactly as returned and never converts it a second time', async () => {
    renderPanel();
    const pressure = await screen.findByRole('region', { name: PRESSURE });
    expect(within(pressure).getByText('14.5')).toBeInTheDocument();
    // 1.45 would be a second kPa -> PSI conversion, 689 a PSI -> kPa one, 100 the raw kPa value.
    expect(within(pressure).queryByText('1.45')).not.toBeInTheDocument();
    expect(within(pressure).queryByText('689')).not.toBeInTheDocument();
    expect(within(pressure).queryByText('100')).not.toBeInTheDocument();
  });

  it('leaves a bucket with no reading as a gap and never plots it as zero', async () => {
    renderPanel();
    const temperature = await screen.findByRole('region', { name: TEMPERATURE });
    const rows = temperature.querySelectorAll('tbody tr');
    expect(rows[1].cells[0].textContent).toBe(formatDateTime(T2));
    expect(rows[1].cells[1].textContent).toBe('—');
    for (const heading of [TEMPERATURE, PRESSURE, VIBRATION]) {
      const region = regionFor(heading);
      expect(within(region).queryByText('0')).not.toBeInTheDocument();
      expect(within(region).queryByText('0.00')).not.toBeInTheDocument();
      // The line is broken at the gap: two move commands, so no segment crosses the missing bucket.
      const d = region.querySelector('svg path').getAttribute('d');
      expect(d.match(/M/g)).toHaveLength(2);
    }
  });

  it('gives temperature, pressure and vibration their own chart and their own labelled axis', async () => {
    renderPanel();
    const temperature = await screen.findByRole('region', { name: TEMPERATURE });
    const pressure = regionFor(PRESSURE);
    const vibration = regionFor(VIBRATION);
    // Three charts, so three independent y scales - °C and PSI are never on one axis.
    for (const region of [temperature, pressure, vibration]) {
      expect(region.querySelectorAll('svg')).toHaveLength(1);
    }
    expect(within(temperature).getByText('°C')).toBeInTheDocument();
    expect(within(pressure).getByText('PSI')).toBeInTheDocument();
    expect(within(vibration).getByText('index')).toBeInTheDocument();
    expect(within(temperature).queryByText('PSI')).not.toBeInTheDocument();
    expect(within(pressure).queryByText('°C')).not.toBeInTheDocument();
    expect(within(vibration).queryByText('°C')).not.toBeInTheDocument();
    expect(within(vibration).queryByText('PSI')).not.toBeInTheDocument();
  });

  it('names the aircraft and its latest telemetry timestamp', async () => {
    const ac = aircraft();
    renderPanel({ ac });
    expect(await screen.findByText('TEST-A01')).toBeInTheDocument();
    expect(screen.getByText(/Latest telemetry reading:/)).toBeInTheDocument();
    expect(screen.getByText(new RegExp(formatDateTime(ac.health.latestReadingAtUtc).replace(/[().,]/g, '\\$&')))).toBeInTheDocument();
  });

  it('labels the history as recorded readings, with no forecast-unavailable banner', async () => {
    renderPanel();
    expect(await screen.findByRole('region', { name: TEMPERATURE })).toBeInTheDocument();
    expect(screen.getByTestId('recorded-history-note')).toHaveTextContent('Recorded readings');
    expect(screen.queryByText(/Forecast unavailable/)).not.toBeInTheDocument();
  });

  it('labels the risk category as a descriptive indicator and never as a probability', async () => {
    const { container } = renderPanel();
    await screen.findByRole('region', { name: TEMPERATURE });
    expect(screen.getByText('Risk indicator — descriptive indicator, not a failure probability')).toBeInTheDocument();
    expect(screen.getByText('Elevated')).toBeInTheDocument();
    expect(container.textContent).not.toContain('%');
  });

  it('is honest when the selected range holds no telemetry', async () => {
    renderPanel({ points: [] });
    expect(await screen.findByText(NO_TELEMETRY_IN_RANGE)).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: TEMPERATURE })).not.toBeInTheDocument();
  });

  it('shows a skeleton while the series is loading', () => {
    renderPanel();
    expect(screen.getByRole('status', { name: 'Loading telemetry history' })).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: TEMPERATURE })).not.toBeInTheDocument();
  });

  it('warns that a reading older than the evaluation window is stale, with the threshold and the age', async () => {
    renderPanel({ ac: aircraft({ health: health({ latest: hoursAgo(72), windowFrom: hoursAgo(24) }), lastTelemetryAtUtc: hoursAgo(72) }) });
    const warning = await screen.findByText(/older than the 24-hour evaluation window/);
    expect(warning).toHaveTextContent('3 d ago');
    expect(screen.getByText('Stale telemetry')).toBeInTheDocument();
  });

  it('does not warn when the latest reading is inside the evaluation window', async () => {
    renderPanel();
    await screen.findByRole('region', { name: TEMPERATURE });
    expect(screen.queryByText('Stale telemetry')).not.toBeInTheDocument();
  });

  it('renders an error state whose retry refetches the series', async () => {
    let calls = 0;
    const { fetchMock } = renderPanel({
      routes: {
        'GET /auth/me': USERS.FlightDispatcher,
        'GET /telemetry/series': () => (++calls === 1 ? problem(500, 'server_error', 'Series unavailable') : series(POINTS)),
      },
    });
    const retry = await screen.findByRole('button', { name: /retry/i });
    expect(screen.getByText('Could not load telemetry history')).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: TEMPERATURE })).not.toBeInTheDocument();

    fireEvent.click(retry);
    expect(await screen.findByRole('region', { name: TEMPERATURE })).toBeInTheDocument();
    expect(calls).toBe(2);
    expect(fetchMock).toHaveBeenCalled();
  });
});

describe('Fleet Health graph on the Fleet Health page', () => {
  const page = (items) => ({ items, page: 1, pageSize: 25, totalCount: items.length, totalPages: 1 });
  const AC1 = aircraft();
  const AC2 = aircraft({ id: 'ac-2', registrationNumber: 'TEST-A02', model: 'E195-E2' });
  const OTHER_POINTS = [{ timestampUtc: T1, temperatureC: 555.5, pressureKpa: 400, count: 9, pressurePsi: 58, vibrationIndex: 0.221 }];

  function openFleet() {
    const requested = [];
    mockFetch({
      'GET /auth/me': USERS.FlightDispatcher,
      'GET /aircraft/fleet-groups': ['Narrowbody'],
      'GET /aircraft': page([AC1, AC2]),
      'GET /telemetry/series': ({ url }) => {
        const id = url.searchParams.get('aircraftId');
        requested.push(id);
        return series(id === 'ac-2' ? OTHER_POINTS : POINTS, id);
      },
    });
    renderApp(<AppRoutes />, { route: '/fleet', user: USERS.FlightDispatcher });
    return requested;
  }

  it('puts the graph on Fleet Health with a picker fed by the real aircraft list', async () => {
    openFleet();
    const select = await screen.findByLabelText('Aircraft');
    await waitFor(() => expect(select).toHaveValue('ac-1'));
    expect(within(select).getByRole('option', { name: 'TEST-A02 · E195-E2' })).toBeInTheDocument();
    expect(await screen.findByRole('region', { name: TEMPERATURE })).toBeInTheDocument();
    expect(screen.getByLabelText('From (UTC)')).toBeInTheDocument();
    expect(screen.getByLabelText('To (UTC)')).toBeInTheDocument();
  });

  it('never shows the previous aircraft series after the selection changes', async () => {
    const requested = openFleet();
    const select = await screen.findByLabelText('Aircraft');
    await waitFor(() => expect(screen.getByText('111.1')).toBeInTheDocument());

    fireEvent.change(select, { target: { value: 'ac-2' } });
    // Cleared the moment the selection changes, before the new series has arrived.
    expect(screen.queryByText('111.1')).not.toBeInTheDocument();

    await waitFor(() => expect(screen.getByText('555.5')).toBeInTheDocument());
    expect(screen.queryByText('111.1')).not.toBeInTheDocument();
    expect(screen.getAllByText('TEST-A02').length).toBeGreaterThan(0);
    expect(requested).toContain('ac-2');
  });
});
