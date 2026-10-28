import { useEffect } from 'react';
import { telemetryApi } from '../../api/telemetry.js';
import { useAsync } from '../../hooks/useAsync.js';
import { useListQuery } from '../../hooks/useListQuery.js';
import { PageHeader, Card } from '../../components/PageHeader.jsx';
import { DataTable } from '../../components/DataTable.jsx';
import { Pagination } from '../../components/Pagination.jsx';
import { StatusBadge } from '../../components/StatusBadge.jsx';
import { EmptyState, ErrorState } from '../../components/States.jsx';
import { Skeleton, SkeletonTable } from '../../components/Skeleton.jsx';
import { SelectField, TextField } from '../../components/FormFields.jsx';
import { Alert } from '../../components/Misc.jsx';
import { AircraftSelect, useAircraftOptions } from '../shared/AircraftSelect.jsx';
import { DataTelemetryTabs } from '../shared/DataEngineerTabs.jsx';
import { BUCKET_OPTIONS, rangeErrorFor } from '../shared/dateRange.js';
import { TelemetryCharts } from './TelemetryCharts.jsx';
import { daysAgo, formatDateTime, formatNumber, fromInputDate, toInputDate } from '../../utils/format.js';
import { QUALITY_STATUS, options } from '../../utils/enums.js';

export const SOURCE_FLAG_HELP = 'Threshold-based flag from the source system — not a confirmed failure';

export function TelemetryPage() {
  /**
   * Default window: the last 7 days, applied on first load. Everything here is UTC — toInputDate()
   * formats the ISO (UTC) calendar date and fromInputDate() reads it back as 00:00:00Z / 23:59:59Z,
   * which is exactly what GET /telemetry and GET /telemetry/series expect for fromUtc/toUtc.
   */
  const { values, page, pageSize, setFilters, setPage, setPageSize } = useListQuery({
    from: toInputDate(daysAgo(7)),
    to: toInputDate(new Date()),
    bucketMinutes: '60',
    pageSize: '50',
  }, { maxPageSize: 1000 });

  /**
   * Default aircraft: the first one the shared lookup returns. No tail number is ever hard-coded,
   * nothing is selected while the list is still loading, and a selection that is no longer in the
   * list falls back to the first available aircraft (or to the empty state when there is none).
   */
  const aircraft = useAircraftOptions();
  const aircraftList = aircraft.data;
  const requestedAircraftId = values.aircraftId || '';
  const known = !!aircraftList && aircraftList.some((o) => o.value === requestedAircraftId);
  const aircraftId = known ? requestedAircraftId : aircraftList?.length ? aircraftList[0].value : '';
  useEffect(() => {
    if (!aircraftList?.length || known) return;
    setFilters({ aircraftId: aircraftList[0].value });
  }, [aircraftList, known]); // eslint-disable-line react-hooks/exhaustive-deps
  const aircraftLoading = !aircraftList && !aircraft.error;
  const noAircraft = !!aircraftList && aircraftList.length === 0;

  const fromUtc = fromInputDate(values.from);
  const toUtc = fromInputDate(values.to, true);
  const rangeError = rangeErrorFor(fromUtc, toUtc);
  const ready = !!aircraftId && !rangeError;

  const records = useAsync(
    () => telemetryApi.records({ aircraftId, fromUtc, toUtc, qualityStatus: values.qualityStatus || undefined, page, pageSize }),
    [aircraftId, fromUtc, toUtc, values.qualityStatus, page, pageSize],
    { enabled: ready },
  );

  const columns = [
    { key: 'recordedAtUtc', header: 'Recorded (UTC)', sortable: true, render: (r) => <span className="nowrap">{formatDateTime(r.recordedAtUtc)}</span> },
    {
      key: 'temperatureC',
      header: 'Temp °C',
      numeric: true,
      sortable: true,
      render: (r) => (
        <span title={r.temperatureValue !== null && r.temperatureValue !== undefined ? `Source: ${r.temperatureValue} ${r.temperatureUnit}` : undefined}>{formatNumber(r.temperatureC, 2)}</span>
      ),
    },
    {
      key: 'pressureKpa',
      header: 'Pressure kPa',
      numeric: true,
      sortable: true,
      render: (r) => <span title={r.pressureValue !== null && r.pressureValue !== undefined ? `Source: ${r.pressureValue} ${r.pressureUnit}` : undefined}>{formatNumber(r.pressureKpa, 2)}</span>,
    },
    { key: 'qualityStatus', header: 'Quality', sortable: true, render: (r) => <StatusBadge value={r.qualityStatus} /> },
    { key: 'qualityFlags', header: 'Flags', render: (r) => (r.qualityFlags?.length ? <span className="mono">{r.qualityFlags.join(', ')}</span> : '—') },
    {
      key: 'vibrationIndex',
      header: <span title="Unitless vibration index from the source system">Vibration</span>,
      numeric: true,
      sortable: true,
      render: (r) => (r.vibrationIndex === null || r.vibrationIndex === undefined ? '—' : formatNumber(r.vibrationIndex, 3)),
    },
    {
      key: 'sourceStatusFlag',
      header: (
        <span title={SOURCE_FLAG_HELP}>
          Source flag <span aria-hidden="true">ⓘ</span>
          <span className="visually-hidden"> ({SOURCE_FLAG_HELP})</span>
        </span>
      ),
      render: (r) => (r.sourceStatusFlag === null || r.sourceStatusFlag === undefined || r.sourceStatusFlag === '' ? '—' : <span className="mono" title={SOURCE_FLAG_HELP}>{String(r.sourceStatusFlag)}</span>),
    },
    { key: 'sourceRecordId', header: 'Source record', render: (r) => (r.sourceRecordId ? <span className="mono">{r.sourceRecordId}</span> : '—') },
  ];

  return (
    <>
      <DataTelemetryTabs active="telemetry" />
      <PageHeader eyebrow="Data" title="Telemetry explorer" subtitle="Normalised temperature (°C) and pressure (kPa) readings per aircraft." />
      <div className="card" style={{ marginBottom: 24 }}>
        <div className="filter-bar" role="search">
          <AircraftSelect value={aircraftId} onChange={(v) => setFilters({ aircraftId: v })} />
          <TextField label="From (UTC)" type="date" value={values.from} onChange={(e) => setFilters({ from: e.target.value })} />
          <TextField label="To (UTC)" type="date" value={values.to} onChange={(e) => setFilters({ to: e.target.value })} />
          <SelectField
            label="Bucket size"
            value={values.bucketMinutes}
            onChange={(e) => setFilters({ bucketMinutes: e.target.value })}
            options={BUCKET_OPTIONS}
          />
        </div>
        <div className="card__body">
          {rangeError ? <Alert tone="danger">{rangeError}</Alert> : null}
          {aircraft.error && !aircraftList ? (
            <ErrorState error={aircraft.error} onRetry={aircraft.reload} title="Could not load aircraft" />
          ) : aircraftLoading ? (
            <div className="grid grid--2" role="status" aria-label="Loading aircraft">
              <Skeleton height={240} />
              <Skeleton height={240} />
            </div>
          ) : noAircraft ? (
            <EmptyState icon="activity" title="No aircraft available" message="No aircraft were returned, so there is no telemetry to show." />
          ) : ready ? (
            <TelemetryCharts aircraftId={aircraftId} fromUtc={fromUtc} toUtc={toUtc} bucketMinutes={Number(values.bucketMinutes) || 60} />
          ) : null}
        </div>
      </div>

      {ready ? (
        <Card
          title="Raw records"
          flush
          actions={
            <div style={{ minWidth: 160 }}>
              <SelectField
                label="Quality"
                value={values.qualityStatus || ''}
                onChange={(e) => setFilters({ qualityStatus: e.target.value })}
                options={options(QUALITY_STATUS)}
                placeholder="All"
                className="field--inline"
              />
            </div>
          }
        >
          {records.error && !records.data ? (
            <ErrorState error={records.error} onRetry={records.reload} />
          ) : !records.data ? (
            <SkeletonTable />
          ) : (
            <div aria-busy={records.loading}>
              <p className="help-text" style={{ padding: '12px 16px 0', margin: 0 }}>
                <strong>Source flag:</strong> {SOURCE_FLAG_HELP}. Vibration is a unitless index as supplied by the source.
              </p>
              <DataTable columns={columns} rows={records.data.items} caption="Telemetry records" emptyTitle="No records in this range" />
              <Pagination
                page={records.data.page}
                pageSize={records.data.pageSize}
                totalCount={records.data.totalCount}
                totalPages={records.data.totalPages}
                onPageChange={setPage}
                onPageSizeChange={setPageSize}
                pageSizes={[25, 50, 100, 250, 500]}
              />
            </div>
          )}
        </Card>
      ) : null}
    </>
  );
}
