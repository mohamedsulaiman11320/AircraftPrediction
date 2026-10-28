import { useEffect, useState } from 'react';
import { adminApi } from '../../api/admin.js';
import { useAsync } from '../../hooks/useAsync.js';
import { useListQuery } from '../../hooks/useListQuery.js';
import { useAuth } from '../../auth/AuthContext.jsx';
import { roleLabel, userRoles } from '../../auth/roles.js';
import { PageHeader } from '../../components/PageHeader.jsx';
import { SearchFilterBar } from '../../components/SearchFilterBar.jsx';
import { DataTable } from '../../components/DataTable.jsx';
import { Pagination } from '../../components/Pagination.jsx';
import { StatusBadge } from '../../components/StatusBadge.jsx';
import { ErrorState } from '../../components/States.jsx';
import { SkeletonTable } from '../../components/Skeleton.jsx';
import { ConfirmDialog } from '../../components/Modal.jsx';
import { TextArea } from '../../components/FormFields.jsx';
import { useToast } from '../../components/Toast.jsx';
import { USER_STATUS, options } from '../../utils/enums.js';
import { formatDateTime } from '../../utils/format.js';

/** Role catalogue from GET /admin/roles (never hard-coded). */
export function useRoleCatalogue() {
  return useAsync(() => adminApi.roles(), []);
}

/** Accessible checkbox group for choosing one or more roles. */
export function RolePicker({ roles, value, onChange, error, loading, loadError }) {
  return (
    <fieldset className="field" style={{ border: 0, padding: 0 }} aria-describedby={error ? 'role-picker-err' : undefined}>
      <legend className="field__label">
        Roles<span className="field__required" aria-hidden="true">*</span>
      </legend>
      {loading ? <p className="subtle">Loading roles…</p> : null}
      {loadError ? <p className="field__error">Could not load roles from the server.</p> : null}
      {(roles || []).map((r) => (
        <label key={r.roleId} className="checkbox" style={{ marginBottom: 6 }}>
          <input
            type="checkbox"
            checked={value.includes(r.roleName)}
            onChange={(e) => onChange(e.target.checked ? [...value, r.roleName] : value.filter((x) => x !== r.roleName))}
          />
          <span>{roleLabel(r.roleName)}</span>
        </label>
      ))}
      {error ? (
        <span className="field__error" id="role-picker-err">
          {error}
        </span>
      ) : null}
    </fieldset>
  );
}

function UserDialog({ dialog, roleCatalogue, onClose, onDone }) {
  const [roles, setRoles] = useState([]);
  const [reason, setReason] = useState('');
  const [error, setError] = useState(null);
  const u = dialog?.user;
  useEffect(() => {
    setRoles(userRoles(dialog?.user));
    setReason('');
    setError(null);
  }, [dialog]);
  if (!dialog) return null;
  const cfg = {
    approve: { title: `Approve ${u.displayName}`, label: 'Approve', message: 'The account becomes Active with the selected roles.' },
    roles: { title: `Change roles · ${u.displayName}`, label: 'Save roles', message: 'Changing roles signs the user out of all sessions.' },
    disable: { title: `Disable ${u.displayName}`, label: 'Disable account', tone: 'danger', message: 'The user is signed out of all sessions and can no longer sign in.' },
    enable: { title: `Enable ${u.displayName}`, label: 'Enable account', message: 'The user will be able to sign in again.' },
  }[dialog.type];

  const confirm = async () => {
    let result;
    if (dialog.type === 'approve' || dialog.type === 'roles') {
      if (roles.length === 0) {
        setError('Choose at least one role.');
        return;
      }
      result = dialog.type === 'approve' ? await adminApi.approve(u.id, roles) : await adminApi.setRoles(u.id, roles);
    } else if (dialog.type === 'disable') {
      if (!reason.trim()) {
        setError('A reason is required.');
        return;
      }
      result = await adminApi.disable(u.id, reason.trim());
    } else {
      result = await adminApi.enable(u.id);
    }
    onDone(result, `${cfg.label}: done.`);
  };

  return (
    <ConfirmDialog open title={cfg.title} message={`${u.username} · ${u.email}. ${cfg.message}`} confirmLabel={cfg.label} tone={cfg.tone || 'primary'} onConfirm={confirm} onClose={onClose}>
      {dialog.type === 'approve' || dialog.type === 'roles' ? (
        <RolePicker roles={roleCatalogue.data} loading={roleCatalogue.loading} loadError={roleCatalogue.error} value={roles} onChange={setRoles} error={error} />
      ) : null}
      {dialog.type === 'disable' ? <TextArea label="Reason" value={reason} onChange={(e) => setReason(e.target.value)} error={error} maxLength={1000} required /> : null}
    </ConfirmDialog>
  );
}

export function AdminUsersPage() {
  const { user: me } = useAuth();
  const toast = useToast();
  const roleCatalogue = useRoleCatalogue();
  const { values, page, pageSize, setFilters, setPage, setPageSize, reset } = useListQuery();
  const [dialog, setDialog] = useState(null);
  const { data, error, loading, reload, setData } = useAsync(
    () => adminApi.listUsers({ status: values.status || undefined, role: values.role || undefined, search: values.search || undefined, page, pageSize }),
    [values.status, values.role, values.search, page, pageSize],
  );

  const done = (updated, msg) => {
    setDialog(null);
    toast.success(msg);
    setData((d) => (d ? { ...d, items: d.items.map((x) => (x.id === updated.id ? updated : x)) } : d));
  };

  const columns = [
    {
      key: 'displayName',
      header: 'User',
      sortable: true,
      render: (u) => (
        <div>
          <strong>{u.displayName}</strong>
          <div className="subtle">
            <span className="mono">{u.username}</span> · {u.email}
          </div>
        </div>
      ),
    },
    {
      key: 'roles',
      header: 'Roles',
      sortable: true,
      sortValue: (u) => userRoles(u).join(','),
      render: (u) =>
        userRoles(u).length ? (
          <div className="chips">
            {userRoles(u).map((r) => (
              <span key={r} className="badge badge--info badge--plain">
                {roleLabel(r)}
              </span>
            ))}
          </div>
        ) : (
          <span className="muted">None</span>
        ),
    },
    {
      key: 'status',
      header: 'Status',
      sortable: true,
      render: (u) => (
        <div>
          <StatusBadge value={u.status} />
          {u.lockoutEndUtc && Date.parse(u.lockoutEndUtc) > Date.now() ? <div className="subtle text-danger">Locked until {formatDateTime(u.lockoutEndUtc)}</div> : null}
          {u.disabledReason ? <div className="subtle">{u.disabledReason}</div> : null}
        </div>
      ),
    },
    { key: 'createdAtUtc', header: 'Registered', sortable: true, render: (u) => <span className="nowrap">{formatDateTime(u.createdAtUtc)}</span> },
    { key: 'lastLoginAtUtc', header: 'Last sign-in', sortable: true, render: (u) => <span className="nowrap">{formatDateTime(u.lastLoginAtUtc)}</span> },
    { key: 'failedLoginCount', header: 'Failed sign-ins', numeric: true, sortable: true },
    {
      key: 'actions',
      header: <span className="visually-hidden">Actions</span>,
      render: (u) => {
        const self = me?.id === u.id;
        return (
          <div className="row" style={{ flexWrap: 'nowrap' }}>
            {u.status === 'Pending' ? (
              <button type="button" className="btn btn--primary btn--sm" onClick={() => setDialog({ type: 'approve', user: u })}>
                Approve
              </button>
            ) : null}
            {u.status === 'Active' ? (
              <button type="button" className="btn btn--secondary btn--sm" onClick={() => setDialog({ type: 'roles', user: u })}>
                Change roles
              </button>
            ) : null}
            {u.status !== 'Disabled' && !self ? (
              <button type="button" className="btn btn--ghost btn--sm" onClick={() => setDialog({ type: 'disable', user: u })}>
                Disable
              </button>
            ) : null}
            {u.status === 'Disabled' ? (
              <button type="button" className="btn btn--secondary btn--sm" onClick={() => setDialog({ type: 'enable', user: u })}>
                Enable
              </button>
            ) : null}
          </div>
        );
      },
    },
  ];

  const roleOptions = (roleCatalogue.data || []).map((r) => ({ value: r.roleName, label: roleLabel(r.roleName) }));

  return (
    <>
      <PageHeader eyebrow="Administration" title="User management" subtitle="Approve registrations, assign roles and manage account access." />
      <div className="card">
        <SearchFilterBar
          values={values}
          onChange={setFilters}
          onReset={reset}
          filters={[
            { name: 'search', label: 'Search', type: 'search', placeholder: 'Username, name or email' },
            { name: 'status', label: 'Status', type: 'select', options: options(USER_STATUS) },
            { name: 'role', label: 'Role', type: 'select', options: roleOptions },
          ]}
        />
        {error && !data ? (
          <ErrorState error={error} onRetry={reload} />
        ) : !data ? (
          <SkeletonTable />
        ) : (
          <div aria-busy={loading}>
            <DataTable columns={columns} rows={data.items} caption="Users" emptyTitle="No users match these filters" />
            <Pagination page={data.page} pageSize={data.pageSize} totalCount={data.totalCount} totalPages={data.totalPages} onPageChange={setPage} onPageSizeChange={setPageSize} />
          </div>
        )}
      </div>
      <UserDialog dialog={dialog} roleCatalogue={roleCatalogue} onClose={() => setDialog(null)} onDone={done} />
    </>
  );
}
