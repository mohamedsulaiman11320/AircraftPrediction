import { useAuth } from '../auth/AuthContext.jsx';
import { getNavSections } from '../router/access.js';
import { Link, NavLink } from '../router/Link.jsx';
import { Icon } from './Icon.jsx';
import { defaultPathFor } from '../router/access.js';

export function BrandMark({ size = 20 }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d="M12 2l1.8 7 7.2 3-7.2 2L12 22l-1.8-8L3 12l7.2-3z" fill="#fff" />
    </svg>
  );
}

/** The `<` / `>` control at the right of the brand bar: collapses the sidebar to an icon rail (desktop only). */
function CollapseToggle({ collapsed, onToggle, sidebarId }) {
  const label = collapsed ? 'Expand sidebar' : 'Collapse sidebar';
  return (
    <button
      type="button"
      onClick={onToggle}
      aria-expanded={!collapsed}
      aria-controls={sidebarId}
      aria-label={label}
      title={label}
      data-testid="sidebar-toggle"
      className="sidebar__collapse flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border border-white/20 bg-transparent text-white hover:bg-white/10 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-white"
    >
      <Icon name={collapsed ? 'chevronRight' : 'chevronLeft'} size={18} />
    </button>
  );
}

export function Sidebar({ open, onNavigate, collapsed = false, onToggleCollapse, id = 'app-sidebar' }) {
  const { user } = useAuth();
  const sections = getNavSections(user);
  return (
    <aside id={id} className={`sidebar ${open ? 'sidebar--open' : ''} ${collapsed ? 'sidebar--collapsed' : ''}`} aria-label="Main navigation">
      <div className="sidebar__brand-bar">
        <Link to={defaultPathFor(user)} className="sidebar__brand min-w-0 flex-1" onClick={onNavigate} title="Aircraft Failure Prediction">
          <span className="sidebar__brand-mark">
            <BrandMark />
          </span>
          <span className="sidebar__brand-text">
            Aircraft Failure Prediction
            <small>Fleet health platform</small>
          </span>
        </Link>
        {onToggleCollapse ? <CollapseToggle collapsed={collapsed} onToggle={onToggleCollapse} sidebarId={id} /> : null}
      </div>
      <nav className="sidebar__nav" aria-label="Primary">
        {sections.map((section) => (
          <div key={section.title}>
            <p className="sidebar__section-label" id={`nav-${section.title}`}>
              {section.title}
            </p>
            <ul className="sidebar__list" aria-labelledby={`nav-${section.title}`}>
              {section.items.map((item) => (
                <li key={item.label}>
                  <NavLink to={item.to} match={item.match} className="sidebar__link" onClick={onNavigate} title={collapsed ? item.label : undefined}>
                    <Icon name={item.icon} />
                    <span>{item.label}</span>
                  </NavLink>
                </li>
              ))}
            </ul>
          </div>
        ))}
      </nav>
    </aside>
  );
}
