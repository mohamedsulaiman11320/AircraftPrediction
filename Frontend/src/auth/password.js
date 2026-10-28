/** Client-side mirror of the server password policy (server remains authoritative). */
export const PASSWORD_MIN = 12;
export const PASSWORD_MAX = 128;

export const PASSWORD_RULES = [
  { id: 'length', label: `${PASSWORD_MIN}–${PASSWORD_MAX} characters`, test: (p) => p.length >= PASSWORD_MIN && p.length <= PASSWORD_MAX },
  { id: 'upper', label: 'An upper-case letter', test: (p) => /[A-Z]/.test(p) },
  { id: 'lower', label: 'A lower-case letter', test: (p) => /[a-z]/.test(p) },
  { id: 'digit', label: 'A digit', test: (p) => /[0-9]/.test(p) },
  { id: 'symbol', label: 'A symbol (e.g. ! # $ %)', test: (p) => /[^A-Za-z0-9\s]/.test(p) },
];

/** Returns { id: boolean } for each rule. */
export function passwordChecks(password = '') {
  return Object.fromEntries(PASSWORD_RULES.map((r) => [r.id, r.test(password)]));
}

/** Returns a single message describing the first failing rule, or null when valid. */
export function validatePassword(password = '') {
  if (!password) return 'Password is required.';
  if (password.length < PASSWORD_MIN) return `Password must be at least ${PASSWORD_MIN} characters.`;
  if (password.length > PASSWORD_MAX) return `Password must be at most ${PASSWORD_MAX} characters.`;
  if (!/[A-Z]/.test(password)) return 'Password must contain an upper-case letter.';
  if (!/[a-z]/.test(password)) return 'Password must contain a lower-case letter.';
  if (!/[0-9]/.test(password)) return 'Password must contain a digit.';
  if (!/[^A-Za-z0-9\s]/.test(password)) return 'Password must contain a symbol.';
  return null;
}

export function validateConfirm(password, confirm) {
  if (!confirm) return 'Please confirm the password.';
  if (password !== confirm) return 'Passwords do not match.';
  return null;
}

/** Username policy (RBAC v2): 3–64 characters from [a-z0-9._-], lower-case. */
export const USERNAME_RE = /^[a-z0-9._-]{3,64}$/;
export function validateUsername(username = '') {
  const v = username.trim();
  if (!v) return 'Username is required.';
  if (v.length < 3) return 'Username must be at least 3 characters.';
  if (v.length > 64) return 'Username must be at most 64 characters.';
  if (!USERNAME_RE.test(v)) return 'Use only lower-case letters, digits, dots, underscores and hyphens.';
  return null;
}

const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
export function validateEmail(email = '') {
  const v = email.trim();
  if (!v) return 'Email is required.';
  if (v.length > 256 || !EMAIL_RE.test(v)) return 'Enter a valid email address.';
  return null;
}
