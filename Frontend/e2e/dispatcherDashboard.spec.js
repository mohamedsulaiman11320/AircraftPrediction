import { ACCOUNTS, API_BASE, PASSWORD, apiToken, expect, signIn, signInThroughForm, test } from './fixtures.js';

/**
 * The Health Dashboard against the live API and database. Every number on the page is compared with what
 * GET /dashboards/dispatcher returns, so the UI can neither invent nor drop anything; the breach -> anomaly ->
 * work-order path is followed on real persisted records.
 */
async function api(request, path) {
  const token = await apiToken(request, ACCOUNTS.dispatcher);
  const res = await request.get(`${API_BASE}/api/v1${path}`, { headers: { Authorization: `Bearer ${token}` } });
  expect(res.ok(), `${path} -> ${res.status()}`).toBeTruthy();
  return res.json();
}

test.describe('Health Dashboard against live data', () => {
  test('KPI cards, aircraft list, search and chart match the API', async ({ page, request, diagnostics }) => {
    const dash = await api(request, '/dashboards/dispatcher');
    await signIn(page, ACCOUNTS.dispatcher);
    await page.goto('/dashboard/dispatcher');
    await expect(page.getByRole('heading', { level: 1, name: 'Health Dashboard' })).toBeVisible();

    const h = dash.health;
    await expect(page.getByTestId('kpi-threshold').getByRole('group')).toHaveAccessibleName(
      `Threshold health of ${h.aircraft} aircraft: Breaching a limit ${h.highRisk}, Within limits ${h.withinLimits}, No recent data ${h.notAssessable}`,
    );
    // No provisional-score card or banner on the dashboard.
    await expect(page.getByTestId('kpi-risk')).toHaveCount(0);
    await expect(page.getByTestId('health-dashboard')).not.toContainText(/provisional/i);
    const w = dash.workOrders;
    await expect(page.getByTestId('kpi-work-orders').getByRole('group')).toHaveAccessibleName(
      `Work orders: New ${w.open}, Assigned ${w.assigned}, In progress ${w.inInspection}, Reported ${w.resolved}, Completed ${w.closed}`,
    );

    const cards = page.getByTestId('aircraft-list').getByRole('button');
    await expect(cards).toHaveCount(dash.aircraftRisk.length);
    const regs = dash.aircraftRisk.map((r) => r.registrationNumber).sort();
    await expect(cards.first()).toHaveAttribute('aria-pressed', 'true');

    // Selecting another aircraft requests ITS forecast (never recorded telemetry on the Forecast tab).
    const target = dash.aircraftRisk.find((r) => r.registrationNumber === regs[regs.length - 1]);
    const forecast = page.waitForResponse((r) => r.url().includes(`/aircraft/${target.aircraftId}/sensor-forecast`));
    await page.getByTestId(`aircraft-card-${target.registrationNumber}`).click();
    const body = await (await forecast).json();
    await expect(page.getByTestId(`aircraft-card-${target.registrationNumber}`)).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByTestId('selected-aircraft').getByRole('heading', { level: 2 })).toHaveText(target.registrationNumber);
    if (body.status === 'NotAvailable') {
      await expect(page.getByTestId('forecast-unavailable')).toContainText(body.reason);
      await expect(page.getByTestId('chart-forecast')).toHaveCount(0);
    }

    // Search narrows the strip by tail number, case-insensitively.
    await page.getByRole('searchbox', { name: 'Search aircraft' }).fill(regs[0].toLowerCase());
    await expect(cards).toHaveCount(1);
    await expect(cards.first()).toHaveAttribute('data-testid', `aircraft-card-${regs[0]}`);
    await page.getByRole('searchbox', { name: 'Search aircraft' }).fill('');
    await expect(cards).toHaveCount(dash.aircraftRisk.length);

    expect(diagnostics.consoleErrors, diagnostics.consoleErrors.join('\n')).toEqual([]);
    expect(diagnostics.failedRequests, diagnostics.failedRequests.join('\n')).toEqual([]);
  });

  test('a forecast breach marker opens its forecast anomaly, which shows and opens its work order', async ({ page, request, diagnostics }) => {
    const dash = await api(request, '/dashboards/dispatcher');
    const row = dash.aircraftRisk.find((r) => r.forecastAnomalyId);
    test.skip(!row, 'no aircraft has an open forecast anomaly');
    const anomaly = await api(request, `/anomalies/${row.forecastAnomalyId}`);
    expect(anomaly.source).toBe('ForecastModel');
    const orders = await api(request, `/work-orders?anomalyId=${anomaly.id}`);

    await signIn(page, ACCOUNTS.dispatcher);
    await page.goto(`/dashboard/dispatcher?aircraft=${row.registrationNumber}`);
    const code = anomaly.ruleCodes.split(',')[0].trim();
    const metric = { OilPressureLowPsi: 'pressurePsi', EngineTemperatureHighC: 'temperatureC' }[code];
    // No resting dots: hover the first breaching hour (where the invisible keyboard diamond sits) to reveal its node.
    const marker = page.locator(`[data-testid="breach-marker"][data-kind="Forecast"][data-metric="${metric}"]`).first();
    await expect(marker).toHaveCount(1);
    const box = await marker.boundingBox();
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    const node = page.locator(`[data-testid="breach-node"][data-metric="${metric}"]`);
    await expect(node).toBeVisible();
    await expect(page.getByTestId('tooltip-breach').first()).toContainText('Click to view anomaly');

    const writes = [];
    page.on('request', (r) => r.method() !== 'GET' && r.url().includes('/api/') && writes.push(`${r.method()} ${r.url()}`));
    await node.click();
    await expect(page).toHaveURL(new RegExp(`/anomalies/${anomaly.id}$`));
    await expect(page.getByRole('heading', { level: 1 })).toContainText(row.registrationNumber);
    // A plain-English diagnosis: no raw JSON, no rule codes, no legacy banners; model ids behind a closed section.
    const diagnosis = page.getByTestId('forecast-diagnosis');
    await expect(diagnosis.getByTestId('forecast-breach-row').first()).toContainText(/Projected breach.*(Engine temperature high|Oil pressure low)/);
    await expect(diagnosis.getByTestId('forecast-breach-row').first()).toContainText('Recommended action');
    await expect(diagnosis).not.toContainText('{');
    await expect(diagnosis).not.toContainText(/OilPressureLowPsi|EngineTemperatureHighC|BR-0/);
    await expect(diagnosis.getByTestId('technical-details')).not.toHaveAttribute('open', '');
    for (const gone of ['Raised from the 30-day sensor forecast', 'About this evidence', 'The source telemetry record is not available']) {
      await expect(page.getByText(gone)).toHaveCount(0);
    }

    if (orders.items.length) {
      const wo = orders.items[0];
      const details = page.getByTestId('linked-work-order-details');
      await expect(details).toContainText(wo.workOrderNumber);
      await expect(details).not.toContainText('BR-01');                              // plain-English diagnosis
      await page.getByTestId('open-linked-work-order').click();
      await expect(page).toHaveURL(new RegExp(`/work-orders/${wo.id}$`));
      await expect(page.getByText(wo.workOrderNumber).first()).toBeVisible();
    }
    expect(writes, 'opening records must not create or change anything').toEqual([]);
    expect(diagnostics.consoleErrors, diagnostics.consoleErrors.join('\n')).toEqual([]);
  });

  test('a breach node mid-episode opens the forecast anomaly that covers it', async ({ page, request }) => {
    const dash = await api(request, '/dashboards/dispatcher');
    const row = dash.aircraftRisk.find((r) => r.forecastAnomalyId);
    test.skip(!row, 'no aircraft has an open forecast anomaly');
    const open = (await api(request, `/anomalies?aircraftId=${row.aircraftId}&source=ForecastModel&pageSize=100`)).items;
    await signIn(page, ACCOUNTS.dispatcher);
    await page.goto(`/dashboard/dispatcher?aircraft=${row.registrationNumber}&horizon=OneDay`);
    const chart = page.getByTestId('chart-forecast');
    await expect(chart.getByTestId('breach-node')).toHaveCount(0);                     // nothing at rest
    const series = await chart.getByTestId('series-temperatureC').boundingBox();
    await page.mouse.move(series.x + series.width / 2, series.y + series.height / 2);    // mid-run hour
    const node = chart.locator('[data-testid="breach-node"][data-metric="temperatureC"]');
    await expect(node).toBeVisible();
    await node.click();
    await expect(page).toHaveURL(/\/anomalies\/\d+$/);
    const id = Number(page.url().split('/anomalies/')[1]);
    expect(open.map((a) => a.id)).toContain(id);
  });

  test('the aircraft list shows each aircraft state from the API, with no Unassigned label', async ({ page, request }) => {
    const dash = await api(request, '/dashboards/dispatcher');
    const state = Object.fromEntries(dash.readiness.map((r) => [r.registrationNumber, r.derivedState]));
    await signIn(page, ACCOUNTS.dispatcher);
    await page.goto('/dashboard/dispatcher');
    const list = page.getByTestId('aircraft-list');
    await expect(list.getByRole('button')).toHaveCount(dash.aircraftRisk.length);
    await expect(list).not.toContainText('Unassigned');
    const badges = list.getByTestId('state-badge');
    const counts = { Available: 0, UnderMaintenance: 0 };
    for (const r of dash.aircraftRisk) {
      const s = state[r.registrationNumber];
      if (s in counts) counts[s] += 1;
      if (r.forecastCondition) await expect(page.getByTestId(`aircraft-card-${r.registrationNumber}`).getByTestId('forecast-badge')).toBeVisible();
    }
    await expect(badges.filter({ hasText: /^Available$/ })).toHaveCount(counts.Available);
    await expect(badges.filter({ hasText: /^Under maintenance$/ })).toHaveCount(counts.UnderMaintenance);
    // The operating scenario: four aircraft available, the two with a forecast breach under maintenance.
    expect(counts).toEqual({ Available: 4, UnderMaintenance: 2 });
  });

  for (const vp of [
    { width: 1440, height: 900, desktop: true },
    { width: 1366, height: 768, desktop: true },
    { width: 768, height: 1024, desktop: false },
    { width: 390, height: 844, desktop: false },
  ]) {
    test(`layout at ${vp.width}x${vp.height}`, async ({ page }) => {
      await signIn(page, ACCOUNTS.dispatcher);        // at the default size: the fixture waits for the sidebar
      await page.setViewportSize({ width: vp.width, height: vp.height });
      await page.goto('/dashboard/dispatcher');
      await expect(page.getByTestId('selected-aircraft')).toBeVisible();
      await expect(page.getByTestId('forecast-unavailable').or(page.getByTestId('chart-forecast'))).toBeVisible();
      const m = await page.evaluate(() => ({ docW: document.documentElement.scrollWidth }));
      expect(m.docW, 'no horizontal page overflow').toBeLessThanOrEqual(vp.width);
      const list = await page.getByTestId('aircraft-list').boundingBox();
      const panel = await page.getByTestId('selected-aircraft').boundingBox();
      if (vp.desktop) {
        // Aircraft list on the left and the chart area beside it, visible together.
        expect(list.x + list.width).toBeLessThanOrEqual(panel.x);
        expect(Math.abs(list.y - panel.y)).toBeLessThan(80);
      } else {
        expect(list.y + list.height).toBeLessThanOrEqual(panel.y + 1);            // stacked on narrow screens
      }
      expect(await page.getByTestId('aircraft-list').evaluate((el) => getComputedStyle(el).overflowY)).toBe('auto');
      if (vp.desktop) {
        // No page scroll: the document fits the viewport, and the aircraft list scrolls on its own.
        const doc = await page.evaluate(() => ({ h: document.documentElement.scrollHeight, view: window.innerHeight }));
        expect(doc.h, 'no vertical page scroll').toBeLessThanOrEqual(doc.view);
        const scrolled = await page.getByTestId('aircraft-list').evaluate((el) => {
          el.style.maxHeight = '120px';                       // force overflow with only six aircraft
          el.scrollTop = 60;
          const r = { list: el.scrollTop, page: window.scrollY };
          el.style.maxHeight = '';
          return r;
        });
        expect(scrolled.list).toBeGreaterThan(0);
        expect(scrolled.page).toBe(0);
        // The < / > toggle sits in the sidebar brand bar and collapses the sidebar without shifting the content over it.
        const sidebar = page.getByRole('complementary', { name: 'Main navigation' });
        const toggle = sidebar.getByTestId('sidebar-toggle');
        await expect(toggle).toBeVisible();
        const brand = await sidebar.locator('.sidebar__brand').boundingBox();
        const tb = await toggle.boundingBox();
        expect(tb.x, 'toggle to the right of the brand').toBeGreaterThanOrEqual(brand.x + brand.width - 1);
        const wide = (await sidebar.boundingBox()).width;
        await toggle.click();
        await expect(toggle).toHaveAttribute('aria-expanded', 'false');
        await expect.poll(async () => (await sidebar.boundingBox()).width).toBeLessThan(wide / 2);
        const narrow = await sidebar.boundingBox();
        const card = await page.getByRole('region', { name: 'Overview' }).boundingBox();
        expect(card.x, 'content starts right of the collapsed rail').toBeGreaterThanOrEqual(narrow.x + narrow.width);
        await toggle.click();
        await expect(toggle).toHaveAttribute('aria-expanded', 'true');
        await expect(page.getByRole('region', { name: 'Overview' }).getByTestId('profile-avatar')).toBeVisible();
        await expect(page.getByTestId('app-title')).toHaveCount(0);
      }
      // Every primary control remains reachable.
      for (const id of ['kpi-threshold', 'kpi-work-orders', 'profile-avatar', 'horizon-filters']) {
        await expect(page.getByTestId(id)).toBeVisible();
      }
      await expect(page.getByRole('searchbox', { name: 'Search aircraft' })).toBeVisible();
      // The drawer toggle exists only where the sidebar is hidden.
      expect(await page.getByRole('button', { name: 'Open navigation menu' }).isVisible()).toBe(vp.width < 900);
    });
  }
});

test.describe('forecast chart against the live API', () => {
  test('the chart shows exactly what the forecast API says and never requests recorded telemetry', async ({ page, request, diagnostics }) => {
    const dash = await api(request, '/dashboards/dispatcher');
    const first = [...dash.aircraftRisk].sort((a, b) => a.registrationNumber.localeCompare(b.registrationNumber))[0];
    const seriesCalls = [];
    page.on('request', (r) => r.url().includes('/telemetry/series') && seriesCalls.push(r.url()));
    await signIn(page, ACCOUNTS.dispatcher);
    for (const [label, horizon] of [['7 Days', 'SevenDays'], ['1 Day', 'OneDay'], ['1 Month', 'ThirtyDays']]) {
      const expected = await api(request, `/aircraft/${first.aircraftId}/sensor-forecast?horizon=${horizon}`);
      await page.goto(`/dashboard/dispatcher?aircraft=${first.registrationNumber}&horizon=${horizon}`);
      const button = page.getByTestId('horizon-filters').getByRole('button', { name: new RegExp(label) });
      await expect(button).toHaveAttribute('aria-pressed', 'true');
      if (expected.status === 'NotAvailable') {
        await expect(page.getByTestId('forecast-unavailable')).toContainText(expected.reason);
        await expect(page.getByTestId('chart-forecast')).toHaveCount(0);
        await expect(page.getByTestId('breach-marker')).toHaveCount(0);
      } else {
        await expect(page.getByTestId('chart-forecast')).toBeVisible();
        await expect(button).toBeEnabled();
        // Clean chart: white background, no interval bands, vibration hidden until ticked, and nodes ONLY where a
        // forecast value is beyond its limit.
        const chart = page.getByTestId('chart-forecast');
        await expect(chart.getByTestId('chart-background')).toHaveAttribute('fill', '#ffffff');
        await expect(chart.locator('[data-testid^="band-"]')).toHaveCount(0);
        await expect(page.getByTestId('toggle-forecast-vibrationIndex')).not.toBeChecked();
        await expect(chart.getByTestId('panel-vibrationIndex')).toHaveCount(0);
        // At rest: continuous lines only - no circles, and the keyboard diamonds are transparent.
        expect(await chart.locator('svg circle').count(), 'no resting dots').toBe(0);
        for (const m of await chart.getByTestId('breach-marker').all()) await expect(m).toHaveCSS('opacity', '0');
        // Hovering a breaching hour reveals a bold node in that series' colour; leaving removes it.
        for (const [key, color] of [['temperatureC', '#dc2626'], ['pressurePsi', '#15803d']]) {
          const first = chart.locator(`[data-testid="breach-marker"][data-metric="${key}"]`).first();
          if (!(await first.count())) continue;
          const b = await first.boundingBox();
          await page.mouse.move(b.x + b.width / 2, b.y + b.height / 2);
          await expect(chart.locator(`[data-testid="breach-node"][data-metric="${key}"]`)).toHaveAttribute('fill', color);
          await page.mouse.move(0, 0);
          await expect(chart.getByTestId('breach-node')).toHaveCount(0);
        }
        await page.getByTestId('toggle-forecast-vibrationIndex').check();
        await expect(chart.getByTestId('series-vibrationIndex')).toHaveAttribute('data-color', '#2563eb');
        await page.getByTestId('toggle-forecast-vibrationIndex').uncheck();
        // Lines and breach diamonds share their metric's colour - no uniform red.
        for (const [key, color] of [['temperatureC', '#dc2626'], ['pressurePsi', '#15803d'], ['vibrationIndex', '#2563eb']]) {
          const panel = page.getByTestId(`panel-${key}`);
          if (!(await panel.count())) continue;
          await expect(panel.getByTestId(`series-${key}`)).toHaveAttribute('data-color', color);
          for (const marker of await panel.getByTestId('breach-marker').all()) await expect(marker).toHaveAttribute('fill', color);
        }
        // The limit key sits above the plot's lines: no label crosses a series path.
        const labels = page.getByTestId('chart-forecast').locator('[data-testid^="limit-label-"]');
        const boxes = await labels.evaluateAll((els) => els.map((e) => e.getBoundingClientRect().toJSON()));
        for (let i = 1; i < boxes.length; i += 1) expect(boxes[i].top, 'limit labels do not overlap').toBeGreaterThanOrEqual(boxes[i - 1].bottom - 1);
      }
    }
    expect(seriesCalls, 'recorded telemetry is never fetched for the forecast view').toEqual([]);
    expect(diagnostics.consoleErrors, diagnostics.consoleErrors.join('\n')).toEqual([]);
  });
});

test.describe('profile menu and sign-out in a real browser', () => {
  test('shows the real identity, signs out, and Back cannot show a protected page', async ({ page, request, diagnostics }) => {
    const me = await api(request, '/auth/me');
    await signInThroughForm(page, ACCOUNTS.dispatcher, PASSWORD);
    await expect(page).toHaveURL(/\/dashboard\/dispatcher/);
    await expect(page.getByTestId('kpi-threshold')).toBeVisible();
    // On the dashboard the avatar sits at the top-right of the Health Dashboard card.
    await page.getByRole('region', { name: 'Overview' }).getByTestId('profile-avatar').click();
    await expect(page.getByTestId('profile-popover')).toBeVisible();
    await expect(page.getByTestId('profile-email')).toHaveText(me.email || me.username);
    await expect(page.getByTestId('profile-role')).toContainText('Flight Dispatcher');
    await expect(page.getByTestId('sign-out')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.getByTestId('profile-popover')).toHaveCount(0);
    await page.getByRole('link', { name: /Fleet Health/ }).first().click();
    await expect(page).toHaveURL(/\/fleet$/);

    await expect(page.getByRole('button', { name: /^Sign out$/ })).toHaveCount(0);   // no standalone button
    await page.getByTestId('profile-avatar').click();
    await expect(page.getByTestId('profile-email')).toHaveText(me.email || me.username);
    await expect(page.getByTestId('profile-role')).toContainText('Flight Dispatcher');
    await page.keyboard.press('Escape');
    await expect(page.getByTestId('profile-popover')).toHaveCount(0);

    await page.getByTestId('profile-avatar').click();
    await page.getByTestId('sign-out').click();
    await expect(page).toHaveURL(/\/login/);
    expect(await page.evaluate(() => sessionStorage.getItem('afp.accessToken'))).toBeNull();

    await page.goBack();
    await expect(page).toHaveURL(/\/login/);
    await expect(page.getByTestId('kpi-threshold')).toHaveCount(0);
    await expect(page.getByTestId('profile-avatar')).toHaveCount(0);
    // Further Back leaves the app for the page the browser started on - still nothing protected.
    await page.goBack();
    expect(page.url()).not.toMatch(/dashboard|fleet/);
    await expect(page.getByTestId('profile-avatar')).toHaveCount(0);
    expect(diagnostics.consoleErrors, diagnostics.consoleErrors.join('\n')).toEqual([]);
  });
});
