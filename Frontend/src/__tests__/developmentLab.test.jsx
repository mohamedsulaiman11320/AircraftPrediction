import { describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AppRoutes } from '../App.jsx';
import { USERS, mockFetch, renderApp } from '../test/utils.jsx';

const NOTICE = 'Development environment on SYNTHETIC data. Results exercise the training and scoring pipeline only.';
const STATUS = { configured: true, database: 'aircraft_predict_db_synth', notice: NOTICE, queueableJobs: ['DatasetAudit', 'TrainFailure', 'ScoreCandidate'] };

const candidate = {
  id: 'm-1', modelName: 'afp-failure-prediction', version: '20261027065330', modelType: 'FailurePrediction',
  algorithm: 'RandomForest', status: 'Trained',
  metrics: {
    calibration_validated: true,
    // roc_auc deliberately absent: the page must show "—", never a default or a guess.
    test: { positives: 180, negatives: 150, precision: 1, recall: 0.9, specificity: 0.98, pr_auc: 0.99, pr_auc_baseline: 0.545 },
  },
};
const scored = {
  id: 'p-1', registrationNumber: 'N120UA', predictionState: 'Scored', probability: 0.992, probabilityValidated: true,
  riskCategory: 'High', featureCutoffUtc: '2026-10-24T00:00:00Z', modelVersion: '20261027065330', horizonDays: 30,
};
const unvalidated = { ...scored, id: 'p-2', registrationNumber: 'N237UA', probability: null, probabilityValidated: false };

function routes(extra = {}) {
  return {
    'GET /auth/me': USERS.DataEngineer,
    'GET /development': STATUS,
    'GET /development/models': [candidate],
    'GET /development/predictions': [scored, unvalidated],
    'GET /development/runs': [],
    ...extra,
  };
}

describe('development lab (isolated synthetic environment)', () => {
  it('labels everything as synthetic and shows stored metrics, with "—" for a metric that was not computed', async () => {
    mockFetch(routes());
    renderApp(<AppRoutes />, { route: '/models/development', user: USERS.DataEngineer });
    expect(await screen.findByText(NOTICE)).toBeInTheDocument();
    expect(screen.getByText(/Synthetic development data — aircraft_predict_db_synth/)).toBeInTheDocument();
    const tabs = screen.getByRole('tablist', { name: 'Model environments' });
    expect(within(tabs).getByRole('tab', { name: 'Development (synthetic)' })).toHaveAttribute('aria-selected', 'true');

    const candidates = await screen.findByRole('table', { name: 'Candidates' });
    const row = within(candidates).getAllByRole('row')[1];
    expect(within(row).getByText('20261027065330')).toBeInTheDocument();
    expect(within(row).getByText('180 / 150')).toBeInTheDocument();
    expect(within(row).getByText('0.980')).toBeInTheDocument();       // specificity as stored
    expect(within(row).getByText('—')).toBeInTheDocument();           // ROC-AUC not stored -> not invented
    expect(within(row).getByText('0.990 (0.545)')).toBeInTheDocument();
    expect(within(row).getByText('Local only')).toBeInTheDocument();   // no artifactUri -> not claimed as stored
  });

  it('shows a probability only when validated and never offers approve or activate', async () => {
    mockFetch(routes());
    renderApp(<AppRoutes />, { route: '/models/development', user: USERS.DataEngineer });
    const preds = await screen.findByRole('table', { name: 'Development predictions' });
    expect(within(preds).getByText('99.2%')).toBeInTheDocument();
    const n237 = within(preds).getByText('N237UA').closest('tr');
    expect(within(n237).getByText(/Not validated/)).toBeInTheDocument();
    for (const name of [/approve/i, /activate/i, /register/i]) {
      expect(screen.queryByRole('button', { name })).not.toBeInTheDocument();
    }
  });

  it('queues training and candidate scoring through the development API and says it is only queued', async () => {
    const posted = [];
    mockFetch(routes({
      'POST /development/jobs': ({ body }) => {
        posted.push(body);
        return { id: 'run-1', status: 'Queued', statusUrl: '/api/v1/development/runs/run-1' };
      },
    }));
    const user = userEvent.setup();
    renderApp(<AppRoutes />, { route: '/models/development', user: USERS.DataEngineer });
    await user.click(await screen.findByRole('button', { name: 'Train development candidate' }));
    await waitFor(() => expect(posted).toEqual([{ jobType: 'TrainFailure' }]));
    expect(await screen.findByText(/queued\. It runs when the development ML worker picks it up/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Score aircraft' }));
    await waitFor(() => expect(posted[1]).toEqual({ jobType: 'ScoreCandidate', modelVersionId: 'm-1' }));
  });

  it('explains an unconfigured environment instead of showing empty tables', async () => {
    mockFetch(routes({ 'GET /development': { ...STATUS, configured: false, database: null } }));
    renderApp(<AppRoutes />, { route: '/models/development', user: USERS.DataEngineer });
    expect(await screen.findByText('Development environment not configured')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it.each([USERS.FlightDispatcher, USERS.MaintenanceTechnician])('is not reachable by %s', async (u) => {
    mockFetch({ 'GET /auth/me': u });
    renderApp(<AppRoutes />, { route: '/models/development', user: u });
    await waitFor(() => expect(window.location.pathname).toBe('/access-denied'));
  });
});
