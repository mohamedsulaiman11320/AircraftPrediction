import { dashboardsApi } from '../../api/dashboards.js';
import { workOrdersApi } from '../../api/workOrders.js';
import { useAsync } from '../../hooks/useAsync.js';
import { PageHeader, Card } from '../../components/PageHeader.jsx';
import { KpiCard } from '../../components/KpiCard.jsx';
import { DataTable } from '../../components/DataTable.jsx';
import { ErrorState } from '../../components/States.jsx';
import { SkeletonKpis, SkeletonTable } from '../../components/Skeleton.jsx';
import { Icon } from '../../components/Icon.jsx';
import { workOrderColumns } from '../shared/columns.jsx';
import { countByStatus, sortMyTickets } from '../workorders/ticketOrder.js';
import { workOrderStatusLabel } from '../../utils/enums.js';

/** Largest page the API serves; more pages are fetched until every own ticket is loaded. */
const PAGE_SIZE = 200;
const MAX_PAGES = 20;

/** Every work order the API returns for the caller (GET /work-orders is own-tickets-only for technicians). */
async function listOwnTickets() {
  const first = await workOrdersApi.list({ page: 1, pageSize: PAGE_SIZE });
  const items = [...(first.items || [])];
  const pages = Math.min(first.totalPages || 1, MAX_PAGES);
  for (let page = 2; page <= pages; page++) {
    const next = await workOrdersApi.list({ page, pageSize: PAGE_SIZE });
    items.push(...(next.items || []));
  }
  return items;
}

/**
 * "My tickets" - the Maintenance Technician's work dashboard (/dashboard/technician redirects here).
 * Counts come from GET /dashboards/technician where it has them (IN_PROGRESS, REPORTED, overdue) and are
 * counted from the technician's own tickets (GET /work-orders) for ASSIGNED and COMPLETED.
 */
export function MyTicketsPage() {
  const { data, error, loading, reload } = useAsync(async () => {
    const [dashboard, tickets] = await Promise.all([dashboardsApi.technician(), listOwnTickets()]);
    return { dashboard, tickets };
  }, []);
  const header = (
    <PageHeader
      eyebrow="Maintenance"
      title="My tickets"
      subtitle="Work orders assigned to you, with the aircraft summary, diagnosis and evidence on each ticket."
      actions={
        <button type="button" className="btn btn--secondary" onClick={reload} disabled={loading}>
          <Icon name="refresh" size={16} /> Refresh
        </button>
      }
    />
  );
  if (error && !data)
    return (
      <>
        {header}
        <div className="card">
          <ErrorState error={error} onRetry={reload} />
        </div>
      </>
    );
  if (!data)
    return (
      <>
        {header}
        <SkeletonKpis count={5} />
        <div className="card">
          <SkeletonTable />
        </div>
      </>
    );
  const c = data.dashboard?.counts || {};
  const byStatus = countByStatus(data.tickets);
  const rows = sortMyTickets(data.tickets);
  const cols = workOrderColumns({ showAssignee: false, linkAircraft: false, showDiagnosis: true });
  return (
    <>
      {header}
      <div className="grid grid--kpi" aria-busy={loading}>
        <KpiCard label={workOrderStatusLabel('Assigned')} value={byStatus.Assigned || 0} tone="accent" hint="Assigned to you, not started" />
        <KpiCard label={workOrderStatusLabel('InInspection')} value={c.inInspection ?? byStatus.InInspection ?? 0} tone="warning" hint="Inspection in progress" />
        <KpiCard label={workOrderStatusLabel('Resolved')} value={c.awaitingClosure ?? byStatus.Resolved ?? 0} tone="neutral" hint="Report submitted, awaiting dispatcher review" />
        <KpiCard label={workOrderStatusLabel('Closed')} value={byStatus.Closed || 0} tone="success" hint="Accepted by the dispatcher" />
        <KpiCard label="Overdue" value={c.overdue ?? 0} tone={c.overdue ? 'danger' : 'neutral'} />
      </div>
      <div className="stack stack--lg">
        <Card title="My tickets" flush>
          <DataTable columns={cols} rows={rows} caption="My tickets" emptyTitle="No tickets are assigned to you" />
        </Card>
      </div>
    </>
  );
}
