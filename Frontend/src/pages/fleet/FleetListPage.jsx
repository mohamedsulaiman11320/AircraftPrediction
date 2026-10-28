import { useState } from 'react';
import { aircraftApi } from '../../api/aircraft.js';
import { useAsync } from '../../hooks/useAsync.js';
import { useListQuery } from '../../hooks/useListQuery.js';
import { useAuth } from '../../auth/AuthContext.jsx';
import { ACCESS } from '../../router/access.js';
import { PageHeader } from '../../components/PageHeader.jsx';
import { SearchFilterBar } from '../../components/SearchFilterBar.jsx';
import { DataTable } from '../../components/DataTable.jsx';
import { Pagination } from '../../components/Pagination.jsx';
import { AircraftStateBadge, RiskBadge } from '../../components/StatusBadge.jsx';
import { ErrorState } from '../../components/States.jsx';
import { SkeletonTable } from '../../components/Skeleton.jsx';
import { Link } from '../../router/Link.jsx';
import { aircraftModelText, formatDateTime, formatRelative } from '../../utils/format.js';
import { AIRCRAFT_STATUS, options } from '../../utils/enums.js';
import { Icon } from '../../components/Icon.jsx';
import { HealthMeasure, HealthReadingTime, ThresholdDisclaimer, ThresholdStatus } from './AircraftHealth.jsx';
import { FleetHealthGraph } from './FleetHealthGraph.jsx';
import { FleetConditionCard, fleetCondition } from './FleetConditionCard.jsx';

export function FleetListPage() {
  const { can } = useAuth();
  const { values, page, pageSize, setFilters, setPage, setPageSize, reset } = useListQuery();
  const [condition, setCondition] = useState(null);
  const { data, error, loading, reload } = useAsync(
    () => aircraftApi.list({ search: values.search, status: values.status, page, pageSize }),
    [values.search, values.status, page, pageSize],
  );

  // Six columns sharing 100% of the width (user decision 2026-10-28); readings, threshold status and counts are folded in.
  const count = (n, one, many) => `${n ?? 0} ${n === 1 ? one : many}`;
  const columns = [
    {
      key: 'registrationNumber',
      header: 'Aircraft',
      width: '16.66%',
      sortable: true,
      render: (a) => (
        <>
          <Link to={`/fleet/${a.id}`} className="row-link">
            {a.registrationNumber}
          </Link>
          {aircraftModelText(a) ? <div className="subtle">{aircraftModelText(a)}</div> : null}
        </>
      ),
    },
    {
      key: 'derivedState',
      header: 'Status',
      width: '16.66%',
      sortable: true,
      sortValue: (a) => a.derivedState || a.operationalStatus,
      render: (a) => <AircraftStateBadge aircraft={a} title={a.statusReason || undefined} />,
    },
    {
      key: 'risk',
      header: 'Risk indicator',
      width: '16.66%',
      render: (a) => (
        <div className="flex flex-col items-start gap-1">
          <RiskBadge value={a.latestRisk?.riskCategory} />
          <ThresholdStatus health={a.health} />
        </div>
      ),
    },
    {
      key: 'openAnomalyCount',
      header: 'Anomalies / work orders',
      width: '16.66%',
      sortable: true,
      render: (a) => (
        <span data-testid="open-items">
          <span className="nowrap">{count(a.openAnomalyCount, 'anomaly', 'anomalies')}</span> ·{' '}
          <span className="nowrap">{count(a.activeInspectionFlagCount, 'flag', 'flags')}</span> ·{' '}
          <span className="nowrap">{count(a.openWorkOrderCount, 'open WO', 'open WOs')}</span>
        </span>
      ),
    },
    {
      key: 'lastTelemetryAtUtc',
      header: 'Latest telemetry',
      width: '16.66%',
      sortable: true,
      render: (a) => (
        <div title={a.health?.latestReadingAtUtc ? `Reading ${formatDateTime(a.health.latestReadingAtUtc)}` : undefined} data-testid="latest-telemetry">
          {a.health ? <HealthReadingTime health={a.health} /> : <span>{formatRelative(a.lastTelemetryAtUtc)}</span>}
          {a.health ? (
            <div className="subtle">
              <HealthMeasure value={a.health.engineTemperatureC} /> °C · <HealthMeasure value={a.health.oilPressurePsi} /> PSI ·{' '}
              <HealthMeasure value={a.health.vibrationIndex} digits={2} />
            </div>
          ) : null}
        </div>
      ),
    },
    {
      key: 'actions',
      header: 'Actions',
      width: '16.66%',
      render: (a) => (
        <Link to={`/fleet/${a.id}`} className="btn btn--secondary btn--sm" aria-label={`View ${a.registrationNumber}`}>
          View
        </Link>
      ),
    },
  ];

  const healthList = (data?.items || []).map((a) => a.health);
  const rows = (data?.items || []).filter((a) => !condition || fleetCondition(a) === condition);

  return (
    <div className="stack stack--lg">
      <PageHeader
        eyebrow="Operations"
        title="Fleet Health"
        subtitle="Every aircraft with its latest telemetry reading, its forecast-based risk indicator and its threshold status."
        actions={
          can(ACCESS.fleetEdit) ? (
            <Link to="/fleet/new" className="btn btn--primary">
              <Icon name="plus" size={16} /> Add aircraft
            </Link>
          ) : null
        }
      />
      {data ? <FleetConditionCard aircraft={data.items} selected={condition} onSelect={setCondition} /> : null}
      <div className="card">
        <SearchFilterBar
          values={values}
          onChange={setFilters}
          onReset={reset}
          filters={[
            { name: 'search', label: 'Search', type: 'search', placeholder: 'Registration, model…' },
            { name: 'status', label: 'Status', type: 'select', options: options(AIRCRAFT_STATUS) },
          ]}
        />
        {error && !data ? (
          <ErrorState error={error} onRetry={reload} />
        ) : !data ? (
          <SkeletonTable />
        ) : (
          <div aria-busy={loading}>
            <ThresholdDisclaimer healthList={healthList} />
            <DataTable columns={columns} rows={rows} caption="Fleet health" emptyTitle="No aircraft match these filters" fixed />
            <Pagination page={data.page} pageSize={data.pageSize} totalCount={data.totalCount} totalPages={data.totalPages} onPageChange={setPage} onPageSizeChange={setPageSize} />
          </div>
        )}
      </div>
      <FleetHealthGraph title="Telemetry history" id="fleet-telemetry-history" />
    </div>
  );
}
