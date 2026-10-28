import { useState } from 'react';
import { AuthLayout } from './AuthLayout.jsx';
import { useAuth } from '../../auth/AuthContext.jsx';
import { isPendingUser } from '../../auth/roles.js';
import { defaultPathFor } from '../../router/access.js';
import { Link } from '../../router/Link.jsx';
import { useNavigate, useQuery } from '../../router/Router.jsx';
import { Icon } from '../../components/Icon.jsx';
import { Alert } from '../../components/Misc.jsx';

export function PendingApprovalPage() {
  const { user, isAuthenticated, refreshMe, logout } = useAuth();
  const navigate = useNavigate();
  const [checking, setChecking] = useState(false);
  const [stillPending, setStillPending] = useState(false);

  const recheck = async () => {
    setChecking(true);
    const me = await refreshMe();
    setChecking(false);
    if (me && !isPendingUser(me)) navigate(defaultPathFor(me), { replace: true });
    else setStillPending(true);
  };

  const signOut = async () => {
    await logout();
    navigate('/login', { replace: true });
  };

  return (
    <AuthLayout title="Account pending approval">
      <div className="auth-icon auth-icon--warning">
        <Icon name="clock" size={26} />
      </div>
      <h1>Account pending approval</h1>
      <p className="auth-card__lead">
        {isAuthenticated && user ? `Thanks, ${user.displayName}. ` : ''}
        Your account has been created but an administrator still needs to approve it and assign your role. You will be able to
        sign in and use the platform once that is done.
      </p>
      {stillPending ? (
        <Alert tone="info" title="Still pending">
          No role has been assigned yet. Please check again later.
        </Alert>
      ) : null}
      {isAuthenticated ? (
        <div className="stack" style={{ gap: 8 }}>
          <button type="button" className="btn btn--primary btn--block" onClick={recheck} disabled={checking}>
            {checking ? <span className="spinner" aria-hidden="true" /> : null}
            Check approval status
          </button>
          <button type="button" className="btn btn--secondary btn--block" onClick={signOut}>
            Sign out
          </button>
        </div>
      ) : (
        <Link to="/login" className="btn btn--primary btn--block">
          Back to sign in
        </Link>
      )}
    </AuthLayout>
  );
}

export function AccessDeniedPage() {
  const { user, isAuthenticated } = useAuth();
  const [query] = useQuery();
  return (
    <AuthLayout title="Access denied">
      <div className="auth-icon auth-icon--danger">
        <Icon name="lock" size={26} />
      </div>
      <h1>Access denied</h1>
      <p className="auth-card__lead">
        Your role does not permit access to {query.from ? <code>{query.from}</code> : 'this page'}. If you believe you need access,
        contact an administrator.
      </p>
      {isAuthenticated ? (
        <Link to={defaultPathFor(user)} className="btn btn--primary btn--block">
          Go to my dashboard
        </Link>
      ) : (
        <Link to="/login" className="btn btn--primary btn--block">
          Sign in
        </Link>
      )}
    </AuthLayout>
  );
}
