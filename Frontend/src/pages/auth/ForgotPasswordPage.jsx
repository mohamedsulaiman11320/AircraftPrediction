import { useState } from 'react';
import { AuthLayout } from './AuthLayout.jsx';
import { authApi } from '../../api/auth.js';
import { validateEmail } from '../../auth/password.js';
import { Link } from '../../router/Link.jsx';
import { TextField } from '../../components/FormFields.jsx';
import { Alert } from '../../components/Misc.jsx';
import { Icon } from '../../components/Icon.jsx';

/** Shown for every outcome so the page never reveals whether an account exists. */
export const NEUTRAL_RESET_MESSAGE =
  'If an account exists for that email address, a password reset link has been sent. The link expires in 30 minutes.';

export function ForgotPasswordPage() {
  const [email, setEmail] = useState('');
  const [error, setError] = useState(null);
  const [sent, setSent] = useState(false);
  const [blocked, setBlocked] = useState(null);
  const [busy, setBusy] = useState(false);

  const submit = async (e) => {
    e.preventDefault();
    const v = validateEmail(email);
    setError(v);
    setBlocked(null);
    if (v) return;
    setBusy(true);
    try {
      await authApi.forgotPassword(email.trim());
      setSent(true);
    } catch (err) {
      // Only transport-level problems are surfaced; everything else gets the neutral confirmation.
      if (err?.status === 429) setBlocked('Too many requests. Please wait a moment and try again.');
      else if (err?.status === 0) setBlocked('Unable to reach the server. Please try again.');
      else setSent(true);
    } finally {
      setBusy(false);
    }
  };

  if (sent) {
    return (
      <AuthLayout title="Check your email">
        <div className="auth-icon auth-icon--success">
          <Icon name="mail" size={26} />
        </div>
        <h1>Check your email</h1>
        <p className="auth-card__lead" role="status">
          {NEUTRAL_RESET_MESSAGE}
        </p>
        <Link to="/login" className="btn btn--primary btn--block">
          Back to sign in
        </Link>
      </AuthLayout>
    );
  }

  return (
    <AuthLayout title="Reset your password">
      <h1>Forgot your password?</h1>
      <p className="auth-card__lead">Enter your account email and we will send a reset link.</p>
      {blocked ? (
        <Alert tone="danger" title="Request not sent">
          <p>{blocked}</p>
        </Alert>
      ) : null}
      <form onSubmit={submit} noValidate aria-busy={busy}>
        <TextField label="Email" type="email" autoComplete="email" value={email} onChange={(e) => setEmail(e.target.value)} error={error} required />
        <button type="submit" className="btn btn--primary btn--block" disabled={busy}>
          {busy ? <span className="spinner" aria-hidden="true" /> : null}
          Send reset link
        </button>
      </form>
      <div className="auth-card__footer">
        Remembered it? <Link to="/login">Sign in</Link>
      </div>
    </AuthLayout>
  );
}
