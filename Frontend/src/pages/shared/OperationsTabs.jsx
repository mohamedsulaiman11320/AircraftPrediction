import { useAuth } from '../../auth/AuthContext.jsx';
import { ACCESS } from '../../router/access.js';
import { useNavigate } from '../../router/Router.jsx';
import { Tabs } from '../../components/Misc.jsx';
import { ProfileMenu, useSignOut } from '../../components/TopNav.jsx';

/**
 * Anomalies and work orders are one top-level tab for the dispatcher, but two routes. This renders
 * the shared sub-navigation at the top of both pages. Tabs the role cannot open are dropped, so a
 * DataEngineer (anomalies only) keeps the page exactly as it was.
 */
const TABS = [
  { id: 'anomalies', label: 'Anomalies', to: '/anomalies', allow: ACCESS.anomalies },
  { id: 'work-orders', label: 'Work orders', to: '/work-orders', allow: ACCESS.workOrdersView },
];

/** True when the tab strip is shown (the role can open both pages); the page then leaves the avatar to the strip. */
export function useOperationsTabsShown() {
  const { can } = useAuth();
  return TABS.filter((t) => can(t.allow)).length >= 2;
}

/** The tab strip, with the profile avatar at the right end of the same row (the page title row then has none). */
export function OperationsTabs({ active }) {
  const { can, user } = useAuth();
  const signOut = useSignOut();
  const navigate = useNavigate();
  const tabs = TABS.filter((t) => can(t.allow));
  if (tabs.length < 2) return null;
  return (
    <div className="tabs-bar mb-4 flex w-full items-center justify-between gap-3 border-b border-slate-200" data-testid="operations-tabs-bar">
      <Tabs
        label="Anomalies and work orders"
        tabs={tabs}
        active={active}
        onChange={(id) => {
          const tab = tabs.find((t) => t.id === id);
          if (tab && tab.id !== active) navigate(tab.to);
        }}
      />
      {user ? <ProfileMenu user={user} onSignOut={signOut} /> : null}
    </div>
  );
}
