import { useEffect, useMemo, useState } from 'react';
import { dashboardsApi } from '../../api/dashboards.js';
import { useAsync } from '../../hooks/useAsync.js';
import { useListQuery } from '../../hooks/useListQuery.js';
import { ErrorState } from '../../components/States.jsx';
import { Skeleton } from '../../components/Skeleton.jsx';
import { SENSOR_FORECAST_HORIZON } from '../../utils/enums.js';
import { OverviewCard } from './health/OverviewCard.jsx';
import { ThresholdHealthCard, WorkOrdersCard } from './health/KpiCards.jsx';
import { AircraftList } from './health/AircraftList.jsx';
import { SelectedAircraftPanel } from './health/SelectedAircraftPanel.jsx';
import { aircraftCards, filterAircraft } from './health/healthModel.js';

// The 30-day forecast is the operational projection (the business horizon).
const DEFAULT_HORIZON = 'ThirtyDays';

/**
 * Health Dashboard (Flight Dispatcher). Overview counts come from GET /dashboards/dispatcher; the selected aircraft's
 * forecast comes only from GET /aircraft/{id}/sensor-forecast (recorded telemetry is shown on Fleet Health, never
 * here). Aircraft and horizon are kept in the URL.
 */
export function DispatcherDashboard() {
  const { data, error, reload } = useAsync(() => dashboardsApi.dispatcher(), []);
  const { values, setFilters } = useListQuery();
  const [query, setQuery] = useState('');

  const cards = useMemo(() => aircraftCards(data), [data]);
  const visible = useMemo(() => filterAircraft(cards, query), [cards, query]);
  const selected = visible.find((c) => c.registration === values.aircraft) || null;
  const horizon = SENSOR_FORECAST_HORIZON.includes(values.horizon) ? values.horizon : DEFAULT_HORIZON;

  // Keep a valid selection: the requested aircraft if it is visible, otherwise the first visible one.
  useEffect(() => {
    if (!data || selected) return;
    const next = visible[0]?.registration;
    if (next !== values.aircraft) setFilters({ aircraft: next });
  }, [data, selected, visible, values.aircraft, setFilters]);

  const overview = (children) => (
    <OverviewCard
      aircraftCount={data ? cards.length : '-'}
      thresholds={data?.thresholds || []}
      latestReadingAtUtc={data?.health?.latestReadingAtUtc}
    >
      {children}
    </OverviewCard>
  );

  if (error && !data)
    return (
      <div className="flex flex-col gap-3">
        {overview(null)}
        <div className="rounded-xl border border-line bg-white p-4">
          <ErrorState error={error} onRetry={reload} />
        </div>
      </div>
    );
  if (!data)
    return (
      <div className="flex flex-col gap-3" aria-busy="true">
        {overview(
          <div className="grid gap-3 md:grid-cols-2">
            {[0, 1].map((i) => (
              <Skeleton key={i} height={140} radius={12} />
            ))}
          </div>,
        )}
        <Skeleton height={360} radius={12} />
      </div>
    );

  return (
    // Desktop: the page fits the viewport (no page scroll); the aircraft list and the forecast table scroll inside.
    <div className="flex flex-col gap-3 lg:h-[calc(100dvh-2*var(--space-4))] lg:overflow-hidden" data-testid="health-dashboard">
      {overview(
        <div className="grid gap-3 md:grid-cols-2">
          <ThresholdHealthCard health={data.health} />
          <WorkOrdersCard workOrders={data.workOrders} />
        </div>,
      )}
      {cards.length ? (
        <div className="grid gap-3 lg:min-h-0 lg:flex-1 lg:grid-cols-[14rem_minmax(0,1fr)]">
          <AircraftList
            cards={visible}
            total={cards.length}
            selectedId={selected?.id}
            onSelect={(id) => setFilters({ aircraft: cards.find((c) => c.id === id)?.registration })}
            query={query}
            onQuery={setQuery}
          />
          {selected ? (
            <div className="flex min-h-[480px] min-w-0 flex-col lg:min-h-0">
              <SelectedAircraftPanel
                card={selected}
                thresholds={data.thresholds || []}
                horizonDays={data.prediction?.horizonDays ?? 30}
                horizon={horizon}
                onHorizon={(h) => setFilters({ horizon: h })}
              />
            </div>
          ) : null}
        </div>
      ) : (
        <p className="m-0 rounded-xl border border-dashed border-line bg-white p-6 text-center text-[13px] text-ink-muted" role="status">
          No aircraft are registered yet.
        </p>
      )}
    </div>
  );
}
