/**
 * Generic role helpers (RBAC v2). Users carry `roles: string[]`. Role NAMES are never listed here:
 * the access rules live in router/access.js and the role catalogue comes from GET /admin/roles.
 */

export function userRoles(user) {
  return Array.isArray(user?.roles) ? user.roles : [];
}

/** "MaintenanceTechnician" -> "Maintenance Technician" */
export function roleLabel(roleName) {
  if (!roleName) return 'No role';
  return String(roleName)
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/([A-Z])([A-Z][a-z])/g, '$1 $2')
    .replace(/[_-]+/g, ' ')
    .trim();
}

export function rolesLabel(user) {
  const r = userRoles(user);
  return r.length ? r.map(roleLabel).join(' · ') : 'No role';
}

/** True when the user must see the Pending Account Approval page (pending, or active without any role). */
export function isPendingUser(user) {
  if (!user) return false;
  if (user.status === 'Pending') return true;
  return userRoles(user).length === 0;
}

export function isActiveUser(user) {
  return !!user && user.status === 'Active' && !isPendingUser(user);
}

/**
 * Whether a user satisfies a requirement list of role names. An empty/undefined list means
 * "any active user with a role". UI convenience only - the server enforces authorisation.
 */
export function hasAccess(user, allow) {
  if (!isActiveUser(user)) return false;
  if (!allow || allow.length === 0) return true;
  const roles = userRoles(user);
  return allow.some((r) => roles.includes(r));
}

export function hasRole(user, ...roles) {
  const mine = userRoles(user);
  return roles.some((r) => mine.includes(r));
}
