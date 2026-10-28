import { useState } from 'react';
import { AuthLayout } from './AuthLayout.jsx';
import { authApi } from '../../api/auth.js';
import { PASSWORD_RULES, passwordChecks, validateConfirm, validateEmail, validatePassword, validateUsername } from '../../auth/password.js';
import { Link } from '../../router/Link.jsx';
import { FormError, PasswordField, TextField } from '../../components/FormFields.jsx';
import { Icon } from '../../components/Icon.jsx';

export function validateRegistration({ username, email, displayName, password, confirmPassword }) {
  const errors = {
    username: validateUsername(username),
    email: validateEmail(email),
    displayName: !displayName.trim()
      ? 'Display name is required.'
      : displayName.trim().length > 100
        ? 'Display name must be at most 100 characters.'
        : null,
    password: validatePassword(password),
    confirmPassword: validateConfirm(password, confirmPassword),
  };
  return Object.fromEntries(Object.entries(errors).filter(([, v]) => v));
}

export function RegisterPage() {
  const [form, setForm] = useState({ username: '', email: '', displayName: '', password: '', confirmPassword: '' });
  const [errors, setErrors] = useState({});
  const [serverError, setServerError] = useState(null);
  const [done, setDone] = useState(null);
  const [busy, setBusy] = useState(false);
  const checks = passwordChecks(form.password);

  const set = (k) => (e) => setForm((f) => ({ ...f, [k]: e.target.value }));

  const submit = async (e) => {
    e.preventDefault();
    const v = validateRegistration(form);
    setErrors(v);
    setServerError(null);
    if (Object.keys(v).length) return;
    setBusy(true);
    try {
      const res = await authApi.register({ ...form, username: form.username.trim(), email: form.email.trim(), displayName: form.displayName.trim() });
      setDone(res?.message || 'If the details are valid, your registration has been received.');
    } catch (err) {
      if (err?.errors) {
        const mapped = {};
        ['username', 'email', 'displayName', 'password', 'confirmPassword'].forEach((k) => {
          const m = err.fieldError?.(k);
          if (m) mapped[k] = m;
        });
        setErrors(mapped);
      }
      setServerError(
        err?.status === 429 ? { title: 'Too many requests', detail: 'Please wait a moment before trying again.' } : err,
      );
    } finally {
      setBusy(false);
    }
  };

  if (done) {
    return (
      <AuthLayout title="Registration received">
        <div className="auth-icon auth-icon--success">
          <Icon name="check" size={26} />
        </div>
        <h1>Registration received</h1>
        <p className="auth-card__lead">{done}</p>
        <p className="muted">
          New accounts are reviewed by an administrator, who assigns your role. You can sign in once your account is approved.
        </p>
        <Link to="/login" className="btn btn--primary btn--block">
          Back to sign in
        </Link>
      </AuthLayout>
    );
  }

  return (
    <AuthLayout title="Request an account">
      <h1>Request an account</h1>
      <p className="auth-card__lead">An administrator will approve your account and assign a role.</p>
      <FormError error={serverError} />
      <form onSubmit={submit} noValidate aria-busy={busy}>
        <TextField
          label="Username"
          autoComplete="username"
          autoCapitalize="none"
          spellCheck={false}
          value={form.username}
          onChange={(e) => setForm((f) => ({ ...f, username: e.target.value.toLowerCase() }))}
          error={errors.username}
          hint="3–64 characters: lower-case letters, digits, dots, underscores or hyphens."
          maxLength={64}
          required
        />
        <TextField label="Email" type="email" autoComplete="email" value={form.email} onChange={set('email')} error={errors.email} required />
        <TextField
          label="Display name"
          autoComplete="name"
          value={form.displayName}
          onChange={set('displayName')}
          error={errors.displayName}
          maxLength={100}
          required
        />
        <PasswordField
          label="Password"
          autoComplete="new-password"
          value={form.password}
          onChange={set('password')}
          error={errors.password}
          maxLength={128}
          required
        />
        <ul className="password-rules" aria-label="Password requirements">
          {PASSWORD_RULES.map((r) => (
            <li key={r.id} data-ok={checks[r.id] ? 'true' : 'false'}>
              {r.label}
              <span className="visually-hidden">{checks[r.id] ? ' (met)' : ' (not met)'}</span>
            </li>
          ))}
        </ul>
        <PasswordField
          label="Confirm password"
          autoComplete="new-password"
          value={form.confirmPassword}
          onChange={set('confirmPassword')}
          error={errors.confirmPassword}
          maxLength={128}
          required
        />
        <button type="submit" className="btn btn--primary btn--block" disabled={busy}>
          {busy ? <span className="spinner" aria-hidden="true" /> : null}
          {busy ? 'Submitting…' : 'Create account'}
        </button>
      </form>
      <div className="auth-card__footer">
        Already have an account? <Link to="/login">Sign in</Link>
      </div>
    </AuthLayout>
  );
}
