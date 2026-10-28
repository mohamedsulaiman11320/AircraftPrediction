import { ACCOUNTS, API_BASE, apiToken, expect, signIn, test } from './fixtures.js';

/**
 * Read-only check of an automatically raised work order against the live API: the dispatcher list shows
 * its Source, and the detail page shows the stored deterministic diagnosis and the anomaly's rule codes.
 * Nothing is assigned, resolved or closed here.
 */
test.describe('Automatic work order diagnosis against live data', () => {
  test('dispatcher opens an Automatic ticket and sees the diagnosis and evidence', async ({ page, request, diagnostics }) => {
    await signIn(page, ACCOUNTS.dispatcher);
    await page.goto('/work-orders');
    await expect(page.getByRole('heading', { level: 1, name: 'Work orders' })).toBeVisible();

    const table = page.getByRole('table', { name: 'Work orders' });
    const autoRow = table.getByRole('row').filter({ has: page.locator('.badge', { hasText: /Source: Automatic$/ }) }).first();
    await expect(autoRow).toBeVisible();
    const link = autoRow.getByRole('link', { name: /^WO-/ });
    const number = (await link.textContent()).trim();
    await link.click();
    await expect(page.getByRole('heading', { level: 1, name: new RegExp(number) })).toBeVisible();

    // The same ticket straight from the API, for the expected values.
    const id = page.url().split('/work-orders/')[1];
    const token = await apiToken(request, ACCOUNTS.dispatcher);
    const wo = await (await request.get(`${API_BASE}/api/v1/work-orders/${id}`, { headers: { Authorization: `Bearer ${token}` } })).json();
    expect(wo.source).toBe('Automatic');

    const diagnosis = page.getByRole('region', { name: 'Diagnosis' });
    await expect(diagnosis.getByText(/Deterministic rules — not a confirmed fault/)).toBeVisible();
    await expect(diagnosis.getByTestId('diagnosis-method')).toHaveText('deterministic-rules');
    if (wo.anomaly?.source === 'ForecastModel') {
      // Forecast-raised tickets: plain English, no rule references, and never stated as a confirmed fault.
      await expect(diagnosis.getByTestId('diagnosis-text')).toContainText('30-day sensor forecast');
      await expect(diagnosis.getByTestId('diagnosis-text')).toContainText('not a confirmed fault');
      await expect(diagnosis.getByTestId('diagnosis-text')).not.toContainText('BR-01');
    } else {
      await expect(diagnosis.getByTestId('diagnosis-text')).toContainText('deterministic rules');
    }

    const evidence = page.getByRole('region', { name: 'Evidence' });
    const codes = String(wo.anomaly?.ruleCodes || '')
      .split(',')
      .map((c) => c.trim())
      .filter(Boolean);
    expect(codes.length).toBeGreaterThan(0);
    for (const code of codes) {
      await expect(evidence.getByTestId('evidence-rule-codes').getByText(code, { exact: true })).toBeVisible();
    }

    expect(diagnostics.consoleErrors, diagnostics.consoleErrors.join('\n')).toEqual([]);
    expect(diagnostics.failedRequests, diagnostics.failedRequests.join('\n')).toEqual([]);
  });
});
