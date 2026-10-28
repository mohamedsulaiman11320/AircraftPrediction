import { useEffect, useId, useRef, useState } from 'react';
import { useAuth } from '../auth/AuthContext.jsx';
import { rolesLabel } from '../auth/roles.js';
import { useNavigate } from '../router/Router.jsx';
import { initials } from '../utils/format.js';
import { Icon } from './Icon.jsx';

/**
 * Avatar (the signed-in user's initials) that opens a small profile popover with their email, role and Sign out.
 * Closes on Escape, on an outside click and after signing out; focus moves into the popover and back to the avatar.
 */
export function ProfileMenu({ user, onSignOut }) {
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const rootRef = useRef(null);
  const buttonRef = useRef(null);
  const signOutRef = useRef(null);
  const panelId = useId();

  useEffect(() => {
    if (!open) return undefined;
    signOutRef.current?.focus();
    const onKey = (e) => {
      if (e.key === 'Escape') {
        setOpen(false);
        buttonRef.current?.focus();
      }
    };
    const onPointer = (e) => {
      if (rootRef.current && !rootRef.current.contains(e.target)) setOpen(false);
    };
    document.addEventListener('keydown', onKey);
    document.addEventListener('mousedown', onPointer);
    return () => {
      document.removeEventListener('keydown', onKey);
      document.removeEventListener('mousedown', onPointer);
    };
  }, [open]);

  const name = user.displayName || user.email || user.username || '';
  const role = rolesLabel(user);

  return (
    <div ref={rootRef} className="relative">
      <button
        ref={buttonRef}
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-haspopup="dialog"
        aria-expanded={open}
        aria-controls={panelId}
        aria-label={`Account: ${name}`}
        data-testid="profile-avatar"
        className="flex h-9 w-9 items-center justify-center rounded-full bg-brand text-[13px] font-semibold text-white ring-2 ring-white/80 hover:bg-brand-deep focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand"
      >
        {initials(name)}
      </button>
      {open ? (
        <div
          id={panelId}
          role="dialog"
          aria-label="Account"
          data-testid="profile-popover"
          className="absolute right-0 top-11 z-50 w-[min(18rem,calc(100vw-2rem))] rounded-xl border border-line bg-white p-3 text-ink shadow-[0_12px_32px_rgba(25,22,34,0.18)]"
        >
          <div className="flex items-center gap-3 border-b border-line pb-3">
            <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-brand-soft text-[14px] font-semibold text-brand" aria-hidden="true">
              {initials(name)}
            </span>
            <div className="min-w-0">
              {user.displayName ? <p className="m-0 truncate text-[14px] font-semibold">{user.displayName}</p> : null}
              <p className="m-0 truncate text-[13px] text-ink-muted" data-testid="profile-email">
                {user.email || user.username}
              </p>
            </div>
          </div>
          <dl className="m-0 py-2 text-[13px]">
            <dt className="text-[11px] font-medium uppercase tracking-wide text-ink-muted">Role</dt>
            <dd className="m-0" data-testid="profile-role">
              {role}
            </dd>
          </dl>
          <button
            ref={signOutRef}
            type="button"
            disabled={busy}
            data-testid="sign-out"
            onClick={async () => {
              setBusy(true);
              try {
                await onSignOut();
              } finally {
                setBusy(false);
                setOpen(false);
              }
            }}
            className="flex w-full items-center justify-center gap-2 rounded-lg border border-crit/30 bg-crit-soft px-3 py-2 text-[13px] font-semibold text-crit hover:bg-crit hover:text-white focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-crit disabled:opacity-60"
          >
            <Icon name="logout" size={15} />
            {busy ? 'Signing out...' : 'Sign out'}
          </button>
        </div>
      ) : null}
    </div>
  );
}

/** The existing logout (server revoke + local clear), then the login screen REPLACES the current entry. */
export function useSignOut() {
  const { logout } = useAuth();
  const navigate = useNavigate();
  return async () => {
    await logout();
    navigate('/login', { replace: true });
  };
}

/**
 * Top bar. `pageOwnsHeader`: the page shows its own header with the profile menu (the Health Dashboard), so on a
 * desktop the bar is hidden and on a phone it keeps only the navigation-drawer button.
 */
/**
 * The narrow-screen bar with the navigation drawer toggle. Hidden on desktop; the profile avatar is in each page's title
 * row (PageHeader / the Health Dashboard card), never here.
 */
export function TopNav({ onToggleMenu, menuOpen, sidebarId }) {
  return (
    <header className="topnav min-[900px]:hidden">
      <button
        type="button"
        className="btn btn--ghost btn--icon topnav__menu-btn"
        onClick={onToggleMenu}
        aria-expanded={menuOpen}
        aria-controls={sidebarId}
        aria-label={menuOpen ? 'Close navigation menu' : 'Open navigation menu'}
      >
        <Icon name={menuOpen ? 'close' : 'menu'} />
      </button>
      <div className="topnav__spacer" />
    </header>
  );
}
