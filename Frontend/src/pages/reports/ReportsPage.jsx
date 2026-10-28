import { useState } from 'react';
import { reportsApi } from '../../api/reports.js';
import { maintenanceLogsApi } from '../../api/aircraft.js';
import { workOrdersApi } from '../../api/workOrders.js';
import { useAsync } from '../../hooks/useAsync.js';
import { useListQuery } from '../../hooks/useListQuery.js';
import { useAuth } from '../../auth/AuthContext.jsx';
import { ACCESS } from '../../router/access.js';
import { useQuery } from '../../router/Router.jsx';
import { Link } from '../../router/Link.jsx';
import { PageHeader, Card } from '../../components/PageHeader.jsx';
import { DataTable } from '../../components/DataTable.jsx';
import { Pagination } from '../../components/Pagination.jsx';
import { SearchFilterBar } from '../../components/SearchFilterBar.jsx';
import { BarChart } from '../../components/charts/BarChart.jsx';
import { RiskBadge, StatusBadge } from '../../components/StatusBadge.jsx';
import { EmptyState, ErrorState } from '../../components/States.jsx';
import { SkeletonTable } from '../../components/Skeleton.jsx';
import { KeyValueList, Tabs } from '../../components/Misc.jsx';
import { TextField } from '../../components/FormFields.jsx';
import { useToast } from '../../components/Toast.jsx';
import { Icon } from '../../components/Icon.jsx';
import { DataQualityView } from '../telemetry/DataQualityPage.jsx';
import { useAircraftOptions } from '../shared/AircraftSelect.jsx';
import { workOrderColumns } from '../shared/columns.jsx';
import { MAINTENANCE_EVENT, options } from '../../utils/enums.js';
import { formatDateTime, formatNumber, fromInputDate, humanize } from '../../utils/format.js';

const MAINTENANCE_LOG_DESCRIPTION =
  'Read-only maintenance history recorded against each aircraft. Completion reports are filed from the work order, not from here.';

/**
 * Report tabs. Fleet health and the maintenance log are deliberately absent: both are top-level
 * navigation destinations of their own, and a tab that duplicates a menu entry is dead weight.
 * reportsApi.fleetHealth and the REPORTS path map in api/reports.js are untouched.
 */
const REPORTS = [
  { id: 'maintenanceHistory', label: 'Maintenance history', allow: ACCESS.reportMaintenance, description: 'Monthly scheduled, unscheduled and inspection counts, work orders opened/closed and mean resolution time.' },
  { id: 'dataQuality', label: 'Data quality', allow: ACCESS.reportDataQuality, description: 'Telemetry validity and import outcomes.' },
  { id: 'modelPerformance', label: 'Model performance', allow: ACCESS.reportModelPerformance, description: 'Model versions with stored evaluation metrics, anomaly investigation outcomes and prediction outcome counts.' },
];

const ISO_RE = /^\d{4}-\d{2}-\d{2}T/;

function renderCell(key, v) {
  if (v === null || v === undefined) return '—';
  if (typeof v === 'boolean') return v ? 'Yes' : 'No';
  if (typeof v === 'number') return Number.isInteger(v) ? formatNumber(v) : formatNumber(v, 3);
  if (typeof v === 'string') {
    if (/risk/i.test(key)) return <RiskBadge value={v} />;
    if (/status$/i.test(key)) return <StatusBadge value={v} />;
    if (ISO_RE.test(v)) return formatDateTime(v);
    return v;
  }
  if (Array.isArray(v)) return v.map((x) => (typeof x === 'object' ? JSON.stringify(x) : x)).join(', ');
  return <span className="mono">{JSON.stringify(v)}</span>;
}

/** Renders any report payload: scalars as key/values, objects as sections, arrays of rows as tables. */
export function GenericReport({ data }) {
  if (data === null || data === undefined) return <EmptyState title="No report data" />;
  if (Array.isArray(data)) return <RowsTable name="Rows" rows={data} />;
  const scalars = {};
  const sections = [];
  Object.entries(data).forEach(([k, v]) => {
    if (Array.isArray(v)) sections.push(<RowsTable key={k} name={humanize(k)} rows={v} />);
    else if (v && typeof v === 'object')
      sections.push(
        <Card key={k} title={humanize(k)}>
          <KeyValueList data={v} format={(val, key) => renderCell(key, val)} />
        </Card>,
      );
    else scalars[k] = v;
  });
  return (
    <div className="stack stack--lg">
      {Object.keys(scalars).length ? (
        <Card title="Summary">
          <KeyValueList data={scalars} format={(val, key) => renderCell(key, val)} />
        </Card>
      ) : null}
      {sections}
    </div>
  );
}

function RowsTable({ name, rows }) {
  if (!rows.length)
    return (
      <Card title={name}>
        <p className="muted">No rows.</p>
      </Card>
    );
  if (typeof rows[0] !== 'object') {
    return (
      <Card title={name}>
        <p>{rows.join(', ')}</p>
      </Card>
    );
  }
  const keys = Array.from(new Set(rows.slice(0, 50).flatMap((r) => Object.keys(r)))).filter((k) => !/^(id|aircraftId|modelVersionId)$/.test(k));
  const columns = keys.map((k) => ({
    key: k,
    header: humanize(k),
    sortable: true,
    numeric: typeof rows[0][k] === 'number',
    render: (r) => renderCell(k, r[k]),
  }));
  return (
    <Card title={name} flush>
      <DataTable columns={columns} rows={rows} rowKey={(r, i) => r.id || r.aircraftId || r.month || i} caption={name} />
    </Card>
  );
}

function MaintenanceChart({ data }) {
  const rows = Array.isArray(data) ? data : Object.values(data || {}).find((v) => Array.isArray(v) && v[0] && 'month' in v[0]);
  if (!rows?.length) return null;
  return (
    <Card title="Monthly maintenance events">
      <BarChart
        ariaLabel="Monthly maintenance events"
        data={rows.map((r) => ({ label: r.month, scheduled: r.scheduled, unscheduled: r.unscheduled, inspections: r.inspections ?? r.inspection }))}
        series={[
          { key: 'scheduled', name: 'Scheduled', color: 'var(--chart-1)' },
          { key: 'unscheduled', name: 'Unscheduled', color: 'var(--chart-2)' },
          { key: 'inspections', name: 'Inspections', color: 'var(--chart-4)' },
        ]}
      />
    </Card>
  );
}

function ReportView({ report }) {
  const toast = useToast();
  const [range, setRange] = useState({ from: '', to: '' });
  const [exporting, setExporting] = useState(false);
  const query = report.id === 'maintenanceHistory' ? { fromUtc: fromInputDate(range.from), toUtc: fromInputDate(range.to, true) } : {};
  const { data, error, loading, reload } = useAsync(() => reportsApi[report.id](query), [report.id, query.fromUtc, query.toUtc]);

  const exportCsv = async () => {
    setExporting(true);
    try {
      await reportsApi.exportCsv(report.id, Object.fromEntries(Object.entries(query).filter(([, v]) => v)));
      toast.success(`${report.label} CSV downloaded.`);
    } catch (err) {
      toast.error(err);
    } finally {
      setExporting(false);
    }
  };

  const isDq = report.id === 'dataQuality' && data && 'totalRecords' in data;
  return (
    <div className="stack stack--lg">
      <div className="row row--between">
        <p className="muted" style={{ margin: 0 }}>
          {report.description}
        </p>
        <button type="button" className="btn btn--primary" onClick={exportCsv} disabled={exporting}>
          {exporting ? <span className="spinner" aria-hidden="true" /> : <Icon name="download" size={16} />}
          Export CSV
        </button>
      </div>
      {report.id === 'maintenanceHistory' ? (
        <div className="row">
          <TextField label="From (UTC)" type="date" value={range.from} onChange={(e) => setRange((r) => ({ ...r, from: e.target.value }))} />
          <TextField label="To (UTC)" type="date" value={range.to} onChange={(e) => setRange((r) => ({ ...r, to: e.target.value }))} />
        </div>
      ) : null}
      {error && !data ? (
        <div className="card">
          <ErrorState error={error} onRetry={reload} />
        </div>
      ) : !data ? (
        <div className="card">
          <SkeletonTable />
        </div>
      ) : (
        <div aria-busy={loading} className="stack stack--lg">
          {report.id === 'maintenanceHistory' ? <MaintenanceChart data={data} /> : null}
          {isDq ? <DataQualityView data={data} /> : <GenericReport data={data} />}
        </div>
      )}
    </div>
  );
}

/**
 * Read-only maintenance history from GET /maintenance-logs. There is deliberately no create form:
 * a maintenance completion report is filed against its work order, never from here.
 */
export function MaintenanceLogView({ description = MAINTENANCE_LOG_DESCRIPTION }) {
  const { values, page, pageSize, setFilters, setPage, setPageSize } = useListQuery();
  const aircraft = useAircraftOptions();
  const { data, error, loading, reload } = useAsync(
    () =>
      maintenanceLogsApi.list({
        aircraftId: values.aircraftId || undefined,
        eventType: values.eventType || undefined,
        page,
        pageSize,
      }),
    [values.aircraftId, values.eventType, page, pageSize],
  );
  const filtered = !!(values.aircraftId || values.eventType);

  const columns = [
    { key: 'registrationNumber', header: 'Aircraft', sortable: true, render: (m) => <Link to={`/fleet/${m.aircraftId}`}>{m.registrationNumber}</Link> },
    { key: 'eventType', header: 'Event type', sortable: true, render: (m) => <StatusBadge value={m.eventType} /> },
    { key: 'occurredAtUtc', header: 'Occurred at', sortable: true, render: (m) => <span className="nowrap">{formatDateTime(m.occurredAtUtc)}</span> },
    { key: 'description', header: 'Description' },
    { key: 'isVerified', header: 'Verified', sortable: true, render: (m) => (m.isVerified ? 'Yes' : 'No') },
    {
      key: 'workOrderId',
      header: 'Linked work order',
      render: (m) => (m.workOrderId ? <Link to={`/work-orders/${m.workOrderId}`}>{m.workOrderNumber || 'View work order'}</Link> : <span className="muted">Not linked</span>),
    },
  ];

  return (
    <div className="stack stack--lg">
      <p className="muted" style={{ margin: 0 }}>
        {description}
      </p>
      <div className="card">
        <SearchFilterBar
          values={values}
          onChange={setFilters}
          onReset={() => setFilters({ aircraftId: '', eventType: '' })}
          filters={[
            { name: 'aircraftId', label: 'Aircraft', type: 'select', options: aircraft.data || [] },
            { name: 'eventType', label: 'Event type', type: 'select', options: options(MAINTENANCE_EVENT) },
          ]}
        />
        {error && !data ? (
          <ErrorState error={error} onRetry={reload} />
        ) : !data ? (
          <SkeletonTable />
        ) : (
          <div aria-busy={loading}>
            <DataTable
              columns={columns}
              rows={data.items}
              caption="Maintenance log"
              emptyTitle={filtered ? 'No maintenance records match these filters' : 'No maintenance records recorded yet'}
            />
            <Pagination page={data.page} pageSize={data.pageSize} totalCount={data.totalCount} totalPages={data.totalPages} onPageChange={setPage} onPageSizeChange={setPageSize} />
          </div>
        )}
      </div>
    </div>
  );
}

/** The maintenance log as its own top-level screen, reached from the Maintenance Log menu entry. */
/**
 * Work orders whose technician report was submitted (status Resolved = REPORTED) but not yet accepted.
 * Deliberately separate from the maintenance history: submitted work is not completed maintenance.
 */
export function ReportsAwaitingReview() {
  const { data, error, loading, reload } = useAsync(() => workOrdersApi.list({ status: 'Resolved', page: 1, pageSize: 100 }), []);
  const columns = [
    ...workOrderColumns({ showAssignee: true }).filter((c) => c.key !== 'dueAtUtc'),
    { key: 'resolvedAtUtc', header: 'Report submitted', sortable: true, render: (w) => <span className="nowrap">{formatDateTime(w.resolvedAtUtc)}</span> },
    {
      key: 'review',
      header: 'Review',
      render: (w) => (
        <Link to={`/work-orders/${w.id}`} aria-label={`Review ${w.workOrderNumber}`}>
          Review report
        </Link>
      ),
    },
  ];
  return (
    <Card title="Reports awaiting your review" id="reports-awaiting-review" flush>
      <p className="muted" style={{ margin: 'var(--space-4) var(--space-4) 0' }}>
        Technician reports that have been submitted (REPORTED) but not yet accepted. They are not part of the maintenance history until you complete the work order.
      </p>
      {error && !data ? (
        <ErrorState error={error} onRetry={reload} />
      ) : !data ? (
        <SkeletonTable rows={3} />
      ) : (
        <div aria-busy={loading}>
          <DataTable columns={columns} rows={data.items} caption="Reports awaiting your review" emptyTitle="No reports are waiting for review" />
          {data.totalCount > (data.items?.length || 0) ? (
            <p className="subtle" style={{ margin: 'var(--space-3) var(--space-4)' }}>
              Showing {data.items.length} of {data.totalCount}. <Link to="/work-orders?status=Resolved">See all REPORTED work orders</Link>
            </p>
          ) : null}
        </div>
      )}
    </Card>
  );
}

export function MaintenanceLogPage() {
  return (
    <>
      <PageHeader eyebrow="Insight" title="Maintenance Log" subtitle="Accepted maintenance history, with submitted technician reports listed separately." />
      <div className="stack stack--lg">
        <ReportsAwaitingReview />
        <section aria-labelledby="maintenance-history-heading" className="stack">
          <h2 id="maintenance-history-heading" style={{ margin: 0 }}>
            Maintenance history (completed and imported)
          </h2>
          <MaintenanceLogView />
        </section>
      </div>
    </>
  );
}

export function ReportsPage() {
  const { can } = useAuth();
  const [query, setQuery] = useQuery();
  const available = REPORTS.filter((r) => can(r.allow));
  const active = available.find((r) => r.id === query.report) || available[0];
  return (
    <>
      <PageHeader eyebrow="Insight" title="Reports" subtitle="Operational reports with CSV export." />
      {available.length === 0 ? (
        <div className="card">
          <EmptyState title="No reports available for your role" />
        </div>
      ) : (
        <>
          {/* A single remaining report needs no tab strip. */}
          {available.length > 1 ? <Tabs label="Reports" tabs={available} active={active.id} onChange={(id) => setQuery({ report: id })} /> : null}
          <ReportView key={active.id} report={active} />
        </>
      )}
    </>
  );
}
