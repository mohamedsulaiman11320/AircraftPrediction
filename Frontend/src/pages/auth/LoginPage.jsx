import { useState } from 'react';
import { AuthLayout } from './AuthLayout.jsx';
import { useAuth } from '../../auth/AuthContext.jsx';
import { isPendingUser } from '../../auth/roles.js';
import { defaultPathFor } from '../../router/access.js';
import { Link } from '../../router/Link.jsx';
import { useNavigate, useQuery } from '../../router/Router.jsx';
import { PasswordField, TextField } from '../../components/FormFields.jsx';
import { Alert } from '../../components/Misc.jsx';
import { DEMO_MODE } from '../../api/config.js';

/** Map login failures to user-facing messages (never reveals whether the account exists). */
export function loginErrorMessage(err) {
  if (!err) return null;
  if (err.status === 0) return { title: 'Unable to reach the server', text: err.detail || 'Check your connection and try again.' };
  if (err.status === 401) return { title: 'Sign-in failed', text: 'The username or password is incorrect.' };
  if (err.status === 423 || err.code === 'account_locked')
    return {
      title: 'Account temporarily locked',
      text: 'Too many failed sign-in attempts. Try again in about 15 minutes or reset your password.',
    };
  if (err.status === 429) return { title: 'Too many attempts', text: 'Please wait a moment before trying again.' };
  if (err.code === 'account_disabled')
    return { title: 'Account disabled', text: 'This account has been disabled. Contact your administrator.' };
  if (err.code === 'account_pending')
    return { title: 'Account pending approval', text: 'An administrator must approve your account before you can sign in.' };
  return { title: 'Sign-in failed', text: err.detail || err.title || 'Please try again.' };
}

function safeReturnTo(value) {
  if (!value || !value.startsWith('/') || value.startsWith('//')) return null;
  if (/^\/(login|register|forgot-password|reset-password)/.test(value)) return null;
  return value;
}

export function LoginPage() {
  const { login } = useAuth();
  const navigate = useNavigate();
  const [query] = useQuery();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [errors, setErrors] = useState({});
  const [failure, setFailure] = useState(null);
  const [busy, setBusy] = useState(false);

  const submit = async (e) => {
    e.preventDefault();
    const next = {
      username: username.trim() ? null : 'Username is required.',
      password: password ? null : 'Password is required.',
    };
    setErrors(next);
    setFailure(null);
    if (next.username || next.password) return;
    setBusy(true);
    try {
      const user = await login(username.trim(), password);
      if (isPendingUser(user)) {
        navigate('/pending', { replace: true });
        return;
      }
      navigate(safeReturnTo(query.returnTo) || defaultPathFor(user), { replace: true });
    } catch (err) {
      setBusy(false);
      if (err?.code === 'account_pending') {
        navigate('/pending?from=login', { replace: true });
        return;
      }
      setFailure(loginErrorMessage(err));
    }
  };

  return (
    <AuthLayout title="Sign in">
      <h1>Sign in</h1>
      <p className="auth-card__lead">Use your organisation account to access fleet health and maintenance tools.</p>
      {query.reason === 'expired' ? (
        <Alert tone="info" title="Session ended">
          Your session has expired or was revoked. Please sign in again.
        </Alert>
      ) : null}
      {query.reset === '1' ? (
        <Alert tone="success" title="Password updated">
          Sign in with your new password. Other sessions were signed out.
        </Alert>
      ) : null}
      {failure ? (
        <Alert tone="danger" title={failure.title}>
          <p>{failure.text}</p>
        </Alert>
      ) : null}
      <form onSubmit={submit} noValidate aria-busy={busy}>
        <TextField
          label="Username or email"
          autoComplete="username"
          autoCapitalize="none"
          spellCheck={false}
          value={username}
          onChange={(e) => setUsername(e.target.value)}
          error={errors.username}
          required
        />
        <PasswordField
          label="Password"
          autoComplete="current-password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          error={errors.password}
          required
        />
        <button type="submit" className="btn btn--primary btn--block" disabled={busy}>
          {busy ? <span className="spinner" aria-hidden="true" /> : null}
          {busy ? 'Signing in…' : 'Sign in'}
        </button>
      </form>
      {DEMO_MODE ? (
        <p className="subtle" style={{ marginTop: 12 }}>
          Demo accounts (any password): username dispatcher or technician
        </p>
      ) : null}
      <div className="auth-card__links">
        <Link to="/forgot-password">Forgot password?</Link>
        <Link to="/register">Request an account</Link>
      </div>
    </AuthLayout>
  );
}
