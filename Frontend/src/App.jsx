import { useEffect } from 'react';
import { AuthProvider, useAuth } from './auth/AuthContext.jsx';
import { defaultPathFor } from './router/access.js';
import { Router, Routes, Redirect, useLocation } from './router/Router.jsx';
import { RequireAuth, RequireRole } from './router/guards.jsx';
import { routes } from './router/routes.jsx';
import { ToastProvider } from './components/Toast.jsx';
import { AppLayout } from './components/AppLayout.jsx';
import { DemoBanner } from './components/Misc.jsx';

/** Signed-in users visiting the login/register pages go to their dashboard. */
function PublicOnly({ children }) {
  const { isAuthenticated, user } = useAuth();
  if (isAuthenticated) return <Redirect to={defaultPathFor(user)} />;
  return children;
}

function FocusOnNavigate() {
  const { pathname } = useLocation();
  useEffect(() => {
    const main = document.getElementById('main-content');
    if (main && document.activeElement && document.activeElement !== document.body) {
      // Move focus to the new page content for screen-reader and keyboard users.
      main.focus({ preventScroll: true });
    }
  }, [pathname]);
  return null;
}

export function wrapRoute(route, content) {
  if (route.layout === 'public') {
    const isReset = route.path === '/reset-password';
    return isReset ? content : <PublicOnly>{content}</PublicOnly>;
  }
  if (route.layout === 'status') return content;
  return (
    <RequireAuth>
      <DemoBanner />
      <AppLayout>
        <RequireRole allow={route.allow}>{content}</RequireRole>
      </AppLayout>
    </RequireAuth>
  );
}

export function AppRoutes() {
  return (
    <>
      <FocusOnNavigate />
      <Routes routes={routes} wrap={wrapRoute} />
    </>
  );
}

export default function App() {
  return (
    <Router>
      <AuthProvider>
        <ToastProvider>
          <AppRoutes />
        </ToastProvider>
      </AuthProvider>
    </Router>
  );
}
