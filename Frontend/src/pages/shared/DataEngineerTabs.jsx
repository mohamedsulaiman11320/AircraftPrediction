import { useAuth } from '../../auth/AuthContext.jsx';
import { ACCESS } from '../../router/access.js';
import { useNavigate } from '../../router/Router.jsx';
import { Tabs } from '../../components/Misc.jsx';

/**
 * The DataEngineer's "Data Model" menu covers more than one route, so the sibling route is reached
 * through this in-page tab strip (same pattern as shared/OperationsTabs.jsx). Tabs the role cannot
 * open are dropped, and a strip with fewer than two tabs renders nothing, so a role holding only
 * one of the two screens keeps the page exactly as it was.
 *
 * The model/forecast strip was removed with the Model & Forecast navigation entry: /models and
 * /predictions stay reachable (routes and ACCESS capabilities are unchanged) but are no longer part
 * of the DataEngineer menu flow, so they render standalone.
 */
const TELEMETRY_TABS = [
  { id: 'imports', label: 'Upload & ingestion', to: '/imports', allow: ACCESS.telemetryImportsView },
  { id: 'telemetry', label: 'Telemetry', to: '/telemetry', allow: ACCESS.telemetry },
];

function TabStrip({ label, tabs: all, active }) {
  const { can } = useAuth();
  const navigate = useNavigate();
  const tabs = all.filter((t) => can(t.allow));
  if (tabs.length < 2) return null;
  return (
    <Tabs
      label={label}
      tabs={tabs}
      active={active}
      onChange={(id) => {
        const tab = tabs.find((t) => t.id === id);
        if (tab && tab.id !== active) navigate(tab.to);
      }}
    />
  );
}

/** Menu 1 — "Data Model": upload & ingestion (/imports) and the explorer (/telemetry). */
export function DataTelemetryTabs({ active }) {
  return <TabStrip label="Data and model telemetry" tabs={TELEMETRY_TABS} active={active} />;
}

const MODEL_TABS = [
  { id: 'operational', label: 'Operational models', to: '/models', allow: ACCESS.modelsView },
  { id: 'development', label: 'Development (synthetic)', to: '/models/development', allow: ACCESS.developmentLab },
];

/**
 * "Model Versions": the operational registry and the isolated development lab. They are separate tabs so a
 * synthetic candidate can never be mistaken for an operational model.
 */
export function ModelTabs({ active }) {
  return <TabStrip label="Model environments" tabs={MODEL_TABS} active={active} />;
}
