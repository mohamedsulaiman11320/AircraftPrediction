import { ACCOUNTS, API_BASE, apiToken, expect, signIn, test } from './fixtures.js';

test.describe('Data Engineer screens against live data', () => {
  test('Data Model shows upload and import history without the Bronze object key', async ({ page, diagnostics }) => {
    await signIn(page, ACCOUNTS.dataEngineer);
    await page.getByRole('navigation', { name: /primary/i }).getByRole('link', { name: 'Data Model' }).click();

    await expect(page.getByRole('heading', { level: 1, name: 'Data Model' })).toBeVisible();
    await expect(page.getByRole('tab', { name: /upload & ingestion/i })).toBeVisible();

    // Import history is populated from the real ETL run.
    const table = page.getByRole('table').first();
    await expect(table).toBeVisible();
    const headers = (await table.getByRole('columnheader').allTextContents()).map((h) => h.trim().toLowerCase());
    expect(headers.some((h) => h.includes('bronze'))).toBe(false);
    expect(headers.some((h) => h.includes('object key'))).toBe(false);
    expect(headers.some((h) => h.includes('file'))).toBe(true);

    expect(diagnostics.consoleErrors, diagnostics.consoleErrors.join('\n')).toEqual([]);
    expect(diagnostics.failedRequests, diagnostics.failedRequests.join('\n')).toEqual([]);
  });

  test('the row menu previews a real deletion plan and cancelling deletes nothing', async ({ page, diagnostics }) => {
    // Safety first: this test must never remove real data, so every DELETE is recorded and asserted.
    const deletes = [];
    page.on('request', (req) => {
      if (req.method() === 'DELETE') deletes.push(`${req.method()} ${req.url()}`);
    });

    await signIn(page, ACCOUNTS.dataEngineer);
    await page.goto('/imports');

    const table = page.getByRole('table', { name: 'Import history' });
    await expect(table).toBeVisible();
    const trigger = table.getByRole('button', { name: /^Actions for / }).first();
    expect(await trigger.count(), 'no import rows to act on').toBeGreaterThan(0);

    await trigger.click();
    const menu = page.getByRole('menu');
    await expect(menu).toBeVisible();
    await expect(trigger).toHaveAttribute('aria-expanded', 'true');

    await menu.getByRole('menuitem', { name: 'Delete import' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible();
    await expect(dialog.getByText(/Permanently delete the import/)).toBeVisible();

    // The plan comes from GET .../deletion-plan and is read-only.
    await expect(dialog.getByText('What would be removed')).toBeVisible();
    await expect(dialog.getByText('Telemetry records')).toBeVisible();
    await expect(dialog.getByText('What is kept')).toBeVisible();
    const kept = dialog.getByRole('list', { name: 'What is kept' });
    await expect(kept.getByRole('listitem').first()).toBeVisible();

    // Cancel — nothing is deleted in the browser test.
    await dialog.getByRole('button', { name: 'Cancel' }).click();
    await expect(dialog).toHaveCount(0);

    expect(deletes, deletes.join('\n')).toEqual([]);
    expect(diagnostics.consoleErrors, diagnostics.consoleErrors.join('\n')).toEqual([]);
  });

  test('upload rejects a non-JSON file before anything is stored', async ({ page }) => {
    await signIn(page, ACCOUNTS.dataEngineer);
    await page.goto('/imports');

    const chooser = page.locator('input[type="file"]').first();
    await chooser.setInputFiles({
      name: 'not-telemetry.txt',
      mimeType: 'text/plain',
      buffer: Buffer.from('this is not json'),
    });

    // The client refuses the extension; no import row is created for it.
    await expect(page.getByText(/\.json/i).first()).toBeVisible();
    await expect(page.getByRole('cell', { name: 'not-telemetry.txt' })).toHaveCount(0);
  });

  test('Telemetry defaults to the first aircraft with a date range applied', async ({ page }) => {
    await signIn(page, ACCOUNTS.dataEngineer);
    await page.goto('/telemetry');

    const aircraft = page.locator('select').first();
    await expect(aircraft).toBeVisible();
    await expect.poll(async () => await aircraft.inputValue(), { timeout: 20_000 }).not.toBe('');

    // Sensor ID filter and the Sensor/Source columns are gone from this page.
    await expect(page.getByLabel(/sensor id/i)).toHaveCount(0);
    const headers = (await page.getByRole('columnheader').allTextContents()).map((h) => h.trim().toLowerCase());
    expect(headers).not.toContain('sensor');
    expect(headers).not.toContain('source');

    const dates = page.locator('input[type="date"]');
    expect(await dates.count()).toBeGreaterThan(0);
    await expect.poll(async () => await dates.first().inputValue()).not.toBe('');
  });

  test('Pipeline shows run history and no manual job controls', async ({ page }) => {
    // Nothing on this page may queue a job any more.
    const jobRequests = [];
    page.on('request', (req) => {
      const url = req.url();
      if (/\/pipelines\/(jobs|dataset-audit)|\/runs\/[^/]+\/cancel/.test(url)) {
        jobRequests.push(`${req.method()} ${url}`);
      }
    });

    await signIn(page, ACCOUNTS.dataEngineer);
    await page.goto('/pipelines');

    await expect(page.getByRole('heading', { level: 1, name: 'Pipeline' })).toBeVisible();
    await expect(page.getByText(/queue ml jobs and review run history/i)).toHaveCount(0);
    await expect(page.getByText(/start a job/i)).toHaveCount(0);
    await expect(page.getByRole('tab', { name: /dataset audit/i })).toHaveCount(0);

    // Real runs from the ETL and ML jobs are listed. Note: the run rows are themselves buttons
    // labelled with the run's job type ("Train failure", "Telemetry import", ...) — those open the
    // run detail, they do not start anything, which is what the request assertion below proves.
    await expect(page.getByRole('table').first()).toBeVisible();
    await expect(page.getByRole('button', { name: /telemetry import/i }).first()).toBeVisible();

    expect(jobRequests, jobRequests.join('\n')).toEqual([]);
  });

  test('Data Quality shows duplicate evidence and not the retired sentence', async ({ page }) => {
    await signIn(page, ACCOUNTS.dataEngineer);
    await page.goto('/data-quality');

    await expect(page.getByRole('heading', { level: 1, name: 'Data Quality' })).toBeVisible();
    await expect(page.getByText(/only physical-plausibility checks are applied/i)).toHaveCount(0);
    await expect(page.getByText(/duplicate/i).first()).toBeVisible();
  });
});

test.describe('model approval authorization, end to end', () => {
  test('only the Data Engineer may approve, and the evidence gate still refuses', async ({ request }) => {
    const [de, dispatcher, technician] = await Promise.all([
      apiToken(request, ACCOUNTS.dataEngineer),
      apiToken(request, ACCOUNTS.dispatcher),
      apiToken(request, ACCOUNTS.technician),
    ]);

    const list = await request.get(`${API_BASE}/api/v1/models?modelType=FailurePrediction`, {
      headers: { Authorization: `Bearer ${de}` },
    });
    expect(list.ok()).toBeTruthy();
    const models = await list.json();
    // A still-unapproved candidate whose test split is too small to support its metrics (since 2026-10-27 the newest
    // failure model is the approved, Active one, so "the first model" is no longer such a candidate).
    const weak = models.find((m) => m.status === 'Trained' && (m.metrics?.test?.negatives ?? 0) < 30);
    test.skip(!weak, 'no unapproved candidate with an insufficient test split present');
    const id = weak.id;
    const body = { notes: 'browser authorization check' };

    for (const token of [dispatcher, technician]) {
      const denied = await request.post(`${API_BASE}/api/v1/models/${id}/approve`, {
        headers: { Authorization: `Bearer ${token}` }, data: body,
      });
      expect(denied.status()).toBe(403);
    }

    // The Data Engineer is authorised, but the candidate's test split cannot support its metrics.
    const attempt = await request.post(`${API_BASE}/api/v1/models/${id}/approve`, {
      headers: { Authorization: `Bearer ${de}` }, data: body,
    });
    expect(attempt.status()).toBe(400);
    expect((await attempt.json()).code).toBe('insufficient_test_examples');

    // And the candidate is still unapproved afterwards.
    const after = await request.get(`${API_BASE}/api/v1/models/${id}`, {
      headers: { Authorization: `Bearer ${de}` },
    });
    expect((await after.json()).status).toBe('Trained');
  });

  test('an unauthenticated approve request is rejected', async ({ request }) => {
    const response = await request.post(
      `${API_BASE}/api/v1/models/00000000-0000-0000-0000-000000000000/approve`,
      { data: { notes: 'x' } });
    expect(response.status()).toBe(401);
  });
});

test.describe('Dispatcher Fleet Health uses live PostgreSQL data', () => {
  test('fleet health lists the ingested aircraft with real readings', async ({ page, diagnostics }) => {
    await signIn(page, ACCOUNTS.dispatcher);
    await page.getByRole('navigation', { name: /primary/i }).getByRole('link', { name: 'Fleet Health' }).click();

    await expect(page.getByRole('heading', { level: 1, name: /fleet health/i })).toBeVisible();

    // The six ETL-registered tail numbers come from the database, not fixtures.
    const table = page.getByRole('table').first();
    await expect(table).toBeVisible();
    const body = await table.textContent();
    expect(body).toMatch(/N\d{3}UA/);

    expect(diagnostics.failedRequests, diagnostics.failedRequests.join('\n')).toEqual([]);
  });

  test('risk shows the scored category but no probability while calibration is unvalidated', async ({ page, request }) => {
    // The API is the contract: a descriptive indicator carries a category and a NULL probability.
    const token = await apiToken(request, ACCOUNTS.dispatcher);
    const response = await request.get(`${API_BASE}/api/v1/aircraft?page=1&pageSize=10`, {
      headers: { Authorization: `Bearer ${token}` },
    });
    const { items } = await response.json();
    const scored = items.filter((a) => a.latestRisk);
    test.skip(scored.length === 0, 'no scoring run has produced risk indicators yet');

    for (const aircraft of scored) {
      // Since 2026-10-27 an approved failure model is Active, so the latest row may be model-based; before that
      // only descriptive indicators existed.
      expect(['DescriptiveIndicator', 'Model']).toContain(aircraft.latestRisk.basis);
      expect(aircraft.latestRisk.riskCategory).toMatch(/^(Low|Elevated|High|Critical|HighCritical|Undetermined)$/);   // Critical / HighCritical: forecast condition (2026-10-28)
      // A probability is served only when calibration is validated; the active model's calibration is NOT validated.
      expect(aircraft.health?.failureRiskScore ?? null).toBeNull();
    }

    await signIn(page, ACCOUNTS.dispatcher);
    await page.goto('/fleet');
    const table = page.getByRole('table').first();
    await expect(table).toBeVisible();

    // The scored category is now rendered instead of "no assessment".
    const text = await table.textContent();
    expect(text).toMatch(/Low|Elevated|High|Undetermined/);
  });

  test('the development lab shows live synthetic candidates and predictions, clearly separated and never promotable', async ({ page, diagnostics }) => {
    await signIn(page, ACCOUNTS.dataEngineer);
    await page.goto('/models/development');

    await expect(page.getByText(/Synthetic development data — aircraft_predict_db_synth/)).toBeVisible();
    await expect(page.getByRole('tab', { name: 'Development (synthetic)' })).toHaveAttribute('aria-selected', 'true');
    const candidates = page.getByRole('table', { name: 'Candidates' });
    await expect(candidates.getByText('RandomForest').first()).toBeVisible();
    // Every aircraft scored by a development candidate (from GET /development/predictions, not fixtures).
    const predictions = page.getByRole('table', { name: 'Development predictions' });
    await expect(predictions.getByRole('row')).toHaveCount(7);      // header + 6 aircraft
    for (const name of [/approve/i, /activate/i, /register/i]) {
      await expect(page.getByRole('button', { name })).toHaveCount(0);
    }
    expect(diagnostics.consoleErrors, diagnostics.consoleErrors.join('\n')).toEqual([]);
    expect(diagnostics.failedRequests, diagnostics.failedRequests.join('\n')).toEqual([]);
  });
});
