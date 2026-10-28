import { expect, test as base } from '@playwright/test';

/**
 * Shared sign-in and diagnostics for the browser tests.
 *
 * Credentials come from the environment so nothing is committed. The defaults are the local
 * development accounts; set E2E_PASSWORD (and the *_USER names) to run against another environment.
 */
export const ACCOUNTS = {
  dataEngineer: process.env.E2E_DE_USER || 'data',
  dispatcher: process.env.E2E_DISPATCHER_USER || 'dispatcher',
  technician: process.env.E2E_TECHNICIAN_USER || 'technician',
};
export const PASSWORD = process.env.E2E_PASSWORD || 'Password@123';
export const API_BASE = process.env.E2E_API_BASE || 'http://localhost:5080';

/** Every test collects console errors and failed requests so they can be asserted, not ignored. */
export const test = base.extend({
  diagnostics: async ({ page }, use) => {
    const consoleErrors = [];
    const failedRequests = [];
    page.on('console', (msg) => {
      if (msg.type() === 'error') consoleErrors.push(msg.text());
    });
    page.on('requestfailed', (req) => {
      failedRequests.push(`${req.method()} ${req.url()} - ${req.failure()?.errorText}`);
    });
    page.on('response', (res) => {
      // 401 before sign-in is normal; anything 5xx is not.
      if (res.status() >= 500) failedRequests.push(`${res.status()} ${res.request().method()} ${res.url()}`);
    });
    await use({ consoleErrors, failedRequests });
  },
});

export { expect };

/**
 * Signs in once per role and reuses the token.
 *
 * The auth endpoints are rate-limited, so logging in from every test trips the limiter and the
 * later tests get stuck on /login. We authenticate each account once through the real API, then
 * seed the same sessionStorage keys the app itself writes (`setSession` in src/auth/session.js).
 * The login FORM is still exercised directly by `signInThroughForm`.
 */
const sessionCache = new Map();

async function cachedSession(request, username, password) {
  if (!sessionCache.has(username)) {
    const response = await request.post(`${API_BASE}/api/v1/auth/login`, { data: { username, password } });
    if (!response.ok()) throw new Error(`login failed for ${username}: ${response.status()}`);
    sessionCache.set(username, await response.json());
  }
  return sessionCache.get(username);
}

export async function signIn(page, username, password = PASSWORD) {
  const s = await cachedSession(page.request, username, password);
  await page.addInitScript((session) => {
    window.sessionStorage.setItem('afp.accessToken', session.accessToken);
    window.sessionStorage.setItem('afp.expiresAtUtc', session.expiresAtUtc);
    if (session.refreshToken) window.sessionStorage.setItem('afp.refreshToken', session.refreshToken);
    if (session.refreshTokenExpiresAtUtc) {
      window.sessionStorage.setItem('afp.refreshTokenExpiresAtUtc', session.refreshTokenExpiresAtUtc);
    }
    if (session.user) window.sessionStorage.setItem('afp.user', JSON.stringify(session.user));
  }, s);
  await page.goto('/');
  await expect(page.getByRole('navigation', { name: /primary/i })).toBeVisible({ timeout: 20_000 });
}

/** Drives the real login form. Used by the test that verifies sign-in itself. */
export async function signInThroughForm(page, username, password = PASSWORD) {
  await page.goto('/login');
  // autocomplete attributes are stable; the visible labels carry a required marker and the
  // password field shares its label text with the show/hide toggle button.
  await page.locator('input[autocomplete="username"]').fill(username);
  await page.locator('input[autocomplete="current-password"]').fill(password);
  await page.getByRole('button', { name: /sign in/i }).click();
  await expect(page).not.toHaveURL(/\/login/, { timeout: 20_000 });
  await expect(page.getByRole('navigation', { name: /primary/i })).toBeVisible();
}

/** The visible top-level navigation labels, in order. */
export async function navLabels(page) {
  const nav = page.getByRole('navigation', { name: /primary/i });
  return (await nav.getByRole('link').allTextContents()).map((t) => t.trim()).filter(Boolean);
}

/** An access token for direct API assertions (authorization is enforced server-side). */
export async function apiToken(request, username, password = PASSWORD) {
  return (await cachedSession(request, username, password)).accessToken;
}
