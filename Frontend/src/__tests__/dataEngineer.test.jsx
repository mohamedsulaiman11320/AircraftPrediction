import { describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AppRoutes } from '../App.jsx';
import { ACCESS, DATA_ENGINEER, defaultPathFor, getNavSections } from '../router/access.js';
import { hasAccess } from '../auth/roles.js';
import { availableModelActions } from '../pages/models/ModelDetailPage.jsx';
import { predictionPeriod } from '../pages/shared/columns.jsx';
import { MAX_RAW_JSON_BYTES, precheckRawJsonFile } from '../utils/rawJson.js';
import { duplicateTelemetryId, isDuplicateRejection } from '../pages/telemetry/DataQualityPage.jsx';
import { daysAgo, toInputDate } from '../utils/format.js';
import { USERS, jsonResponse, mockFetch, problem, renderApp } from '../test/utils.jsx';

const DE = { id: 5, username: 'eli', email: 'de@example.com', displayName: 'Eli Engineer', roles: [DATA_ENGINEER], status: 'Active' };
const page = (items) => ({ items, page: 1, pageSize: 25, totalCount: items.length, totalPages: 1 });
const AIRCRAFT = page([
  { id: 'ac-1', registrationNumber: 'TEST-A01', model: 'A320neo' },
  { id: 'ac-2', registrationNumber: 'TEST-A02', model: 'E195-E2' },
]);
const labels = (user) => getNavSections(user).flatMap((s) => s.items.map((i) => i.label));

function imp(overrides = {}) {
  return {
    id: 'im-9',
    fileName: 'raw.json',
    source: 'RawJson',
    fileSha256: 'f'.repeat(64),
    status: 'Queued',
    totalRows: 0,
    acceptedRows: 0,
    rejectedRows: 0,
    duplicateRows: 0,
    warningCount: 0,
    warnings: [],
    errorMessage: null,
    storedPath: null,
    fileSizeBytes: 0,
    requestedBy: 'Eli Engineer',
    requestedAtUtc: '2026-10-21T08:00:00Z',
    startedAtUtc: null,
    completedAtUtc: null,
    ...overrides,
  };
}

describe('DataEngineer navigation and route guards', () => {
  it('sees exactly five top-level menus, in order, and lands on menu 1', () => {
    expect(labels(DE)).toEqual(['Data Model', 'Pipeline', 'Data Quality', 'Model Versions', 'User Management']);
    expect(defaultPathFor(DE)).toBe('/imports');
  });

  it('the dropped entries are a navigation change only — the routes and capabilities remain', () => {
    for (const k of ['dataModelsDashboard', 'anomalies', 'predictions', 'telemetry', 'reports', 'reportDataQuality', 'modelsView']) {
      expect(hasAccess(DE, ACCESS[k])).toBe(true);
    }
    // Model & Forecast was renamed Model Versions; the capabilities keep the DataEngineer role
    expect(labels(DE)).not.toContain('Model & Forecast');
    expect(ACCESS.modelsView).toEqual([DATA_ENGINEER]);
    expect(ACCESS.predictions).toEqual([DATA_ENGINEER]);
  });

  it('may upload CSV and raw JSON and retry, but not act on anomalies/predictions or approve models', () => {
    for (const k of ['telemetryImportUpload', 'rawJsonUpload', 'importRetry', 'modelRegister', 'modelActivate', 'aircraftLookup', 'reportDataQuality', 'reportModelPerformance', 'adminUsers', 'developmentLab']) {
      expect(hasAccess(DE, ACCESS[k])).toBe(true);
    }
    for (const k of ['anomalyActions', 'predictionActions', 'modelApprove', 'fleet', 'workOrdersView', 'technicians', 'auditActions', 'reportFleetHealth', 'reportMaintenance']) {
      expect(hasAccess(DE, ACCESS[k])).toBe(false);
    }
    // the data area belongs to the DataEngineer alone since the dispatcher was scoped to five tabs
    expect(hasAccess(USERS.FlightDispatcher, ACCESS.rawJsonUpload)).toBe(false);
    expect(hasAccess(USERS.MaintenanceTechnician, ACCESS.rawJsonUpload)).toBe(false);
  });

  it.each(['/dashboard/data-engineer', '/telemetry', '/imports', '/data-quality', '/pipelines', '/models', '/anomalies', '/predictions', '/reports', '/admin/users'])('can open %s', async (path) => {
    mockFetch({ 'GET /auth/me': DE });
    renderApp(<AppRoutes />, { route: path, user: DE });
    await waitFor(() => expect(document.querySelector('h1')).not.toBeNull());
    expect(window.location.pathname).toBe(path);
    expect(screen.queryByRole('heading', { name: 'Access denied' })).not.toBeInTheDocument();
  });

  it.each(['/work-orders', '/work-orders/wo-1', '/work-orders/new', '/fleet', '/fleet/ac-1', '/audit', '/dashboard/dispatcher', '/dashboard/leadership', '/my-tickets', '/maintenance-log'])(
    'is sent to access denied from %s',
    async (path) => {
      mockFetch({ 'GET /auth/me': DE });
      renderApp(<AppRoutes />, { route: path, user: DE });
      await waitFor(() => expect(window.location.pathname).toBe('/access-denied'));
      expect(await screen.findByRole('heading', { name: 'Access denied' })).toBeInTheDocument();
    },
  );
});

describe('models for a DataEngineer', () => {
  const can = (allow) => hasAccess(DE, allow);
  it('register/activate/retire are offered; approve/reject are hidden', () => {
    expect(availableModelActions({ status: 'Trained' }, can)).toEqual(['register']);
    expect(availableModelActions({ status: 'Registered' }, can)).toEqual([]);
    expect(availableModelActions({ status: 'Approved' }, can)).toEqual(['activate']);
    expect(availableModelActions({ status: 'Active' }, can)).toEqual(['retire']);
    // model approval was revoked from the dispatcher with the five-tab scoping and no role holds it yet
    const dispatcherCan = (allow) => hasAccess(USERS.FlightDispatcher, allow);
    expect(availableModelActions({ status: 'Registered' }, dispatcherCan)).toEqual([]);
  });

  it('the model detail page shows Activate but no Approve/Reject buttons', async () => {
    const model = { id: 'mv-1', modelName: 'anomaly-iforest', version: '1.0.0', modelType: 'AnomalyDetection', algorithm: 'IsolationForest', status: 'Approved', metrics: {}, featureNames: [], createdAtUtc: '2026-10-01T00:00:00Z' };
    mockFetch({ 'GET /auth/me': DE, 'GET /models/mv-1': model, 'GET /models/mv-1/evaluation': { metrics: {}, evaluationAvailable: false } });
    renderApp(<AppRoutes />, { route: '/models/mv-1', user: DE });
    expect(await screen.findByRole('button', { name: 'Activate' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Reject' })).not.toBeInTheDocument();
  });
});

describe('raw JSON client-side validation', () => {
  const file = (content, name = 'raw.json') => new File([content], name, { type: 'application/json' });
  it('requires .json, non-empty, <= 50 MB, valid JSON with an array root', async () => {
    expect(await precheckRawJsonFile(file('[]', 'raw.csv'))).toEqual(['The file must have a .json extension.']);
    expect(await precheckRawJsonFile(file(''))).toEqual(['The file is empty.']);
    const big = file('[]');
    Object.defineProperty(big, 'size', { value: MAX_RAW_JSON_BYTES + 1 });
    expect(await precheckRawJsonFile(big)).toEqual(['The file exceeds the 50 MB limit.']);
    expect(await precheckRawJsonFile(file('[{"a":1},'))).toEqual(['The file is not valid JSON.']);
    expect(await precheckRawJsonFile(file('{"records":[]}'))).toEqual(['The JSON root must be an array of telemetry records.']);
    expect(await precheckRawJsonFile(file('[{"aircraft":"A1","ts":"2026-10-01T00:00:00Z"}]'))).toEqual([]);
  });
});

describe('raw JSON upload to the ETL', () => {
  async function openRawUpload(routes) {
    const fetchMock = mockFetch({ 'GET /auth/me': DE, 'GET /telemetry/imports': page([]), ...routes });
    const user = userEvent.setup({ applyAccept: false });
    renderApp(<AppRoutes />, { route: '/imports', user: DE });
    await screen.findByLabelText('Raw JSON file');
    return { user, fetchMock };
  }

  it('offers a .json-only upload: the CSV variant is gone from the page', async () => {
    mockFetch({ 'GET /auth/me': DE, 'GET /telemetry/imports': page([]) });
    renderApp(<AppRoutes />, { route: '/imports', user: DE });
    const input = await screen.findByLabelText('Raw JSON file');
    expect(input).toHaveAttribute('accept', '.json,application/json');
    expect(screen.getByRole('button', { name: 'Upload to ETL' })).toBeInTheDocument();
    // no CSV choice, no CSV input, no CSV help text and no CSV-only upload button
    expect(screen.queryByLabelText('Telemetry CSV')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('CSV file')).not.toBeInTheDocument();
    expect(screen.queryByRole('group', { name: 'Upload type' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Upload and import' })).not.toBeInTheDocument();
    expect(screen.queryByText(/Required columns:/)).not.toBeInTheDocument();
    expect(document.querySelectorAll('input[type="file"]')).toHaveLength(1);
    for (const el of document.querySelectorAll('input[type="file"]')) {
      expect(el.getAttribute('accept')).toBe('.json,application/json');
    }
    // the help and page copy speak of JSON only
    expect(screen.getByText(/file whose root is an array of source records/)).toBeInTheDocument();
    expect(screen.getByText(/Upload raw JSON telemetry for the ETL/)).toBeInTheDocument();
  });

  it('rejects a CSV file client-side now that the page is JSON-only', async () => {
    const { user, fetchMock } = await openRawUpload({});
    await user.upload(screen.getByLabelText('Raw JSON file'), new File(['a,b\n1,2'], 'telemetry.csv', { type: 'text/csv' }));
    expect(await screen.findByText('The file must have a .json extension.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Upload to ETL' })).toBeDisabled();
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false);
  });

  it('shows client-side errors and does not upload an invalid file', async () => {
    const { user, fetchMock } = await openRawUpload({});
    await user.upload(screen.getByLabelText('Raw JSON file'), new File(['{"not":"an array"}'], 'raw.json', { type: 'application/json' }));
    expect(await screen.findByText('The JSON root must be an array of telemetry records.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Upload to ETL' })).toBeDisabled();
    expect(fetchMock.mock.calls.some(([u]) => String(u).includes('/raw-uploads'))).toBe(false);
  });

  it('202 is not completion: polls Queued -> Processing (Spark ETL) -> Completed with counts', async () => {
    const states = [imp({ status: 'Processing' }), imp({ status: 'Completed', totalRows: 100, acceptedRows: 91, rejectedRows: 4, duplicateRows: 5, warningCount: 1, warnings: ['2 records lacked vibration_index.'] })];
    let posted = null;
    const { user } = await openRawUpload({
      'POST /telemetry/raw-uploads': ({ init }) => {
        posted = init.body;
        return jsonResponse(202, { id: 'im-9', status: 'Queued', statusUrl: '/api/v1/telemetry/imports/im-9' });
      },
      'GET /telemetry/imports/im-9': () => states.shift() || imp({ status: 'Completed' }),
    });
    await user.upload(screen.getByLabelText('Raw JSON file'), new File(['[{"a":1}]'], 'raw.json', { type: 'application/json' }));
    await user.click(await screen.findByRole('button', { name: 'Upload to ETL' }));
    const current = await screen.findByRole('heading', { name: 'Current import' });
    const section = current.parentElement;
    await waitFor(() => expect(within(section).getAllByText('Uploaded – waiting for ETL').length).toBeGreaterThan(0));
    expect(posted).toBeInstanceOf(FormData);
    expect(posted.get('file').name).toBe('raw.json');
    expect(within(section).getByText('Row counts are available once the ETL has completed.')).toBeInTheDocument();
    await waitFor(() => expect(within(section).getAllByText('Processing (Spark ETL)').length).toBeGreaterThan(0), { timeout: 4000 });
    await waitFor(() => expect(within(section).getByText('91')).toBeInTheDocument(), { timeout: 4000 });
    expect(within(section).getByText('2 records lacked vibration_index.')).toBeInTheDocument();
  }, 15000);

  it('a Failed ETL shows the error message and Retry re-queues and resumes polling', async () => {
    const states = [imp({ status: 'Failed', errorMessage: 'Spark job failed: malformed record at offset 42.' })];
    let retried = 0;
    const { user } = await openRawUpload({
      'POST /telemetry/raw-uploads': jsonResponse(202, { id: 'im-9', status: 'Queued', statusUrl: '/api/v1/telemetry/imports/im-9' }),
      'GET /telemetry/imports/im-9': () => states.shift() || imp({ status: 'Completed', totalRows: 10, acceptedRows: 10 }),
      'POST /telemetry/imports/im-9/retry': () => {
        retried++;
        return imp({ status: 'Queued' });
      },
    });
    await user.upload(screen.getByLabelText('Raw JSON file'), new File(['[]'], 'raw.json', { type: 'application/json' }));
    await user.click(await screen.findByRole('button', { name: 'Upload to ETL' }));
    expect(await screen.findByText('Spark job failed: malformed record at offset 42.', {}, { timeout: 4000 })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Retry' }));
    expect(retried).toBe(1);
    const section = screen.getByRole('heading', { name: 'Current import' }).parentElement;
    await waitFor(() => expect(within(section).getAllByText('Uploaded – waiting for ETL').length).toBeGreaterThan(0));
    expect(screen.queryByRole('button', { name: 'Retry' })).not.toBeInTheDocument();
    await waitFor(() => expect(within(section).getAllByText('Completed').length).toBeGreaterThan(0), { timeout: 4000 });
  }, 15000);

  it('lists errors.file[] from a 400 and explains a 409 duplicate', async () => {
    let call = 0;
    const { user } = await openRawUpload({
      'POST /telemetry/raw-uploads': () =>
        ++call === 1
          ? problem(400, 'validation_failed', 'Validation failed', { errors: { file: ['Record 3 is missing "timestamp".', 'Record 9 has an unknown aircraft.'] } })
          : problem(409, 'conflict', 'Duplicate file', { detail: 'This exact file has already been uploaded.' }),
    });
    const input = screen.getByLabelText('Raw JSON file');
    await user.upload(input, new File(['[1]'], 'raw.json', { type: 'application/json' }));
    await user.click(await screen.findByRole('button', { name: 'Upload to ETL' }));
    expect(await screen.findByText('Record 3 is missing "timestamp".')).toBeInTheDocument();
    expect(screen.getByText('Record 9 has an unknown aircraft.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Upload to ETL' }));
    expect(await screen.findByText('Already uploaded')).toBeInTheDocument();
  });

  it('history shows the source column, the ETL requester and a Retry for failed raw JSON imports', async () => {
    let retried = false;
    mockFetch({
      'GET /auth/me': DE,
      'GET /telemetry/imports': page([
        imp({ id: 'im-1', fileName: 'a.csv', source: 'Csv', status: 'Completed', requestedBy: 'Eli Engineer' }),
        imp({ id: 'im-2', fileName: 'dropped.json', status: 'Failed', errorMessage: 'ETL failed', requestedBy: 'ETL (placed directly in object storage)' }),
      ]),
      'POST /telemetry/imports/im-2/retry': () => {
        retried = true;
        return imp({ id: 'im-2', fileName: 'dropped.json', status: 'Queued' });
      },
      'GET /telemetry/imports/im-2': imp({ id: 'im-2', fileName: 'dropped.json', status: 'Processing' }),
    });
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/imports', user: DE });
    const table = await screen.findByRole('table', { name: 'Import history' });
    expect(within(table).getByRole('columnheader', { name: 'Source' })).toBeInTheDocument();
    expect(within(table).getByText('CSV')).toBeInTheDocument();
    expect(within(table).getByText('Raw JSON')).toBeInTheDocument();
    expect(within(table).getByText('ETL (placed directly in object storage)')).toBeInTheDocument();
    await user.click(within(table).getByRole('button', { name: 'dropped.json' }));
    await user.click(await screen.findByRole('button', { name: 'Retry' }));
    expect(retried).toBe(true);
    expect((await screen.findAllByText('Uploaded – waiting for ETL')).length).toBeGreaterThan(0);
  });

  it('a 202 upload response is not rendered as a completed ETL run', async () => {
    const { user } = await openRawUpload({
      'POST /telemetry/raw-uploads': jsonResponse(202, {
        id: 'im-9',
        status: 'Queued',
        storedPath: 'bronze/telemetry/2026/10/21/im-9.json',
        fileSizeBytes: 2048,
        statusUrl: '/api/v1/telemetry/imports/im-9',
      }),
      // the backend keeps reporting Queued: nothing has been imported yet
      'GET /telemetry/imports/im-9': () => imp({ status: 'Queued', storedPath: 'bronze/telemetry/2026/10/21/im-9.json', fileSizeBytes: 2048 }),
    });
    await user.upload(screen.getByLabelText('Raw JSON file'), new File(['[{"a":1}]'], 'raw.json', { type: 'application/json' }));
    await user.click(await screen.findByRole('button', { name: 'Upload to ETL' }));
    const section = (await screen.findByRole('heading', { name: 'Current import' })).parentElement;
    await waitFor(() => expect(within(section).getAllByText('Uploaded – waiting for ETL').length).toBeGreaterThan(0));
    // no counts are shown and the Completed stage is still pending — 202 only means "accepted"
    expect(within(section).getByText('Row counts are available once the ETL has completed.')).toBeInTheDocument();
    expect(within(section).queryByText('Total rows')).not.toBeInTheDocument();
    expect(within(section).queryByText('Accepted')).not.toBeInTheDocument();
    const completedStage = within(section).getByText('Completed');
    expect(completedStage).toHaveClass('stages__item--pending');
    expect(completedStage).not.toHaveAttribute('aria-current');
    // the file size from the 202 is shown straight away; the Bronze object key is internal detail
    expect(within(section).getByText('2.0 KB')).toBeInTheDocument();
    expect(within(section).queryByText('Bronze object key')).not.toBeInTheDocument();
    expect(within(section).queryByText('bronze/telemetry/2026/10/21/im-9.json')).not.toBeInTheDocument();
  }, 15000);

  it('history hides the Bronze object key, keeps a human-readable file size and shows a dash when the size is absent', async () => {
    mockFetch({
      'GET /auth/me': DE,
      'GET /telemetry/imports': page([
        imp({ id: 'im-1', fileName: 'stored.csv', source: 'Csv', status: 'Completed', totalRows: 10, acceptedRows: 10, storedPath: 'bronze/telemetry/2026/10/21/stored.csv', fileSizeBytes: 2048 }),
        imp({ id: 'im-2', fileName: 'big.json', status: 'Processing', storedPath: 'bronze/telemetry/2026/10/21/big.json', fileSizeBytes: 5 * 1024 * 1024 }),
        imp({ id: 'im-3', fileName: 'notstored.json', status: 'Queued', storedPath: null, fileSizeBytes: 0 }),
      ]),
    });
    renderApp(<AppRoutes />, { route: '/imports', user: DE });
    const table = await screen.findByRole('table', { name: 'Import history' });
    expect(within(table).queryByRole('columnheader', { name: 'Bronze object key' })).not.toBeInTheDocument();
    expect(within(table).queryByText('bronze/telemetry/2026/10/21/stored.csv')).not.toBeInTheDocument();
    expect(within(table).getByText('2.0 KB')).toBeInTheDocument();
    expect(within(table).getByText('5.0 MB')).toBeInTheDocument();
    // an unrecorded size renders as a dash, never as "0 B"
    const row = within(table).getByRole('button', { name: 'notstored.json' }).closest('tr');
    expect(within(row).getAllByText('—').length).toBeGreaterThanOrEqual(1);
    expect(within(table).queryByText('0 B')).not.toBeInTheDocument();
    // the other history fields stay visible
    for (const header of ['File', 'Status', 'Size', 'Total', 'Accepted', 'Rejected', 'Duplicates', 'Upload time', 'Uploaded by', 'Error']) {
      expect(within(table).getByRole('columnheader', { name: header })).toBeInTheDocument();
    }
  });
});

describe('DataEngineer in-page tab strips', () => {
  it('menu 1 offers Upload & ingestion and Telemetry and navigates between them', async () => {
    mockFetch({ 'GET /auth/me': DE, 'GET /telemetry/imports': page([]), 'GET /aircraft': AIRCRAFT });
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/imports', user: DE });
    const tablist = await screen.findByRole('tablist', { name: 'Data and model telemetry' });
    expect(within(tablist).getAllByRole('tab').map((t) => t.textContent)).toEqual(['Upload & ingestion', 'Telemetry']);
    expect(await screen.findByRole('heading', { level: 1, name: 'Data Model' })).toBeInTheDocument();
    await user.click(within(tablist).getByRole('tab', { name: 'Telemetry' }));
    await waitFor(() => expect(window.location.pathname).toBe('/telemetry'));
    expect(await screen.findByRole('heading', { level: 1, name: 'Telemetry explorer' })).toBeInTheDocument();
  });

  it('Model Versions shows the operational/development tab strip and states the Blocked forecast honestly', async () => {
    mockFetch({
      'GET /auth/me': DE,
      'GET /models': [],
      'GET /predictions/metadata': {
        configuredHorizonDays: 30,
        failurePredictionStatus: { status: 'Blocked', reasons: ['Not enough confirmed failures to train.'], horizonDays: 30 },
        activeAnomalyModel: null,
        activeFailureModel: null,
        riskCategories: [],
      },
    });
    renderApp(<AppRoutes />, { route: '/models', user: DE });
    expect(await screen.findByRole('heading', { level: 1, name: 'Model Versions' })).toBeInTheDocument();
    const envTabs = screen.getByRole('tablist', { name: 'Model environments' });
    expect(within(envTabs).getAllByRole('tab').map((t) => t.textContent)).toEqual(['Operational models', 'Development (synthetic)']);
    expect(screen.queryByRole('tablist', { name: 'Model and forecast' })).not.toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Model registry' })).not.toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Forecast' })).not.toBeInTheDocument();
    expect(await screen.findByText('Failure prediction: Blocked · 30-day horizon')).toBeInTheDocument();
    expect(screen.getByText('Not enough confirmed failures to train.')).toBeInTheDocument();
    expect(screen.getByTestId('configured-horizon')).toHaveTextContent('Configured forecast horizon: 30 days.');
  });
});

describe('predictions for a DataEngineer', () => {
  const base = {
    id: 'pr-1',
    aircraftId: 'ac-1',
    registrationNumber: 'TEST-A01',
    predictionTimestampUtc: '2026-10-20T06:00:00Z',
    featureCutoffUtc: '2026-10-20T00:00:00Z',
    horizonDays: 30,
    predictionState: 'Scored',
    riskCategory: 'Elevated',
    basis: 'DescriptiveIndicator',
    probability: 0.42,
    probabilityValidated: false,
    modelName: null,
    modelVersion: null,
    approvalStatus: 'PendingReview',
    actualOutcome: 'Unknown',
    featureSnapshot: {},
    disclaimer: 'Advisory only. Not an airworthiness determination.',
  };
  const metadata = (status) => ({ configuredHorizonDays: 30, failurePredictionStatus: { status, reasons: [], horizonDays: 30 }, activeAnomalyModel: null, activeFailureModel: null, riskCategories: [] });

  it('computes a rolling window from featureCutoffUtc + horizonDays and never invents one', () => {
    expect(predictionPeriod(base)).toEqual({ label: 'Rolling 30-day window', fromUtc: '2026-10-20T00:00:00.000Z', toUtc: '2026-11-19T00:00:00.000Z' });
    expect(predictionPeriod({ ...base, featureCutoffUtc: null })).toEqual({ label: 'Rolling 30-day window', fromUtc: null, toUtc: null });
  });

  it('shows period, state, model, disclaimer and the Blocked message; no review/outcome actions; aircraft filter from /aircraft', async () => {
    mockFetch({
      'GET /auth/me': DE,
      'GET /aircraft': AIRCRAFT,
      'GET /predictions/metadata': metadata('Blocked'),
      'GET /predictions': page([base, { ...base, id: 'pr-2', registrationNumber: 'TEST-A02', aircraftId: 'ac-2', predictionState: 'InsufficientTelemetry', probabilityValidated: true, probability: 0.25, modelName: 'failure-gbm', modelVersion: '2.0.0' }]),
    });
    renderApp(<AppRoutes />, { route: '/predictions', user: DE });
    const table = await screen.findByRole('table', { name: 'Predictions' });
    expect(within(table).getAllByText('Rolling 30-day window')).toHaveLength(2);
    expect(within(table).getByText('Insufficient telemetry')).toBeInTheDocument();
    expect(within(table).getByText('Scored')).toBeInTheDocument();
    expect(within(table).getByText('failure-gbm 2.0.0')).toBeInTheDocument();
    expect(within(table).getByText('25.0%')).toBeInTheDocument(); // validated only
    expect(within(table).queryByText('42.0%')).not.toBeInTheDocument();
    const body = table.querySelector('tbody');
    expect(within(body).getAllByRole('button', { name: /View prediction/ })).toHaveLength(2);
    expect(within(body).queryByRole('button', { name: 'Review' })).not.toBeInTheDocument();
    expect(within(body).queryByRole('button', { name: 'Outcome' })).not.toBeInTheDocument();
    expect(within(table).queryByRole('link', { name: 'TEST-A01' })).not.toBeInTheDocument(); // no aircraft detail for DE
    expect(screen.getByText('No validated failure-prediction model — showing descriptive anomaly-based risk indicators only.')).toBeInTheDocument();
    expect(screen.getByText(/Not an airworthiness determination/)).toBeInTheDocument();
    const aircraftFilter = screen.getByLabelText('Aircraft');
    await waitFor(() => expect(within(aircraftFilter).getByRole('option', { name: 'TEST-A02 · E195-E2' })).toBeInTheDocument());
  });

  it('says so honestly when the selected aircraft has no prediction', async () => {
    mockFetch({
      'GET /auth/me': DE,
      'GET /aircraft': AIRCRAFT,
      'GET /predictions/metadata': metadata('Active'),
      'GET /predictions': ({ url }) => (url.searchParams.get('aircraftId') === 'ac-2' ? page([]) : page([base])),
    });
    renderApp(<AppRoutes />, { route: '/predictions?aircraftId=ac-2', user: DE });
    expect(await screen.findByText('No prediction available for this aircraft yet')).toBeInTheDocument();
    expect(screen.queryByText(/No validated failure-prediction model/)).not.toBeInTheDocument();
  });
});

describe('telemetry explorer defaults', () => {
  const series = { aircraftId: 'ac-1', bucketMinutes: 60, points: [] };

  it('selects the first aircraft returned by the lookup and applies the last-7-days window', async () => {
    let query = null;
    mockFetch({
      'GET /auth/me': DE,
      'GET /aircraft': AIRCRAFT,
      'GET /telemetry/series': series,
      'GET /telemetry': ({ url }) => {
        query = url.searchParams;
        return page([]);
      },
    });
    renderApp(<AppRoutes />, { route: '/telemetry', user: DE });
    const select = await screen.findByLabelText('Aircraft');
    await waitFor(() => expect(select).toHaveValue('ac-1')); // first option, never a hard-coded tail number
    await waitFor(() => expect(window.location.search).toContain('aircraftId=ac-1'));
    const from = toInputDate(daysAgo(7));
    const to = toInputDate(new Date());
    expect(screen.getByLabelText('From (UTC)')).toHaveValue(from);
    expect(screen.getByLabelText('To (UTC)')).toHaveValue(to);
    await waitFor(() => expect(query).not.toBeNull());
    // the inputs are UTC calendar dates and are sent as 00:00:00Z .. 23:59:59Z
    expect(query.get('aircraftId')).toBe('ac-1');
    expect(query.get('fromUtc')).toBe(`${from}T00:00:00.000Z`);
    expect(query.get('toUtc')).toBe(`${to}T23:59:59.000Z`);
    expect(query.get('sensorId')).toBeNull();
  });

  it('falls back to the first available aircraft when the selected one is no longer in the list', async () => {
    mockFetch({ 'GET /auth/me': DE, 'GET /aircraft': AIRCRAFT, 'GET /telemetry/series': series, 'GET /telemetry': page([]) });
    renderApp(<AppRoutes />, { route: '/telemetry?aircraftId=ac-gone', user: DE });
    await waitFor(() => expect(screen.getByLabelText('Aircraft')).toHaveValue('ac-1'));
    await waitFor(() => expect(window.location.search).toContain('aircraftId=ac-1'));
  });
});

describe('telemetry records table', () => {
  it('offers no Sensor ID filter and no Sensor or Source columns', async () => {
    mockFetch({
      'GET /auth/me': DE,
      'GET /aircraft': AIRCRAFT,
      'GET /telemetry/series': { aircraftId: 'ac-1', bucketMinutes: 60, points: [] },
      'GET /telemetry': page([
        {
          id: 1,
          aircraftId: 'ac-1',
          sensorId: 'ENG1',
          recordedAtUtc: '2026-10-20T10:00:00Z',
          temperatureC: 99,
          pressureKpa: 301,
          dataSource: 'etl',
          qualityStatus: 'Valid',
          qualityFlags: [],
          sourceRecordId: 'SRC-77',
          vibrationIndex: 0.512,
          sourceStatusFlag: 'WARN',
        },
      ]),
    });
    renderApp(<AppRoutes />, { route: '/telemetry?aircraftId=ac-1', user: DE });
    const table = await screen.findByRole('table', { name: 'Telemetry records' });
    expect(screen.queryByLabelText('Sensor ID')).not.toBeInTheDocument();
    expect(within(table).queryByRole('columnheader', { name: 'Sensor' })).not.toBeInTheDocument();
    expect(within(table).queryByRole('columnheader', { name: 'Source' })).not.toBeInTheDocument();
    expect(within(table).queryByText('ENG1')).not.toBeInTheDocument();
    expect(within(table).queryByText('etl')).not.toBeInTheDocument();
    // the remaining columns are untouched
    for (const header of ['Recorded (UTC)', 'Temp °C', 'Pressure kPa', 'Quality', 'Flags', 'Source record']) {
      expect(within(table).getByRole('columnheader', { name: header })).toBeInTheDocument();
    }
  });

  it('shows source record id, vibration index and the source flag with its help text', async () => {
    const record = {
      id: 1,
      aircraftId: 'ac-1',
      sensorId: 'ENG1',
      recordedAtUtc: '2026-10-20T10:00:00Z',
      temperatureValue: 99,
      temperatureUnit: 'C',
      temperatureC: 99,
      pressureValue: 301,
      pressureUnit: 'kPa',
      pressureKpa: 301,
      dataSource: 'etl',
      qualityStatus: 'Valid',
      qualityFlags: [],
      sourceRecordId: 'SRC-77',
      vibrationIndex: 0.512,
      sourceStatusFlag: 'WARN',
    };
    mockFetch({
      'GET /auth/me': DE,
      'GET /aircraft': AIRCRAFT,
      'GET /telemetry/series': { aircraftId: 'ac-1', bucketMinutes: 60, points: [] },
      'GET /telemetry': page([record, { ...record, id: 2, sourceRecordId: null, vibrationIndex: null, sourceStatusFlag: null }]),
    });
    renderApp(<AppRoutes />, { route: '/telemetry?aircraftId=ac-1', user: DE });
    const table = await screen.findByRole('table', { name: 'Telemetry records' });
    const flagHeader = within(table).getByRole('columnheader', { name: /Source flag/ });
    expect(flagHeader.querySelector('[title]')).toHaveAttribute('title', 'Threshold-based flag from the source system — not a confirmed failure');
    expect(within(table).getByText('SRC-77')).toBeInTheDocument();
    expect(within(table).getByText('0.512')).toBeInTheDocument();
    expect(within(table).getByText('WARN')).toBeInTheDocument();
    expect(screen.getByText(/Threshold-based flag from the source system — not a confirmed failure\. Vibration/)).toBeInTheDocument();
  });
});

describe('Pipeline page: run history only', () => {
  const run = (overrides = {}) => ({
    id: 'run-1',
    jobType: 'TelemetryImport',
    status: 'Succeeded',
    trigger: 'Cli',
    requestedBy: 'etl',
    requestedAtUtc: '2026-10-21T08:00:00Z',
    startedAtUtc: '2026-10-21T08:00:05Z',
    completedAtUtc: '2026-10-21T08:01:00Z',
    parameters: {},
    resultSummary: null,
    errorMessage: null,
    modelVersionId: null,
    recordsProcessed: 120,
    ...overrides,
  });

  it('has no Start a job controls, no Dataset audit tab and no longer claims to queue ML jobs', async () => {
    const fetchMock = mockFetch({ 'GET /auth/me': DE, 'GET /pipelines/runs': page([run()]) });
    renderApp(<AppRoutes />, { route: '/pipelines', user: DE });
    expect(await screen.findByRole('heading', { level: 1, name: 'Pipeline' })).toBeInTheDocument();
    expect(screen.getByText('ETL and processing run history.')).toBeInTheDocument();
    expect(screen.queryByText('Queue ML jobs and review run history. Jobs run in the Python worker (python -m afp_ml worker).')).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Start a job' })).not.toBeInTheDocument();
    for (const name of ['Start', 'Database audit', 'Train anomaly', 'Score', 'Train failure', 'Queue job', 'Cancel']) {
      expect(screen.queryByRole('button', { name })).not.toBeInTheDocument();
    }
    expect(screen.queryByRole('tablist')).not.toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Dataset audit' })).not.toBeInTheDocument();
    // the run-history filters stay
    expect(screen.getByLabelText('Job type')).toBeInTheDocument();
    expect(screen.getByLabelText('Status')).toBeInTheDocument();
    await screen.findByRole('table', { name: 'Pipeline runs' });
    const called = fetchMock.mock.calls.map(([u]) => String(u));
    expect(called.some((u) => u.includes('/pipelines/jobs'))).toBe(false);
    expect(called.some((u) => u.includes('/pipelines/dataset-audit'))).toBe(false);
    expect(called.some((u) => u.includes('/cancel'))).toBe(false);
  });

  it('shows the recorded run fields and renders Unknown for metadata the backend does not store', async () => {
    mockFetch({
      'GET /auth/me': DE,
      'GET /pipelines/runs': page([run({ status: 'Failed', errorMessage: 'ETL failed: schema mismatch' })]),
    });
    renderApp(<AppRoutes />, { route: '/pipelines', user: DE });
    const table = await screen.findByRole('table', { name: 'Pipeline runs' });
    for (const header of ['Job', 'Status', 'Trigger', 'Requested', 'Started', 'Completed', 'Records', 'Failure reason', 'Run ID']) {
      expect(within(table).getByRole('columnheader', { name: header })).toBeInTheDocument();
    }
    expect(within(table).getByText('run-1')).toBeInTheDocument();
    expect(within(table).getByText('Cli')).toBeInTheDocument();
    expect(within(table).getByText('120')).toBeInTheDocument();
    expect(within(table).getByText('ETL failed: schema mismatch')).toBeInTheDocument();
    // the requester and the stage column are no longer part of this table
    expect(within(table).queryByRole('columnheader', { name: 'By' })).not.toBeInTheDocument();
    expect(within(table).queryByRole('columnheader', { name: 'Current / last stage' })).not.toBeInTheDocument();
    expect(within(table).queryByText('etl')).not.toBeInTheDocument();
    // counters, retries and the import name are not recorded: Unknown, never 0
    for (const header of ['Inserted', 'Updated', 'Rejected', 'Already stored', 'Retries', 'Failure stage', 'Import']) {
      expect(within(table).getByRole('columnheader', { name: header })).toBeInTheDocument();
    }
    expect(within(table).getAllByText('Unknown')).toHaveLength(7);
    expect(within(table).queryByText('0')).not.toBeInTheDocument();
    expect(screen.getByText(/Per-stage run metadata .* is not recorded for pipeline runs/)).toBeInTheDocument();
  });

  it('uses the counters a run actually reported in its result summary instead of Unknown', async () => {
    mockFetch({
      'GET /auth/me': DE,
      'GET /pipelines/runs': page([run({ resultSummary: { total_rows: 100, accepted_rows: 91, rejected_rows: 4, duplicate_rows: 5 } })]),
    });
    renderApp(<AppRoutes />, { route: '/pipelines', user: DE });
    const table = await screen.findByRole('table', { name: 'Pipeline runs' });
    expect(within(table).getByText('91')).toBeInTheDocument();
    expect(within(table).getByText('4')).toBeInTheDocument();
    expect(within(table).getByText('5')).toBeInTheDocument();
    expect(within(table).getAllByText('Unknown')).toHaveLength(4);
  });
});

describe('Data quality duplicate details', () => {
  const dataQuality = {
    totalRecords: 100,
    validRecords: 95,
    suspectRecords: 5,
    missingTemperature: 0,
    missingPressure: 0,
    firstRecordUtc: '2026-10-01T00:00:00Z',
    lastRecordUtc: '2026-10-21T00:00:00Z',
    byAircraft: [],
    imports: { count: 2, acceptedRows: 90, rejectedRows: 6, duplicateRows: 4 },
  };

  it('parses the duplicate business key from the stored rejection evidence', () => {
    expect(isDuplicateRejection({ reasons: ['duplicate telemetry_id in this file'] })).toBe(true);
    expect(isDuplicateRejection({ reasons: ["unknown aircraft registration 'X'"] })).toBe(false);
    expect(duplicateTelemetryId({ rawLine: '{"telemetry_id": "TLM-0007"}' })).toBe('TLM-0007');
    expect(duplicateTelemetryId({ rawLine: 'telemetry_id=TLM-9, tail_number=A1' })).toBe('TLM-9');
    expect(duplicateTelemetryId({ rawLine: 'no key here' })).toBeNull();
  });

  it('replaces the removed sentence and lists duplicates from the import counters and rejected rows', async () => {
    mockFetch({
      'GET /auth/me': DE,
      'GET /aircraft': AIRCRAFT,
      'GET /telemetry/data-quality': dataQuality,
      'GET /telemetry/imports': page([
        imp({ id: 'im-1', fileName: 'batch-a.json', status: 'Completed', totalRows: 50, acceptedRows: 44, rejectedRows: 4, duplicateRows: 2 }),
        imp({ id: 'im-2', fileName: 'clean.json', status: 'Completed', totalRows: 10, acceptedRows: 10, rejectedRows: 0, duplicateRows: 0 }),
      ]),
      'GET /telemetry/imports/im-1/rejected-rows': page([
        { rowNumber: 7, rawLine: '{"telemetry_id": "TLM-0007"}', reasons: ['duplicate telemetry_id in this file'] },
        { rowNumber: 9, rawLine: '{"telemetry_id": "TLM-0007"}', reasons: ['duplicate telemetry_id in this file'] },
        { rowNumber: 12, rawLine: '{"telemetry_id": "TLM-0012", "tail_number": "TEST-A01"}', reasons: ["unknown aircraft registration 'TEST-A01'"] },
      ]),
    });
    renderApp(<AppRoutes />, { route: '/data-quality', user: DE });
    expect(await screen.findByRole('heading', { level: 1, name: 'Data Quality' })).toBeInTheDocument();
    expect(
      screen.queryByText('Validity of ingested telemetry. Only physical-plausibility checks are applied until engineering thresholds are approved.'),
    ).not.toBeInTheDocument();
    const table = await screen.findByRole('table', { name: 'Duplicate records' });
    expect(within(table).getAllByText('TLM-0007')).toHaveLength(2);
    expect(within(table).getAllByText('duplicate telemetry_id in this file')).toHaveLength(2);
    expect(within(table).getAllByText('Deduplication (in-file)')).toHaveLength(2);
    expect(within(table).getAllByText('batch-a.json')).toHaveLength(2);
    // a non-duplicate rejection is not reported as a duplicate
    expect(within(table).queryByText('TLM-0012')).not.toBeInTheDocument();
    // counters and the documented retention rule
    const groups = screen.getByText('Duplicate groups (distinct telemetry ids)').closest('div');
    expect(within(groups).getByText('1')).toBeInTheDocument();
    const rejected = screen.getByText('Duplicate rows rejected').closest('div');
    expect(within(rejected).getByText('2')).toBeInTheDocument();
    expect(screen.getByText(/first occurrence of a telemetry id is retained/)).toBeInTheDocument();
    const byImport = screen.getByRole('table', { name: 'Duplicate counters by import' });
    expect(within(byImport).getByText('batch-a.json')).toBeInTheDocument();
    expect(within(byImport).queryByText('clean.json')).not.toBeInTheDocument();
  });

  it('says so honestly when no duplicate was recorded', async () => {
    mockFetch({
      'GET /auth/me': DE,
      'GET /aircraft': AIRCRAFT,
      'GET /telemetry/data-quality': dataQuality,
      'GET /telemetry/imports': page([imp({ id: 'im-2', fileName: 'clean.json', status: 'Completed', totalRows: 10, acceptedRows: 10, rejectedRows: 0, duplicateRows: 0 })]),
    });
    renderApp(<AppRoutes />, { route: '/data-quality', user: DE });
    expect(await screen.findByText('No duplicate records recorded')).toBeInTheDocument();
    expect(screen.queryByRole('table', { name: 'Duplicate records' })).not.toBeInTheDocument();
  });
});

describe('deleting an import from the history row menu', () => {
  const KEPT_ALWAYS = 'Aircraft, anomalies, predictions, maintenance, users and audit records are never removed.';
  const KEPT_LAKE = 'Lake batch b-7 is shared with another import, so its lake data is kept.';
  const PLAN = {
    importId: 'im-1',
    fileName: 'batch.json',
    status: 'Completed',
    bronzeObjects: 1,
    telemetryRecords: 1200,
    rejectedRows: 4,
    pipelineRuns: 2,
    silverPartitionsRemoved: 1,
    goldPartitionsRemoved: 1,
    preserved: [KEPT_ALWAYS, KEPT_LAKE],
  };
  const ROWS = [
    imp({ id: 'im-1', fileName: 'batch.json', status: 'Completed', totalRows: 1204, acceptedRows: 1200, rejectedRows: 4 }),
    imp({ id: 'im-2', fileName: 'other.json', status: 'Completed' }),
  ];

  async function openHistory(routes = {}, rows = ROWS) {
    const fetchMock = mockFetch({ 'GET /auth/me': DE, 'GET /telemetry/imports': () => page(rows), ...routes });
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/imports', user: DE });
    const table = await screen.findByRole('table', { name: 'Import history' });
    return { user, fetchMock, table };
  }

  const listCalls = (fetchMock) => fetchMock.mock.calls.filter(([u, i]) => (i?.method || 'GET') === 'GET' && /\/telemetry\/imports(\?|$)/.test(String(u))).length;
  const deleteCalls = (fetchMock) => fetchMock.mock.calls.filter(([, i]) => i?.method === 'DELETE').length;

  async function openDeleteDialog(user, table, fileName = 'batch.json') {
    await user.click(within(table).getByRole('button', { name: `Actions for ${fileName}` }));
    await user.click(await screen.findByRole('menuitem', { name: 'Delete import' }));
    return screen.findByRole('dialog');
  }

  it('renders a three-dot menu per row with the documented aria attributes', async () => {
    const { table } = await openHistory();
    const triggers = within(table).getAllByRole('button', { name: /^Actions for / });
    expect(triggers.map((t) => t.getAttribute('aria-label'))).toEqual(['Actions for batch.json', 'Actions for other.json']);
    for (const t of triggers) {
      expect(t).toHaveAttribute('aria-haspopup', 'menu');
      expect(t).toHaveAttribute('aria-expanded', 'false');
    }
    expect(within(table).getByRole('columnheader', { name: 'Actions' })).toBeInTheDocument();
  });

  it('opens on click and closes on Escape, on an outside click, and never leaves two menus open', async () => {
    const { user, table } = await openHistory();
    const first = within(table).getByRole('button', { name: 'Actions for batch.json' });
    const second = within(table).getByRole('button', { name: 'Actions for other.json' });

    await user.click(first);
    const menu = await screen.findByRole('menu', { name: 'Actions for batch.json' });
    expect(first).toHaveAttribute('aria-expanded', 'true');
    expect(within(menu).getByRole('menuitem', { name: 'Delete import' })).toBeInTheDocument();

    // Escape closes it and hands focus back to the trigger.
    await user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByRole('menu')).not.toBeInTheDocument());
    expect(first).toHaveAttribute('aria-expanded', 'false');
    expect(first).toHaveFocus();

    // An outside click closes it too.
    await user.click(first);
    await screen.findByRole('menu');
    await user.click(document.body);
    await waitFor(() => expect(screen.queryByRole('menu')).not.toBeInTheDocument());

    // Opening another row's menu leaves exactly one open.
    await user.click(first);
    await screen.findByRole('menu', { name: 'Actions for batch.json' });
    await user.click(second);
    await screen.findByRole('menu', { name: 'Actions for other.json' });
    expect(screen.getAllByRole('menu')).toHaveLength(1);
    expect(first).toHaveAttribute('aria-expanded', 'false');
  });

  it('Delete import fetches the deletion plan and shows the scope and what is kept before confirming', async () => {
    const { user, table, fetchMock } = await openHistory({ 'GET /telemetry/imports/im-1/deletion-plan': PLAN });
    const dialog = await openDeleteDialog(user, table);

    expect(within(dialog).getByText(/Permanently delete the import batch\.json/)).toBeInTheDocument();
    expect(await within(dialog).findByText('1,200')).toBeInTheDocument(); // telemetry records
    for (const label of ['Telemetry records', 'Rejected rows', 'Pipeline runs', 'Bronze objects', 'Silver partitions', 'Gold partitions']) {
      expect(within(dialog).getByText(label)).toBeInTheDocument();
    }
    // the preserved list is rendered verbatim as a visible "what is kept" section
    expect(within(dialog).getByText('What is kept')).toBeInTheDocument();
    const kept = within(dialog).getByRole('list', { name: 'What is kept' });
    expect(within(kept).getByText(KEPT_ALWAYS)).toBeInTheDocument();
    expect(within(kept).getByText(KEPT_LAKE)).toBeInTheDocument();
    // the plan is read-only: nothing has been deleted by opening the dialog
    expect(deleteCalls(fetchMock)).toBe(0);
    expect(fetchMock.mock.calls.some(([u]) => String(u).includes('/deletion-plan'))).toBe(true);
  });

  it('refuses to confirm when the plan cannot be loaded', async () => {
    const { user, table, fetchMock } = await openHistory({
      'GET /telemetry/imports/im-1/deletion-plan': () => problem(500, 'internal_error', 'The server encountered an error.'),
    });
    const dialog = await openDeleteDialog(user, table);
    expect(await within(dialog).findByText('Could not load data')).toBeInTheDocument();
    const confirm = within(dialog).getByRole('button', { name: 'Delete import' });
    expect(confirm).toBeDisabled();
    await user.click(confirm);
    expect(deleteCalls(fetchMock)).toBe(0);
  });

  it('confirming issues DELETE exactly once even on a double click', async () => {
    let release;
    const inFlight = new Promise((r) => {
      release = r;
    });
    const { user, table, fetchMock } = await openHistory({
      'GET /telemetry/imports/im-1/deletion-plan': PLAN,
      'DELETE /telemetry/imports/im-1': async () => {
        await inFlight;
        return { importId: 'im-1', completed: true, databaseRecordsRemoved: 1207, objectsRemoved: 3, preserved: [KEPT_ALWAYS], remainingWork: [] };
      },
    });
    const dialog = await openDeleteDialog(user, table);
    const confirm = await waitFor(() => {
      const b = within(dialog).getByRole('button', { name: 'Delete import' });
      expect(b).toBeEnabled();
      return b;
    });
    await user.dblClick(confirm);
    expect(confirm).toBeDisabled();
    release();
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(deleteCalls(fetchMock)).toBe(1);
  });

  it('completed: true reports the removal and refreshes the list from the server', async () => {
    const { user, table, fetchMock } = await openHistory({
      'GET /telemetry/imports/im-1/deletion-plan': PLAN,
      'DELETE /telemetry/imports/im-1': { importId: 'im-1', completed: true, databaseRecordsRemoved: 1207, objectsRemoved: 3, preserved: [KEPT_ALWAYS], remainingWork: [] },
    });
    const before = listCalls(fetchMock);
    const dialog = await openDeleteDialog(user, table);
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Delete import' })).toBeEnabled());
    await user.click(within(dialog).getByRole('button', { name: 'Delete import' }));

    expect(await screen.findByText('Import removed: batch.json')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(() => expect(listCalls(fetchMock)).toBeGreaterThan(before));
  });

  it('completed: false is a partial failure: no success wording, remaining work listed, retry offered', async () => {
    const REMAINING = ['bronze/telemetry/2026/10/21/batch.json: connection refused', 'silver/batch_id=b-7/: 2 objects could not be removed'];
    const { user, table, fetchMock } = await openHistory({
      'GET /telemetry/imports/im-1/deletion-plan': PLAN,
      'DELETE /telemetry/imports/im-1': { importId: 'im-1', completed: false, databaseRecordsRemoved: 1207, objectsRemoved: 0, preserved: [KEPT_ALWAYS], remainingWork: REMAINING },
    });
    const before = listCalls(fetchMock);
    const dialog = await openDeleteDialog(user, table);
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Delete import' })).toBeEnabled());
    await user.click(within(dialog).getByRole('button', { name: 'Delete import' }));

    // the dialog stays open with an explicit INCOMPLETE warning
    expect(await screen.findByText('Deletion is INCOMPLETE')).toBeInTheDocument();
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    for (const line of REMAINING) expect(screen.getByText(line)).toBeInTheDocument();
    expect(screen.getByText(/Deleting again is safe/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Retry delete' })).toBeInTheDocument();

    // nothing anywhere may read as a success
    expect(screen.queryByText(/deleted successfully/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/success/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/Import removed/)).not.toBeInTheDocument();

    // the database half did succeed, so the list is refreshed anyway
    await waitFor(() => expect(listCalls(fetchMock)).toBeGreaterThan(before));

    // the retry re-issues the DELETE
    await user.click(screen.getByRole('button', { name: 'Retry delete' }));
    await waitFor(() => expect(deleteCalls(fetchMock)).toBe(2));
  });

  it('400 import_in_progress shows the server message and deletes nothing', async () => {
    const QUEUED = [imp({ id: 'im-1', fileName: 'batch.json', status: 'Queued' })];
    const { user, table, fetchMock } = await openHistory(
      {
        'GET /telemetry/imports/im-1/deletion-plan': { ...PLAN, status: 'Queued', telemetryRecords: 0, rejectedRows: 0, pipelineRuns: 0 },
        'DELETE /telemetry/imports/im-1': () =>
          problem(400, 'import_in_progress', 'Business rule violation', { detail: 'This import is still being processed. Wait for it to finish before deleting it.' }),
      },
      QUEUED,
    );
    const dialog = await openDeleteDialog(user, table);
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Delete import' })).toBeEnabled());
    await user.click(within(dialog).getByRole('button', { name: 'Delete import' }));

    expect(await within(dialog).findByText('This import is still being processed. Wait for it to finish before deleting it.')).toBeInTheDocument();
    expect(deleteCalls(fetchMock)).toBe(1); // issued once, not retried automatically
    expect(screen.queryByText(/Import removed/)).not.toBeInTheDocument();
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });

  it('403 surfaces the error and leaves the row in the table', async () => {
    const { user, table } = await openHistory({
      'GET /telemetry/imports/im-1/deletion-plan': PLAN,
      'DELETE /telemetry/imports/im-1': () => problem(403, 'forbidden', 'You do not have permission to perform this action.'),
    });
    const dialog = await openDeleteDialog(user, table);
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Delete import' })).toBeEnabled());
    await user.click(within(dialog).getByRole('button', { name: 'Delete import' }));

    expect(await within(dialog).findByText('You do not have permission to perform this action.')).toBeInTheDocument();
    expect(within(table).getByRole('button', { name: 'batch.json' })).toBeInTheDocument();
  });

  it('404 surfaces the error without crashing the table', async () => {
    const { user, table } = await openHistory({
      'GET /telemetry/imports/im-1/deletion-plan': PLAN,
      'DELETE /telemetry/imports/im-1': () => problem(404, 'not_found', 'The requested item was not found.'),
    });
    const dialog = await openDeleteDialog(user, table);
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Delete import' })).toBeEnabled());
    await user.click(within(dialog).getByRole('button', { name: 'Delete import' }));

    expect(await within(dialog).findByText('The requested item was not found.')).toBeInTheDocument();
    expect(screen.getByRole('table', { name: 'Import history' })).toBeInTheDocument();
  });

  it('cancelling the dialog sends no DELETE', async () => {
    const { user, table, fetchMock } = await openHistory({ 'GET /telemetry/imports/im-1/deletion-plan': PLAN });
    const dialog = await openDeleteDialog(user, table);
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(deleteCalls(fetchMock)).toBe(0);
  });
});
