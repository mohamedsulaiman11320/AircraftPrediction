import { useAuth } from '../auth/AuthContext.jsx';
import { hasAccess, isPendingUser } from '../auth/roles.js';
import { Redirect, useLocation } from './Router.jsx';
import { toQueryString } from './matchPath.js';
import { Skeleton } from '../components/Skeleton.jsx';

/** Requires a signed-in user. Pending / role-less users are sent to /pending. */
export function RequireAuth({ children, allowPending = false }) {
  const { isAuthenticated, user, status } = useAuth();
  const { pathname, search } = useLocation();
  if (status === 'checking') {
    return (
      <div className="workspace" aria-busy="true" aria-label="Loading session">
        <Skeleton height={28} width="40%" />
      </div>
    );
  }
  if (!isAuthenticated) {
    return <Redirect to={'/login' + toQueryString({ returnTo: pathname + search })} />;
  }
  if (!allowPending && isPendingUser(user)) {
    return <Redirect to="/pending" />;
  }
  return children;
}

/** Requires one of the roles in `allow` (role names). Otherwise redirects to /access-denied. */
export function RequireRole({ allow, children }) {
  const { user } = useAuth();
  const { pathname } = useLocation();
  if (!hasAccess(user, allow)) {
    return <Redirect to={'/access-denied' + toQueryString({ from: pathname })} />;
  }
  return children;
}
