import { useEffect, useState } from 'react';
import { Sidebar } from './Sidebar.jsx';
import { TopNav } from './TopNav.jsx';
import { useLocation } from '../router/Router.jsx';

const COLLAPSED_KEY = 'afp.sidebarCollapsed';

/** Per-browser convenience only; storage may be unavailable (private mode), so failures are ignored. */
function readCollapsed() {
  try {
    return localStorage.getItem(COLLAPSED_KEY) === '1';
  } catch {
    return false;
  }
}

export function AppLayout({ children }) {
  const [menuOpen, setMenuOpen] = useState(false);
  const [collapsed, setCollapsed] = useState(readCollapsed);
  const { pathname } = useLocation();

  useEffect(() => {
    try {
      localStorage.setItem(COLLAPSED_KEY, collapsed ? '1' : '0');
    } catch {
      /* not persisted */
    }
  }, [collapsed]);

  useEffect(() => {
    setMenuOpen(false);
  }, [pathname]);

  useEffect(() => {
    if (!menuOpen) return undefined;
    const onKey = (e) => {
      if (e.key === 'Escape') setMenuOpen(false);
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [menuOpen]);

  return (
    <>
      <a href="#main-content" className="skip-link">
        Skip to main content
      </a>
      <div className={`app-shell ${collapsed ? 'app-shell--collapsed' : ''}`}>
        <Sidebar
          open={menuOpen}
          onNavigate={() => setMenuOpen(false)}
          collapsed={collapsed}
          onToggleCollapse={() => setCollapsed((c) => !c)}
          id="app-sidebar"
        />
        {menuOpen ? <button type="button" className="drawer-backdrop" aria-label="Close navigation menu" onClick={() => setMenuOpen(false)} /> : null}
        <div className="main-col">
          <TopNav
            onToggleMenu={() => setMenuOpen((o) => !o)}
            menuOpen={menuOpen}
            sidebarId="app-sidebar"
          />
          <main id="main-content" className="workspace" tabIndex={-1}>
            {children}
          </main>
        </div>
      </div>
    </>
  );
}
