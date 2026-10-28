import { ACCESS } from './access.js';
import { LoginPage } from '../pages/auth/LoginPage.jsx';
import { RegisterPage } from '../pages/auth/RegisterPage.jsx';
import { ForgotPasswordPage } from '../pages/auth/ForgotPasswordPage.jsx';
import { ResetPasswordPage } from '../pages/auth/ResetPasswordPage.jsx';
import { AccessDeniedPage, PendingApprovalPage } from '../pages/auth/StatusPages.jsx';
import { HomeRedirect } from '../pages/dashboards/HomeRedirect.jsx';
import { DispatcherDashboard } from '../pages/dashboards/DispatcherDashboard.jsx';
import { MyTicketsPage } from '../pages/dashboards/TechnicianDashboard.jsx';
import { Redirect } from './Router.jsx';
import { DataEngineerDashboard } from '../pages/dashboards/DataEngineerDashboard.jsx';
import { LeadershipDashboard } from '../pages/dashboards/LeadershipDashboard.jsx';
import { FleetListPage } from '../pages/fleet/FleetListPage.jsx';
import { AircraftDetailPage } from '../pages/fleet/AircraftDetailPage.jsx';
import { AircraftFormPage } from '../pages/fleet/AircraftFormPage.jsx';
import { TelemetryPage } from '../pages/telemetry/TelemetryPage.jsx';
import { ImportsPage } from '../pages/telemetry/ImportsPage.jsx';
import { DataQualityPage } from '../pages/telemetry/DataQualityPage.jsx';
import { AnomaliesPage } from '../pages/anomalies/AnomaliesPage.jsx';
import { AnomalyDetailPage } from '../pages/anomalies/AnomalyDetailPage.jsx';
import { PredictionsPage } from '../pages/predictions/PredictionsPage.jsx';
import { WorkOrdersPage } from '../pages/workorders/WorkOrdersPage.jsx';
import { WorkOrderCreatePage } from '../pages/workorders/WorkOrderCreatePage.jsx';
import { WorkOrderDetailPage } from '../pages/workorders/WorkOrderDetailPage.jsx';
import { PipelinesPage } from '../pages/pipelines/PipelinesPage.jsx';
import { ModelsPage } from '../pages/models/ModelsPage.jsx';
import { ModelDetailPage } from '../pages/models/ModelDetailPage.jsx';
import { DevelopmentLabPage } from '../pages/models/DevelopmentLabPage.jsx';
import { MaintenanceLogPage, ReportsPage } from '../pages/reports/ReportsPage.jsx';
import { AuditPage } from '../pages/audit/AuditPage.jsx';
import { AdminUsersPage } from '../pages/admin/AdminUsersPage.jsx';
import { NotFoundPage } from '../pages/NotFoundPage.jsx';

/**
 * Route table. `layout`: 'public' (no auth), 'status' (standalone card page, auth optional),
 * 'app' (authenticated shell). `allow` lists role names from router/access.js (undefined = any active user).
 * Order matters: static segments before params (e.g. /fleet/new before /fleet/:id).
 */
export const routes = [
  { path: '/login', layout: 'public', element: <LoginPage /> },
  { path: '/register', layout: 'public', element: <RegisterPage /> },
  { path: '/forgot-password', layout: 'public', element: <ForgotPasswordPage /> },
  { path: '/reset-password', layout: 'public', element: <ResetPasswordPage /> },
  { path: '/pending', layout: 'status', element: <PendingApprovalPage /> },
  { path: '/access-denied', layout: 'status', element: <AccessDeniedPage /> },

  { path: '/', layout: 'app', element: <HomeRedirect /> },
  { path: '/dashboard/dispatcher', layout: 'app', allow: ACCESS.dispatcherDashboard, element: <DispatcherDashboard /> },
  { path: '/my-tickets', layout: 'app', allow: ACCESS.myTickets, element: <MyTicketsPage /> },
  { path: '/dashboard/technician', layout: 'app', allow: ACCESS.myTickets, element: <Redirect to="/my-tickets" /> },
  { path: '/dashboard/data-engineer', layout: 'app', allow: ACCESS.dataModelsDashboard, element: <DataEngineerDashboard /> },
  { path: '/dashboard/leadership', layout: 'app', allow: ACCESS.fleetHealthDashboard, element: <LeadershipDashboard /> },

  { path: '/fleet', layout: 'app', allow: ACCESS.fleet, element: <FleetListPage /> },
  { path: '/fleet/new', layout: 'app', allow: ACCESS.fleetEdit, element: <AircraftFormPage /> },
  { path: '/fleet/:id/edit', layout: 'app', allow: ACCESS.fleetEdit, element: <AircraftFormPage /> },
  { path: '/fleet/:id', layout: 'app', allow: ACCESS.fleet, element: <AircraftDetailPage /> },

  { path: '/telemetry', layout: 'app', allow: ACCESS.telemetry, element: <TelemetryPage /> },
  { path: '/imports', layout: 'app', allow: ACCESS.telemetryImportsView, element: <ImportsPage /> },
  { path: '/data-quality', layout: 'app', allow: ACCESS.dataQuality, element: <DataQualityPage /> },

  { path: '/anomalies', layout: 'app', allow: ACCESS.anomalies, element: <AnomaliesPage /> },
  { path: '/anomalies/:id', layout: 'app', allow: ACCESS.anomalies, element: <AnomalyDetailPage /> },
  { path: '/predictions', layout: 'app', allow: ACCESS.predictions, element: <PredictionsPage /> },

  { path: '/work-orders', layout: 'app', allow: ACCESS.workOrdersView, element: <WorkOrdersPage /> },
  { path: '/work-orders/new', layout: 'app', allow: ACCESS.workOrderCreate, element: <WorkOrderCreatePage /> },
  { path: '/work-orders/:id', layout: 'app', allow: ACCESS.workOrderDetail, element: <WorkOrderDetailPage /> },

  { path: '/pipelines', layout: 'app', allow: ACCESS.pipelines, element: <PipelinesPage /> },
  { path: '/models', layout: 'app', allow: ACCESS.modelsView, element: <ModelsPage /> },
  { path: '/models/development', layout: 'app', allow: ACCESS.developmentLab, element: <DevelopmentLabPage /> },
  { path: '/models/:id', layout: 'app', allow: ACCESS.modelsView, element: <ModelDetailPage /> },
  { path: '/reports', layout: 'app', allow: ACCESS.reports, element: <ReportsPage /> },
  { path: '/maintenance-log', layout: 'app', allow: ACCESS.reportMaintenance, element: <MaintenanceLogPage /> },
  { path: '/audit', layout: 'app', allow: ACCESS.auditActions, element: <AuditPage /> },
  { path: '/admin/users', layout: 'app', allow: ACCESS.adminUsers, element: <AdminUsersPage /> },

  { path: '*', layout: 'app', element: <NotFoundPage /> },
];
