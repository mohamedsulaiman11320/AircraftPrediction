/** Session persistence in sessionStorage (cleared when the browser tab closes). */
const TOKEN_KEY = 'afp.accessToken';
const EXPIRES_KEY = 'afp.expiresAtUtc';
const REFRESH_KEY = 'afp.refreshToken';
const REFRESH_EXPIRES_KEY = 'afp.refreshTokenExpiresAtUtc';
const USER_KEY = 'afp.user';

function safe(fn, fallback = null) {
  try {
    return fn();
  } catch {
    return fallback;
  }
}
const read = (k) => safe(() => window.sessionStorage.getItem(k));
const expired = (iso) => !!iso && Date.parse(iso) <= Date.now();

/** Current access token, or null when missing/expired (the refresh token may still be valid). */
export function getToken() {
  const token = read(TOKEN_KEY);
  if (!token || expired(read(EXPIRES_KEY))) return null;
  return token;
}

export function getRefreshToken() {
  const token = read(REFRESH_KEY);
  if (!token || expired(read(REFRESH_EXPIRES_KEY))) return null;
  return token;
}

/** True when there is an access token or a refresh token that can obtain one. */
export function hasSession() {
  return !!getToken() || !!getRefreshToken();
}

export function getStoredUser() {
  return safe(() => JSON.parse(window.sessionStorage.getItem(USER_KEY) || 'null'));
}

/** Store a TokenResponse { accessToken, expiresAtUtc, refreshToken, refreshTokenExpiresAtUtc, user }. */
export function setSession({ accessToken, expiresAtUtc, refreshToken, refreshTokenExpiresAtUtc, user }) {
  safe(() => {
    const s = window.sessionStorage;
    if (accessToken) s.setItem(TOKEN_KEY, accessToken);
    if (expiresAtUtc) s.setItem(EXPIRES_KEY, expiresAtUtc);
    else if (accessToken) s.removeItem(EXPIRES_KEY);
    if (refreshToken) s.setItem(REFRESH_KEY, refreshToken);
    if (refreshTokenExpiresAtUtc) s.setItem(REFRESH_EXPIRES_KEY, refreshTokenExpiresAtUtc);
    if (user) s.setItem(USER_KEY, JSON.stringify(user));
  });
  updateListeners.forEach((l) => l({ user: user || getStoredUser() }));
}

export function setStoredUser(user) {
  safe(() => window.sessionStorage.setItem(USER_KEY, JSON.stringify(user)));
}

export function clearSession() {
  safe(() => {
    [TOKEN_KEY, EXPIRES_KEY, REFRESH_KEY, REFRESH_EXPIRES_KEY, USER_KEY].forEach((k) => window.sessionStorage.removeItem(k));
  });
  clearListeners.forEach((l) => l());
}

const clearListeners = new Set();
const updateListeners = new Set();

/** Subscribe to session clears (e.g. the api client clearing after a failed refresh). */
export function onSessionCleared(listener) {
  clearListeners.add(listener);
  return () => clearListeners.delete(listener);
}

/** Subscribe to session updates (login or silent token refresh). */
export function onSessionUpdated(listener) {
  updateListeners.add(listener);
  return () => updateListeners.delete(listener);
}
