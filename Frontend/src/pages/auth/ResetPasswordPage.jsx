import { useState } from 'react';
import { AuthLayout } from './AuthLayout.jsx';
import { authApi } from '../../api/auth.js';
import { PASSWORD_RULES, passwordChecks, validateConfirm, validatePassword } from '../../auth/password.js';
import { Link } from '../../router/Link.jsx';
import { useNavigate, useQuery } from '../../router/Router.jsx';
import { PasswordField } from '../../components/FormFields.jsx';
import { Alert } from '../../components/Misc.jsx';

export function ResetPasswordPage() {
  const [query] = useQuery();
  const navigate = useNavigate();
  const token = query.token || '';
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [errors, setErrors] = useState({});
  const [failure, setFailure] = useState(null);
  const [busy, setBusy] = useState(false);
  const checks = passwordChecks(password);

  if (!token) {
    return (
      <AuthLayout title="Reset link invalid">
        <h1>Reset link invalid</h1>
        <Alert tone="danger" title="Missing reset token">
          <p>This page must be opened from the link in your password reset email.</p>
        </Alert>
        <Link to="/forgot-password" className="btn btn--primary btn--block">
          Request a new link
        </Link>
      </AuthLayout>
    );
  }

  const submit = async (e) => {
    e.preventDefault();
    const v = { password: validatePassword(password), confirm: validateConfirm(password, confirm) };
    setErrors(v);
    setFailure(null);
    if (v.password || v.confirm) return;
    setBusy(true);
    try {
      await authApi.resetPassword({ token, newPassword: password, confirmPassword: confirm });
      navigate('/login?reset=1', { replace: true });
    } catch (err) {
      setBusy(false);
      if (err?.code === 'invalid_or_expired_token') {
        setFailure({ title: 'Link expired or already used', text: 'Reset links are single-use and expire after 30 minutes. Request a new one.' });
      } else if (err?.status === 429) {
        setFailure({ title: 'Too many requests', text: 'Please wait a moment and try again.' });
      } else if (err?.errors) {
        setErrors({ password: err.fieldError('newPassword') || err.fieldError('password'), confirm: err.fieldError('confirmPassword') });
        setFailure({ title: err.title, text: err.detail });
      } else {
        setFailure({ title: 'Password not reset', text: err?.detail || err?.message || 'Please try again.' });
      }
    }
  };

  return (
    <AuthLayout title="Choose a new password">
      <h1>Choose a new password</h1>
      <p className="auth-card__lead">Resetting your password signs out all existing sessions.</p>
      {failure ? (
        <Alert tone="danger" title={failure.title}>
          {failure.text ? <p>{failure.text}</p> : null}
          {failure.title?.startsWith('Link') ? <Link to="/forgot-password">Request a new reset link</Link> : null}
        </Alert>
      ) : null}
      <form onSubmit={submit} noValidate aria-busy={busy}>
        <PasswordField label="New password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} error={errors.password} maxLength={128} required />
        <ul className="password-rules" aria-label="Password requirements">
          {PASSWORD_RULES.map((r) => (
            <li key={r.id} data-ok={checks[r.id] ? 'true' : 'false'}>
              {r.label}
              <span className="visually-hidden">{checks[r.id] ? ' (met)' : ' (not met)'}</span>
            </li>
          ))}
        </ul>
        <PasswordField label="Confirm new password" autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(e.target.value)} error={errors.confirm} maxLength={128} required />
        <button type="submit" className="btn btn--primary btn--block" disabled={busy}>
          {busy ? <span className="spinner" aria-hidden="true" /> : null}
          Update password
        </button>
      </form>
      <div className="auth-card__footer">
        <Link to="/login">Back to sign in</Link>
      </div>
    </AuthLayout>
  );
}
