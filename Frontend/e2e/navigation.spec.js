import { ACCOUNTS, API_BASE, apiToken, expect, navLabels, signIn, signInThroughForm, test } from './fixtures.js';

test.describe('role navigation in a real browser', () => {
  test('the real login form signs a user in', async ({ page, diagnostics }) => {
    await signInThroughForm(page, ACCOUNTS.dataEngineer);

    await expect(page.getByRole('navigation', { name: /primary/i })).toBeVisible();
    expect(diagnostics.failedRequests, diagnostics.failedRequests.join('\n')).toEqual([]);
  });

  test('Data Engineer sees its five menus, including Model Versions and User Management', async ({ page, diagnostics }) => {
    await signIn(page, ACCOUNTS.dataEngineer);

    expect(await navLabels(page)).toEqual(['Data Model', 'Pipeline', 'Data Quality', 'Model Versions', 'User Management']);
    await expect(page.getByRole('navigation', { name: /primary/i })
      .getByRole('link', { name: /model & forecast/i })).toHaveCount(0);

    expect(diagnostics.consoleErrors, diagnostics.consoleErrors.join('\n')).toEqual([]);
    expect(diagnostics.failedRequests, diagnostics.failedRequests.join('\n')).toEqual([]);
  });

  test('Dispatcher has four menus and no User Management', async ({ page }) => {
    await signIn(page, ACCOUNTS.dispatcher);

    expect(await navLabels(page)).toEqual(['Health Dashboard', 'Fleet Health', 'Anomalies / Work Orders', 'Maintenance Log']);
    // Not just hidden: the route guard refuses it, and so does the API (Policies.DataEngineer).
    await page.goto('/admin/users');
    await expect(page).toHaveURL(/access-denied/);
  });

  test('the Anomalies / Work Orders menu owns the work-order screens', async ({ page }) => {
    await signIn(page, ACCOUNTS.dispatcher);

    const nav = page.getByRole('navigation', { name: /primary/i });
    await nav.getByRole('link', { name: 'Anomalies / Work Orders', exact: true }).click();
    await expect(page).toHaveURL(/\/anomalies/);

    // The work-order route is reached under the same menu entry, which stays marked current.
    await page.goto('/work-orders');
    await expect(nav.getByRole('link', { name: 'Anomalies / Work Orders', exact: true })).toHaveAttribute('aria-current', 'page');
  });

  test('the Maintenance Log menu opens the maintenance log itself', async ({ page }) => {
    await signIn(page, ACCOUNTS.dispatcher);

    await page.getByRole('navigation', { name: /primary/i }).getByRole('link', { name: 'Maintenance Log' }).click();

    await expect(page.getByRole('heading', { level: 1, name: 'Maintenance Log' })).toBeVisible();
    await expect(page.getByLabel(/event type/i)).toBeVisible();
  });

  test('Technician keeps its single menu', async ({ page }) => {
    await signIn(page, ACCOUNTS.technician);

    expect(await navLabels(page)).toEqual(['My tickets']);
  });

  test('a de-navigated screen is still refused by route guard, not just hidden', async ({ page }) => {
    await signIn(page, ACCOUNTS.dispatcher);

    await page.goto('/models');

    // The dispatcher no longer holds the model area: the guard redirects rather than rendering it.
    await expect(page).toHaveURL(/access-denied/);
  });
});

/** 2026-10-28 layout brief: the title row carries the avatar everywhere, and no page scrolls sideways. */
test.describe('global title row (tab row on Anomalies / Work orders) and full-width layout', () => {
  const PAGES = ['/fleet', '/anomalies', '/work-orders', '/maintenance-log'];

  for (const width of [1366, 1440, 1920]) {
    test(`no horizontal page scroll at ${width}px, and the avatar sits in each page's title row`, async ({ page }) => {
      await signIn(page, ACCOUNTS.dispatcher);
      await page.setViewportSize({ width, height: 900 });
      for (const path of PAGES) {
        await page.goto(path);
        const header = page.locator('header.page-header');
        await expect(header.getByRole('heading', { level: 1 })).toBeVisible();
        await expect(page.locator('header.topnav')).toBeHidden();                       // no blank bar above the title
        await expect(page.getByTestId('profile-avatar')).toHaveCount(1);
        const tabsBar = page.getByTestId('operations-tabs-bar');
        if (await tabsBar.count()) {
          // Anomalies / Work orders: the avatar sits at the right end of the tab row, vertically centred; not in the title row.
          await expect(header.getByTestId('profile-avatar')).toHaveCount(0);
          const bar = await tabsBar.boundingBox();
          const avatar = await tabsBar.getByTestId('profile-avatar').boundingBox();
          expect(Math.abs(avatar.y + avatar.height / 2 - (bar.y + bar.height / 2)), `${path}: centred in the tab row`).toBeLessThanOrEqual(4);
          expect(avatar.x + avatar.width, `${path}: not clipped`).toBeLessThanOrEqual(bar.x + bar.width + 1);
          expect(bar.x + bar.width - (avatar.x + avatar.width), `${path}: at the right end`).toBeLessThanOrEqual(4);
        } else {
          await expect(header.getByTestId('profile-avatar')).toBeVisible();
          const heading = await header.getByRole('heading', { level: 1 }).boundingBox();
          const avatar = await header.getByTestId('profile-avatar').boundingBox();
          expect(avatar.y, `${path}: avatar in the title row`).toBeLessThan(heading.y + heading.height);
        }
        await page.waitForLoadState('networkidle');
        const widths = await page.evaluate(() => ({ doc: document.documentElement.scrollWidth, view: window.innerWidth }));
        expect(widths.doc, `${path} at ${width}px`).toBeLessThanOrEqual(widths.view);
      }
    });
  }

  test('the title-row avatar opens the profile popover with Sign out on every page', async ({ page }) => {
    await signIn(page, ACCOUNTS.dispatcher);
    for (const path of PAGES) {
      await page.goto(path);
      await page.getByTestId('profile-avatar').click();                                // tab row or title row
      await expect(page.getByTestId('profile-popover')).toBeVisible();
      await expect(page.getByTestId('profile-role')).toContainText('Flight Dispatcher');
      await expect(page.getByTestId('sign-out')).toBeVisible();
      await page.keyboard.press('Escape');
      await expect(page.getByTestId('profile-popover')).toHaveCount(0);
    }
  });

  test('the Fleet Health table fills the canvas with six columns and no clipped cells', async ({ page }) => {
    await signIn(page, ACCOUNTS.dispatcher);
    await page.setViewportSize({ width: 1366, height: 900 });
    await page.goto('/fleet');
    const table = page.getByRole('table', { name: 'Fleet health' });
    await expect(table.getByRole('row')).toHaveCount(7);
    await expect(table.getByRole('columnheader')).toHaveCount(6);
    const fit = await table.evaluate((t) => {
      const wrap = t.parentElement;
      const clipped = [...t.querySelectorAll('td')].filter((td) => td.scrollWidth > td.clientWidth + 1).length;
      return { table: t.getBoundingClientRect().width, wrap: wrap.clientWidth, overflow: wrap.scrollWidth > wrap.clientWidth, clipped };
    });
    expect(fit.overflow, 'no scrolling inside the table wrapper').toBe(false);
    expect(Math.abs(fit.table - fit.wrap)).toBeLessThanOrEqual(1);                          // 100% of the canvas
    expect(fit.clipped).toBe(0);
  });

  test('one anomaly per aircraft and first breaching hour (no duplicates at N237UA 2026-10-27 19:00 UTC)', async ({ request }) => {
    const token = await apiToken(request, ACCOUNTS.dispatcher);
    const auth = { headers: { Authorization: `Bearer ${token}` } };
    const anomalies = await (await request.get(`${API_BASE}/api/v1/anomalies?source=ForecastModel&pageSize=200`, auth)).json();
    const keys = anomalies.items.map((a) => `${a.registrationNumber}|${a.observedAtUtc}`);
    expect(new Set(keys).size, 'duplicate (aircraft, first breach) anomalies').toBe(keys.length);
    const n237 = anomalies.items.filter((a) => a.registrationNumber === 'N237UA' && Date.parse(a.observedAtUtc) === Date.parse('2026-10-27T19:00:00Z'));
    expect(n237).toHaveLength(1);
    expect(n237[0].ruleCodes.split(',').sort()).toEqual(['EngineTemperatureHighC', 'OilPressureLowPsi']);
  });

  test('no internal rule code (BR-0x) appears on any main screen or record page', async ({ page, request }) => {
    const token = await apiToken(request, ACCOUNTS.dispatcher);
    const auth = { headers: { Authorization: `Bearer ${token}` } };
    const anomalies = (await (await request.get(`${API_BASE}/api/v1/anomalies?pageSize=5`, auth)).json()).items;
    const orders = (await (await request.get(`${API_BASE}/api/v1/work-orders?pageSize=5`, auth)).json()).items;
    const aircraft = (await (await request.get(`${API_BASE}/api/v1/aircraft?pageSize=10`, auth)).json()).items;
    const paths = ['/dashboard/dispatcher', ...PAGES, ...aircraft.map((a) => `/fleet/${a.id}`),
      ...anomalies.map((a) => `/anomalies/${a.id}`), ...orders.map((w) => `/work-orders/${w.id}`)];
    await signIn(page, ACCOUNTS.dispatcher);
    for (const path of paths) {
      await page.goto(path);
      await page.waitForLoadState('networkidle');
      await page.locator('details').evaluateAll((els) => els.forEach((d) => { d.open = true; }));   // technical sections too
      expect(await page.locator('body').innerText(), path).not.toMatch(/BR-0\d/);
    }
  });
});
