import { useEffect } from 'react';
import { useAuth } from '../auth/AuthContext.jsx';
import { ProfileMenu, useSignOut } from './TopNav.jsx';

/**
 * Page title row: eyebrow, heading and description on the left; page actions and the profile avatar on the right.
 * `profile={false}` when a tab bar above the title carries the avatar instead.
 */
export function PageHeader({ title, subtitle, eyebrow, actions, documentTitle, profile = true }) {
  const { user } = useAuth();
  const signOut = useSignOut();
  useEffect(() => {
    document.title = `${documentTitle || title} · Aircraft Failure Prediction`;
  }, [title, documentTitle]);
  return (
    <header className="page-header">
      <div className="page-header__titles">
        {eyebrow ? <div className="page-header__eyebrow">{eyebrow}</div> : null}
        <h1>{title}</h1>
        {subtitle ? <p className="page-header__subtitle">{subtitle}</p> : null}
      </div>
      <div className="page-header__side">
        {actions ? <div className="page-header__actions">{actions}</div> : null}
        {user && profile ? <ProfileMenu user={user} onSignOut={signOut} /> : null}
      </div>
    </header>
  );
}

export function Card({ title, actions, children, flush = false, className = '', headingLevel = 2, id }) {
  const H = `h${headingLevel}`;
  return (
    <section className={`card ${className}`} aria-labelledby={title && id ? id : undefined}>
      {title || actions ? (
        <div className="card__header">
          {title ? <H id={id}>{title}</H> : <span />}
          {actions ? <div className="row">{actions}</div> : null}
        </div>
      ) : null}
      <div className={flush ? 'card__body--flush' : 'card__body'}>{children}</div>
    </section>
  );
}
