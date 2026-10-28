import { workOrdersApi } from '../../api/workOrders.js';
import { useAsync } from '../../hooks/useAsync.js';
import { useListQuery } from '../../hooks/useListQuery.js';
import { useAuth } from '../../auth/AuthContext.jsx';
import { ACCESS } from '../../router/access.js';
import { hasRole } from '../../auth/roles.js';
import { TECHNICIAN } from '../../router/access.js';
import { Link } from '../../router/Link.jsx';
import { PageHeader } from '../../components/PageHeader.jsx';
import { SearchFilterBar } from '../../components/SearchFilterBar.jsx';
import { DataTable } from '../../components/DataTable.jsx';
import { Pagination } from '../../components/Pagination.jsx';
import { ErrorState } from '../../components/States.jsx';
import { SkeletonTable } from '../../components/Skeleton.jsx';
import { Icon } from '../../components/Icon.jsx';
import { workOrderColumns } from '../shared/columns.jsx';
import { useAircraftOptions } from '../shared/AircraftSelect.jsx';
import { OperationsTabs, useOperationsTabsShown } from '../shared/OperationsTabs.jsx';
import { WO_PRIORITY, WO_SOURCE, options, workOrderStatusOptions } from '../../utils/enums.js';

export function WorkOrdersPage() {
  const tabsShown = useOperationsTabsShown();
  const { can, user } = useAuth();
  const { values, page, pageSize, setFilters, setPage, setPageSize, reset } = useListQuery();
  const aircraft = useAircraftOptions();
  const assignedToMe = values.assignedToMe === 'true';
  const { data, error, loading, reload } = useAsync(
    () =>
      workOrdersApi.list({
        aircraftId: values.aircraftId || undefined,
        status: values.status || undefined,
        priority: values.priority || undefined,
        source: values.source || undefined,
        assignedToMe,
        page,
        pageSize,
      }),
    [values.aircraftId, values.status, values.priority, values.source, assignedToMe, page, pageSize],
  );

  const filters = [
    { name: 'aircraftId', label: 'Aircraft', type: 'select', options: aircraft.data || [] },
    { name: 'status', label: 'Status', type: 'select', options: workOrderStatusOptions() },
    { name: 'priority', label: 'Priority', type: 'select', options: options(WO_PRIORITY) },
    { name: 'source', label: 'Source', type: 'select', options: options(WO_SOURCE) },
  ];
  if (hasRole(user, TECHNICIAN)) filters.push({ name: 'assignedToMe', label: 'Assigned to me', type: 'checkbox' });

  return (
    <>
      <OperationsTabs active="work-orders" />
      <PageHeader
        profile={!tabsShown}
        eyebrow="Operations"
        title="Work orders"
        subtitle="Maintenance work from creation through inspection, resolution and close-out."
        actions={
          can(ACCESS.workOrderCreate) ? (
            <Link to="/work-orders/new" className="btn btn--primary">
              <Icon name="plus" size={16} /> New work order
            </Link>
          ) : null
        }
      />
      <div className="card">
        <SearchFilterBar values={values} onChange={setFilters} onReset={reset} filters={filters} />
        {error && !data ? (
          <ErrorState error={error} onRetry={reload} />
        ) : !data ? (
          <SkeletonTable />
        ) : (
          <div aria-busy={loading}>
            <DataTable columns={workOrderColumns()} rows={data.items} caption="Work orders" emptyTitle="No work orders match these filters" />
            <Pagination page={data.page} pageSize={data.pageSize} totalCount={data.totalCount} totalPages={data.totalPages} onPageChange={setPage} onPageSizeChange={setPageSize} />
          </div>
        )}
      </div>
    </>
  );
}
