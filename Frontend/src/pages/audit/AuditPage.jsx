import { useState } from 'react';
import { auditApi } from '../../api/audit.js';
import { useAsync } from '../../hooks/useAsync.js';
import { useListQuery } from '../../hooks/useListQuery.js';
import { useAuth } from '../../auth/AuthContext.jsx';
import { ACCESS } from '../../router/access.js';
import { PageHeader } from '../../components/PageHeader.jsx';
import { SearchFilterBar } from '../../components/SearchFilterBar.jsx';
import { DataTable } from '../../components/DataTable.jsx';
import { Pagination } from '../../components/Pagination.jsx';
import { ErrorState } from '../../components/States.jsx';
import { SkeletonTable } from '../../components/Skeleton.jsx';
import { JsonView, Tabs } from '../../components/Misc.jsx';
import { Modal } from '../../components/Modal.jsx';
import { formatDateTime, fromInputDate, humanize } from '../../utils/format.js';

function ActionAudit() {
  const { values, page, pageSize, setFilters, setPage, setPageSize, reset } = useListQuery({ tab: 'actions' });
  const [detail, setDetail] = useState(null);
  const fromUtc = fromInputDate(values.from);
  const toUtc = fromInputDate(values.to, true);
  const { data, error, loading, reload } = useAsync(
    () =>
      auditApi.actions({
        entityType: values.entityType || undefined,
        entityId: values.entityId || undefined,
        userId: values.userId || undefined,
        fromUtc,
        toUtc,
        page,
        pageSize,
      }),
    [values.entityType, values.entityId, values.userId, fromUtc, toUtc, page, pageSize],
  );
  const columns = [
    { key: 'occurredAtUtc', header: 'When (UTC)', sortable: true, render: (r) => <span className="nowrap">{formatDateTime(r.occurredAtUtc)}</span> },
    { key: 'userName', header: 'User', sortable: true, render: (r) => r.userName || 'System' },
    { key: 'action', header: 'Action', sortable: true, render: (r) => humanize(r.action) },
    { key: 'entityType', header: 'Entity', sortable: true },
    { key: 'entityId', header: 'Entity ID', render: (r) => <span className="mono" style={{ fontSize: 11 }}>{r.entityId}</span> },
    {
      key: 'details',
      header: 'Details',
      render: (r) =>
        r.details ? (
          <button type="button" className="link-btn" onClick={() => setDetail(r)}>
            View
          </button>
        ) : (
          '—'
        ),
    },
  ];
  return (
    <div className="card">
      <SearchFilterBar
        values={values}
        onChange={setFilters}
        onReset={reset}
        filters={[
          { name: 'entityType', label: 'Entity type', type: 'search', placeholder: 'e.g. WorkOrder' },
          { name: 'entityId', label: 'Entity ID', type: 'search' },
          { name: 'userId', label: 'User ID', type: 'search' },
          { name: 'from', label: 'From (UTC)', type: 'date' },
          { name: 'to', label: 'To (UTC)', type: 'date' },
        ]}
      />
      {error && !data ? (
        <ErrorState error={error} onRetry={reload} />
      ) : !data ? (
        <SkeletonTable />
      ) : (
        <div aria-busy={loading}>
          <DataTable columns={columns} rows={data.items} caption="Action audit" emptyTitle="No audit entries" />
          <Pagination page={data.page} pageSize={data.pageSize} totalCount={data.totalCount} totalPages={data.totalPages} onPageChange={setPage} onPageSizeChange={setPageSize} />
        </div>
      )}
      <Modal open={!!detail} title={detail ? `${humanize(detail.action)} · ${detail.entityType}` : ''} onClose={() => setDetail(null)} size="lg">
        {detail ? (
          <>
            <p className="subtle">
              {formatDateTime(detail.occurredAtUtc)} · {detail.userName || 'System'}
              {detail.correlationId ? ` · correlation ${detail.correlationId}` : ''}
            </p>
            <JsonView value={detail.details} label="Audit details" />
          </>
        ) : null}
      </Modal>
    </div>
  );
}

function AuthAudit() {
  const { values, page, pageSize, setFilters, setPage, setPageSize, reset } = useListQuery({ tab: 'auth' });
  const fromUtc = fromInputDate(values.from);
  const toUtc = fromInputDate(values.to, true);
  const { data, error, loading, reload } = useAsync(
    () => auditApi.auth({ email: values.email || undefined, eventType: values.eventType || undefined, fromUtc, toUtc, page, pageSize }),
    [values.email, values.eventType, fromUtc, toUtc, page, pageSize],
  );
  const columns = [
    { key: 'occurredAtUtc', header: 'When (UTC)', sortable: true, render: (r) => <span className="nowrap">{formatDateTime(r.occurredAtUtc)}</span> },
    { key: 'email', header: 'Email', sortable: true, render: (r) => r.email || '—' },
    { key: 'eventType', header: 'Event', sortable: true, render: (r) => humanize(r.eventType) },
    {
      key: 'succeeded',
      header: 'Result',
      sortable: true,
      render: (r) => <span className={`badge badge--${r.succeeded ? 'success' : 'danger'}`}>{r.succeeded ? 'Succeeded' : 'Failed'}</span>,
    },
    { key: 'ipAddress', header: 'IP', render: (r) => <span className="mono">{r.ipAddress || '—'}</span> },
    { key: 'detail', header: 'Detail', render: (r) => r.detail || '—' },
  ];
  return (
    <div className="card">
      <SearchFilterBar
        values={values}
        onChange={setFilters}
        onReset={reset}
        filters={[
          { name: 'email', label: 'Email', type: 'search' },
          { name: 'eventType', label: 'Event type', type: 'search', placeholder: 'e.g. LoginFailed' },
          { name: 'from', label: 'From (UTC)', type: 'date' },
          { name: 'to', label: 'To (UTC)', type: 'date' },
        ]}
      />
      {error && !data ? (
        <ErrorState error={error} onRetry={reload} />
      ) : !data ? (
        <SkeletonTable />
      ) : (
        <div aria-busy={loading}>
          <DataTable columns={columns} rows={data.items} caption="Authentication audit" emptyTitle="No authentication events" />
          <Pagination page={data.page} pageSize={data.pageSize} totalCount={data.totalCount} totalPages={data.totalPages} onPageChange={setPage} onPageSizeChange={setPageSize} />
        </div>
      )}
    </div>
  );
}

export function AuditPage() {
  const { can } = useAuth();
  const { values, setFilters } = useListQuery({ tab: 'actions' });
  const canAuth = can(ACCESS.auditAuth);
  const tab = canAuth && values.tab === 'auth' ? 'auth' : 'actions';
  const tabs = [{ id: 'actions', label: 'Business actions' }, ...(canAuth ? [{ id: 'auth', label: 'Authentication events' }] : [])];
  return (
    <>
      <PageHeader eyebrow="Insight" title="Audit log" subtitle="Who did what and when. Entries are append-only." />
      {tabs.length > 1 ? <Tabs label="Audit views" tabs={tabs} active={tab} onChange={(t) => setFilters({ tab: t })} /> : null}
      {tab === 'auth' ? <AuthAudit key="auth" /> : <ActionAudit key="actions" />}
    </>
  );
}
