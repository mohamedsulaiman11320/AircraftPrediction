import { useId, useRef } from 'react';
import { Icon } from '../../../components/Icon.jsx';
import { STATUS_LABELS } from '../../../components/StatusBadge.jsx';
import { FORECAST_CONDITION_LABELS } from './healthModel.js';

const STATE_TONE = {
  Available: 'bg-ok-soft text-ok',
  UnderMaintenance: 'bg-warn-soft text-warn',
  AwaitingRelease: 'bg-canvas text-ink-muted',
  Grounded: 'bg-crit-soft text-crit',
};

/** Condition badges: operational status, plus Critical / High Critical when the 30-day forecast breaches a limit. */
function ConditionBadges({ card, selected }) {
  const state = card.state;
  return (
    <span className="flex flex-wrap items-center gap-1">
      {state ? (
        <span
          className={['rounded px-1.5 py-0.5 text-[11px] font-semibold', selected ? 'bg-white/20 text-white' : STATE_TONE[state] || 'bg-canvas text-ink-muted'].join(' ')}
          data-testid="state-badge"
        >
          {STATUS_LABELS[state] || state}
        </span>
      ) : null}
      {card.forecastCondition ? (
        <span
          className={['flex items-center gap-0.5 rounded px-1.5 py-0.5 text-[11px] font-semibold', selected ? 'bg-white text-crit' : 'bg-crit text-white'].join(' ')}
          data-testid="forecast-badge"
        >
          <Icon name="alert" size={11} />
          {FORECAST_CONDITION_LABELS[card.forecastCondition] || card.forecastCondition}
        </span>
      ) : null}
    </span>
  );
}

/**
 * Left-hand aircraft selector: search on top of an independently scrolling vertical list. Items are buttons
 * (selection, not navigation); Arrow Up/Down and Home/End move between them.
 */
export function AircraftList({ cards, total, selectedId, onSelect, query, onQuery }) {
  const searchId = useId();
  const refs = useRef([]);
  const focusAt = (i) => refs.current[Math.max(0, Math.min(cards.length - 1, i))]?.focus();

  return (
    <aside aria-label="Aircraft" className="flex min-h-0 flex-col gap-2 rounded-xl border border-line bg-white p-2.5" data-testid="aircraft-pane">
      <label htmlFor={searchId} className="relative block shrink-0">
        <span className="sr-only">Search aircraft</span>
        <Icon name="search" size={15} className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-ink-muted" />
        <input
          id={searchId}
          type="search"
          value={query}
          onChange={(e) => onQuery(e.target.value)}
          placeholder="Search tail number..."
          className="h-8 w-full rounded-lg border border-line bg-white pl-8 pr-2 text-[13px] text-ink placeholder:text-ink-muted focus-visible:border-brand focus-visible:outline-2 focus-visible:outline-brand"
        />
      </label>
      <p className="m-0 shrink-0 px-0.5 text-[11px] text-ink-muted">{cards.length === total ? `${total} aircraft` : `${cards.length} of ${total} aircraft`}</p>
      {cards.length ? (
        <ul className="m-0 flex max-h-[50vh] min-h-0 flex-1 list-none flex-col gap-1.5 overflow-y-auto p-0.5 lg:max-h-none" data-testid="aircraft-list" aria-label="Select an aircraft">
          {cards.map((c, i) => {
            const selected = c.id === selectedId;
            return (
              <li key={c.id}>
                <button
                  ref={(el) => {
                    refs.current[i] = el;
                  }}
                  type="button"
                  aria-pressed={selected}
                  data-testid={`aircraft-card-${c.registration}`}
                  onClick={() => onSelect(c.id)}
                  onKeyDown={(e) => {
                    const step = { ArrowDown: i + 1, ArrowUp: i - 1, Home: 0, End: cards.length - 1 }[e.key];
                    if (step !== undefined) {
                      e.preventDefault();
                      focusAt(step);
                    }
                  }}
                  className={[
                    'flex w-full flex-col gap-1 rounded-lg border px-2.5 py-2 text-left transition-colors',
                    'focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-brand',
                    selected ? 'border-brand bg-brand text-white' : 'border-line bg-white text-ink hover:border-brand',
                  ].join(' ')}
                >
                  <span className="text-[14px] font-semibold tracking-wide">{c.registration}</span>
                  <ConditionBadges card={c} selected={selected} />
                </button>
              </li>
            );
          })}
        </ul>
      ) : (
        <p className="m-0 rounded-lg border border-dashed border-line px-3 py-4 text-[13px] text-ink-muted" role="status">
          No aircraft match "{query}".
        </p>
      )}
    </aside>
  );
}
