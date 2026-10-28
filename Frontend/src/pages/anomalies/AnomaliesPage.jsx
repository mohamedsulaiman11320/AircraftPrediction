import { anomaliesApi } from '../../api/anomalies.js';
import { useAsync } from '../../hooks/useAsync.js';
import { useListQuery } from '../../hooks/useListQuery.js';
import { PageHeader } from '../../components/PageHeader.jsx';
import { KpiCard } from '../../components/KpiCard.jsx';
import { SearchFilterBar } from '../../components/SearchFilterBar.jsx';
import { DataTable } from '../../components/DataTable.jsx';
import { Pagination } from '../../components/Pagination.jsx';
import { ErrorState } from '../../components/States.jsx';
import { SkeletonTable } from '../../components/Skeleton.jsx';
import { anomalyColumns } from '../shared/columns.jsx';
import { useAircraftOptions } from '../shared/AircraftSelect.jsx';
import { OperationsTabs, useOperationsTabsShown } from '../shared/OperationsTabs.jsx';
import { ANOMALY_SEVERITY, ANOMALY_STATUS, options } from '../../utils/enums.js';
import { fromInputDate } from '../../utils/format.js';

export function AnomaliesPage() {
  const tabsShown = useOperationsTabsShown();
  const { values, page, pageSize, setFilters, setPage, setPageSize, reset } = useListQuery();
  const aircraft = useAircraftOptions();
  const summary = useAsync(() => anomaliesApi.summary(30), []);
  const fromUtc = fromInputDate(values.from);
  const toUtc = fromInputDate(values.to, true);
  const { data, error, loading, reload } = useAsync(
    () =>
      anomaliesApi.list({
        aircraftId: values.aircraftId || undefined,
        status: values.status || undefined,
        severity: values.severity || undefined,
        fromUtc,
        toUtc,
        page,
        pageSize,
      }),
    [values.aircraftId, values.status, values.severity, fromUtc, toUtc, page, pageSize],
  );
  const s = summary.data;
  return (
    <>
      <OperationsTabs active="anomalies" />
      <PageHeader
        profile={!tabsShown}
        eyebrow="Operations"
        title="Anomalies"
        subtitle="Telemetry readings flagged as unusual by the active anomaly-detection model. Scores rank unusualness; they are not probabilities."
      />
      {s ? (
        <div className="grid grid--kpi">
          <KpiCard label="Detected (30 days)" value={s.total} tone="accent" spark={(s.byDay || []).map((d) => d.count)} />
          <KpiCard label="Open" value={s.open} tone="warning" />
          <KpiCard label="High severity" value={s.bySeverity?.High ?? 0} tone="danger" />
          <KpiCard label="Medium / Low" value={`${s.bySeverity?.Medium ?? 0} / ${s.bySeverity?.Low ?? 0}`} tone="neutral" />
        </div>
      ) : null}
      <div className="card">
        <SearchFilterBar
          values={values}
          onChange={setFilters}
          onReset={reset}
          filters={[
            { name: 'aircraftId', label: 'Aircraft', type: 'select', options: aircraft.data || [] },
            { name: 'severity', label: 'Severity', type: 'select', options: options(ANOMALY_SEVERITY) },
            { name: 'status', label: 'Investigation status', type: 'select', options: options(ANOMALY_STATUS) },
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
            <DataTable columns={anomalyColumns()} rows={data.items} caption="Anomalies" emptyTitle="No anomalies match these filters" />
            <Pagination page={data.page} pageSize={data.pageSize} totalCount={data.totalCount} totalPages={data.totalPages} onPageChange={setPage} onPageSizeChange={setPageSize} />
          </div>
        )}
      </div>
    </>
  );
}
