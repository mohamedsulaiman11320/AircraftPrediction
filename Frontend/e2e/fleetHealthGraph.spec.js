import { ACCOUNTS, expect, signIn, test } from './fixtures.js';

test.describe('Fleet Health telemetry graph against live data', () => {
  test('plots historical telemetry as recorded readings, with no stale forecast banner and no probability', async ({ page, diagnostics }) => {
    await signIn(page, ACCOUNTS.dispatcher);
    await page.getByRole('navigation', { name: /primary/i }).getByRole('link', { name: 'Fleet Health' }).click();
    await expect(page.getByRole('heading', { level: 1, name: /fleet health/i })).toBeVisible();

    // The graph panel is fed by the real aircraft list and GET /telemetry/series.
    const panel = page.getByRole('region', { name: 'Telemetry history' });
    await expect(panel).toBeVisible();
    const aircraft = panel.getByRole('combobox', { name: 'Aircraft' });
    await expect.poll(async () => await aircraft.inputValue(), { timeout: 20_000 }).not.toBe('');
    for (const label of await aircraft.locator('option').allTextContents()) expect(label).not.toMatch(/Unknown/);

    // Hand-rolled SVG charts: temperature, pressure and vibration each on their own labelled axis.
    await expect(panel.locator('svg').first()).toBeVisible({ timeout: 20_000 });
    for (const heading of ['Engine temperature (°C)', 'Oil pressure (PSI)', 'Vibration index (dimensionless)']) {
      const chart = panel.getByRole('region', { name: heading });
      await expect(chart).toBeVisible();
      expect(await chart.locator('svg').count()).toBe(1);
    }

    await expect(panel.getByTestId('recorded-history-note')).toContainText('Recorded readings');
    await expect(page.getByText(/Forecast unavailable/)).toHaveCount(0);

    // Neither the table nor the graph presents a failure probability as a percentage (the condition card's shares
    // of the fleet are the only percentages on the page).
    for (const text of [await page.getByRole('table', { name: 'Fleet health' }).textContent(), await panel.textContent()]) {
      expect(text || '').not.toMatch(/\d+(\.\d+)?\s*%/);
    }

    expect(diagnostics.consoleErrors, diagnostics.consoleErrors.join('\n')).toEqual([]);
    expect(diagnostics.failedRequests, diagnostics.failedRequests.join('\n')).toEqual([]);
  });

  test('the Aircraft health card counts each condition and filters the table on click', async ({ page }) => {
    await signIn(page, ACCOUNTS.dispatcher);
    await page.goto('/fleet');
    const card = page.getByTestId('fleet-condition');
    await expect(card).toBeVisible();
    const table = page.getByRole('table', { name: 'Fleet health' });
    const count = async (key) => Number((await card.getByTestId(`condition-${key}`).getByTestId('condition-count').textContent()) || 0);
    const expected = { available: 4, critical: 1, highCritical: 1 };
    for (const [key, n] of Object.entries(expected)) expect(await count(key), key).toBe(n);
    await card.getByTestId('condition-highCritical').click();
    await expect(table.getByRole('row')).toHaveCount(2);
    await card.getByTestId('condition-highCritical').click();
    await expect(table.getByRole('row')).toHaveCount(7);
    await expect(table).not.toContainText('Unassigned');
    // The risk indicator follows the forecast condition: 4 Low, 1 Critical, 1 High Critical.
    // The Risk indicator cell holds the indicator badge first, then the threshold status.
    const risks = await table.getByRole('row').evaluateAll((rows) => rows.slice(1).map((r) => r.querySelectorAll('td')[2]?.querySelector('.badge')?.textContent?.trim() || ''));
    expect(risks.filter((r) => /Low$/.test(r))).toHaveLength(4);                    // the badge carries a hidden "Risk" prefix
    expect(risks.filter((r) => /(?<!High )Critical$/.test(r))).toHaveLength(1);
    expect(risks.filter((r) => /High Critical$/.test(r))).toHaveLength(1);
  });
});
