import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import { authApi } from '../api/auth.js';
import {
  clearSession,
  getRefreshToken,
  getStoredUser,
  getToken,
  hasSession,
  onSessionCleared,
  onSessionUpdated,
  setSession,
  setStoredUser,
} from './session.js';
import { hasAccess, isPendingUser } from './roles.js';

const AuthContext = createContext(null);

export function AuthProvider({ children }) {
  const [signedIn, setSignedIn] = useState(() => hasSession());
  const [user, setUser] = useState(() => (hasSession() ? getStoredUser() : null));
  // 'checking' only when we have a session but no cached user to show yet.
  const [status, setStatus] = useState(() => (hasSession() && !getStoredUser() ? 'checking' : 'ready'));

  // Keep React state in sync with the api client (silent refresh / clear after a failed refresh).
  useEffect(() => {
    const offClear = onSessionCleared(() => {
      setSignedIn(false);
      setUser(null);
    });
    const offUpdate = onSessionUpdated(({ user: u }) => {
      setSignedIn(true);
      if (u) setUser(u);
    });
    return () => {
      offClear();
      offUpdate();
    };
  }, []);

  const refreshMe = useCallback(async () => {
    if (!hasSession()) return null;
    try {
      const me = await authApi.me();
      setUser(me);
      setStoredUser(me);
      return me;
    } catch (err) {
      if (err?.status === 401) clearSession();
      return null;
    } finally {
      setStatus('ready');
    }
  }, []);

  // Validate any stored session on first load.
  useEffect(() => {
    if (hasSession()) refreshMe();
  }, [refreshMe]);

  const login = useCallback(async (username, password) => {
    const result = await authApi.login(username, password);
    setSession(result);
    setSignedIn(true);
    setUser(result.user);
    setStatus('ready');
    return result.user;
  }, []);

  const logout = useCallback(async () => {
    try {
      if (getToken() || getRefreshToken()) await authApi.logout(getRefreshToken());
    } catch {
      /* tokens are discarded locally regardless */
    }
    clearSession();
    setSignedIn(false);
    setUser(null);
  }, []);

  const value = useMemo(
    () => ({
      user,
      status,
      isAuthenticated: signedIn && !!user,
      isPending: isPendingUser(user),
      roles: user?.roles || [],
      can: (allow) => hasAccess(user, allow),
      login,
      logout,
      refreshMe,
    }),
    [user, signedIn, status, login, logout, refreshMe],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used inside <AuthProvider>');
  return ctx;
}
