import { defineConfig, devices } from '@playwright/test';

/**
 * Browser (end-to-end) tests. Separate from the Vitest unit suite:
 * `npm test` still runs Vitest; `npm run test:e2e` runs these against a RUNNING application.
 *
 * These drive the real frontend and the real API, so they need both to be up:
 *   Backend : cd Backend && dotnet run --project src/AFP.Api      (http://localhost:5080)
 *   Frontend: cd Frontend && npm run dev                          (http://localhost:5173)
 *
 * Uses the locally installed Google Chrome (`channel: 'chrome'`) rather than downloading a
 * Playwright-managed browser.
 */
const BASE_URL = process.env.E2E_BASE_URL || 'http://localhost:5173';

export default defineConfig({
  testDir: './e2e',
  // These tests sign in and read live data; running them in parallel fights over shared state.
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 60_000,
  expect: { timeout: 15_000 },
  reporter: [['list']],
  use: {
    baseURL: BASE_URL,
    channel: 'chrome',
    headless: true,
    screenshot: 'only-on-failure',
    trace: 'off',
    actionTimeout: 15_000,
  },
  projects: [{ name: 'chrome', use: { ...devices['Desktop Chrome'], channel: 'chrome' } }],
});
