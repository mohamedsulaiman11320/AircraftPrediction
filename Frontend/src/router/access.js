import { hasAccess, hasRole, isPendingUser } from '../auth/roles.js';

/**
 * RBAC v2 (docs/API.md). The ONLY place in the UI that names roles. Role catalogues for
 * dropdowns are always fetched from GET /admin/roles.
 *  - FlightDispatcher: five screens only - health dashboard, fleet health, "Anomalies / Work Orders"
 *    (one menu entry owning both the anomaly and the work-order screens), maintenance log and user
 *    management.
 *  - MaintenanceTechnician: only their own assigned tickets.
 *  - DataEngineer: the Data area (telemetry, imports incl. raw JSON to ETL, data quality) and the
 *    Model area (pipelines, models without approve/reject, read-only anomalies and predictions,
 *    model-performance report). Aircraft LIST only, for filter dropdowns - no aircraft detail.
 * Hidden navigation and route guards are a convenience - the server enforces every permission.
 */
export const DISPATCHER = 'FlightDispatcher';
export const TECHNICIAN = 'MaintenanceTechnician';
export const DATA_ENGINEER = 'DataEngineer';

const D = [DISPATCHER];
const DE = [DATA_ENGINEER];
const DDE = [DISPATCHER, DATA_ENGINEER];
/**
 * "No role holds this." An empty array means "any active user" to hasAccess(), so a capability that
 * nobody may exercise is expressed as an unassignable role name instead. These capabilities were
 * revoked from the FlightDispatcher when the role was scoped to five tabs, and no other role holds
 * them yet; the screens and actions behind them stay in the codebase for a future owner.
 */
const NOBODY = ['__unassigned__'];

export const ACCESS = {
  // dispatcher screens
  dispatcherDashboard: D,
  fleetHealthDashboard: NOBODY,
  dataModelsDashboard: DE,
  fleet: D, // fleet list page + aircraft detail
  aircraftLookup: DDE, // GET /aircraft for filter dropdowns only
  fleetEdit: NOBODY,
  fleetStatus: D,
  inspectionFlags: D,
  telemetry: DE,
  telemetryImportsView: DE,
  telemetryImportUpload: DE,
  rawJsonUpload: DE, // POST /telemetry/raw-uploads (ETL path)
  importRetry: DE, // POST /telemetry/imports/{id}/retry (failed RawJson imports)
  rejectedRows: DE,
  dataQuality: DE,
  anomalies: DDE,
  anomalyActions: D,
  predictions: DE,
  predictionActions: NOBODY,
  workOrdersView: D,
  workOrderCreate: D,
  workOrderEdit: D, // PATCH
  technicians: D,
  pipelines: DE,
  modelsView: DE,
  developmentLab: DE, // /models/development: the isolated synthetic environment (GET/POST /development)
  modelRegister: DE,
  modelApprove: NOBODY,
  modelActivate: DE, // activate + retire
  reports: DDE,
  reportFleetHealth: D,
  reportMaintenance: D,
  reportDataQuality: DE,
  reportModelPerformance: DE,
  auditActions: NOBODY,
  auditAuth: NOBODY,
  adminUsers: DE, // user administration moved to the Data Engineer (API: Policies.DataEngineer)
  // technician screens
  myTickets: [TECHNICIAN],
  // shared: each ticket's buttons come from the role-filtered allowedTransitions
  workOrderDetail: [DISPATCHER, TECHNICIAN],
  workOrderActions: [DISPATCHER, TECHNICIAN],
  maintenanceLogCreate: [DISPATCHER, TECHNICIAN],
};

/**
 * Navigation table. `icon` names map to components/Icon.jsx. Optional `match` lists sibling path
 * prefixes that also mark the item as the current page (e.g. /work-orders under the
 * "Anomalies / Work Orders" entry, which owns both the anomaly and the work-order screens). Items
 * are grouped by `section` in this order, so the dispatcher resolves to exactly five top-level items, the technician to one, and the DataEngineer to three.
 *
 * The DataEngineer's "Data Model" menu covers more than one route and the sibling route is reached
 * through an in-page tab strip (pages/shared/DataEngineerTabs.jsx), so the data-engineer overview,
 * the standalone Anomalies, Risk predictions, Model & Forecast and Reports entries are no longer
 * listed. This is a NAVIGATION change only: every ACCESS capability and route guard below is
 * untouched, so those screens (/models, /predictions included) and the APIs behind them stay
 * reachable for the role that legitimately holds them.
 */
export const NAV_ITEMS = [
  { section: 'Overview', label: 'Health Dashboard', to: '/dashboard/dispatcher', icon: 'gauge', allow: ACCESS.dispatcherDashboard },
  { section: 'My work', label: 'My tickets', to: '/my-tickets', icon: 'wrench', allow: ACCESS.myTickets },
  { section: 'Operations', label: 'Fleet Health', to: '/fleet', icon: 'plane', allow: ACCESS.fleet },
  {
    section: 'Operations',
    label: 'Anomalies / Work Orders',
    to: '/anomalies',
    icon: 'alert',
    allow: ACCESS.workOrdersView,
    match: ['/anomalies', '/work-orders'],
  },
  // DataEngineer menu 1: upload & ingestion plus the telemetry explorer behind one entry.
  { section: 'Data', label: 'Data Model', to: '/imports', icon: 'upload', allow: ACCESS.telemetryImportsView, match: ['/imports', '/telemetry'] },
  { section: 'Data', label: 'Pipeline', to: '/pipelines', icon: 'flow', allow: ACCESS.pipelines },
  { section: 'Data', label: 'Data Quality', to: '/data-quality', icon: 'check', allow: ACCESS.dataQuality },
  // Operational model registry and the isolated development (synthetic) lab, as sibling tabs.
  { section: 'Model', label: 'Model Versions', to: '/models', icon: 'cube', allow: ACCESS.modelsView, match: ['/models'] },
  // The maintenance log is its own top-level destination; the Reports page no longer duplicates it.
  { section: 'Insight', label: 'Maintenance Log', to: '/maintenance-log', icon: 'file', allow: ACCESS.reportMaintenance, match: ['/maintenance-log'] },
  { section: 'Administration', label: 'User Management', to: '/admin/users', icon: 'users', allow: ACCESS.adminUsers },
];

/** Navigation grouped into sections for the given user. */
export function getNavSections(user) {
  const sections = [];
  for (const item of NAV_ITEMS) {
    if (!hasAccess(user, item.allow)) continue;
    let s = sections.find((x) => x.title === item.section);
    if (!s) {
      s = { title: item.section, items: [] };
      sections.push(s);
    }
    s.items.push(item);
  }
  return sections;
}

/** Landing page after sign-in. A user holding both roles lands on the dispatcher overview. */
export function defaultPathFor(user) {
  if (!user) return '/login';
  if (isPendingUser(user)) return '/pending';
  if (hasRole(user, DISPATCHER)) return '/dashboard/dispatcher';
  if (hasRole(user, DATA_ENGINEER)) return '/imports'; // menu 1, "Data Model"
  if (hasRole(user, TECHNICIAN)) return '/my-tickets';
  return '/pending';
}

export function isDispatcher(user) {
  return hasRole(user, DISPATCHER);
}
