import { WO_PRIORITY } from '../../utils/enums.js';

/**
 * Work order of a technician's queue: what still needs them first (ASSIGNED, then IN_PROGRESS),
 * then work waiting on the dispatcher (REPORTED), then finished (COMPLETED) and anything else.
 */
export const MY_TICKET_STATUS_ORDER = ['Assigned', 'InInspection', 'Resolved', 'Closed'];

function statusRank(status) {
  const i = MY_TICKET_STATUS_ORDER.indexOf(status);
  return i === -1 ? MY_TICKET_STATUS_ORDER.length : i;
}

/** Higher priority first (WO_PRIORITY is listed lowest to highest; unknown values sort last). */
function priorityRank(priority) {
  const i = WO_PRIORITY.indexOf(priority);
  return i === -1 ? WO_PRIORITY.length : WO_PRIORITY.length - 1 - i;
}

function createdMs(w) {
  const t = Date.parse(w.createdAtUtc);
  return Number.isNaN(t) ? Number.MAX_SAFE_INTEGER : t;
}

/** Status group, then priority (highest first), then oldest first. Returns a new array. */
export function sortMyTickets(tickets) {
  return [...(tickets || [])].sort(
    (a, b) => statusRank(a.status) - statusRank(b.status) || priorityRank(a.priority) - priorityRank(b.priority) || createdMs(a) - createdMs(b),
  );
}

/** Number of tickets per status, from the tickets the API returned. */
export function countByStatus(tickets) {
  const out = {};
  (tickets || []).forEach((w) => {
    out[w.status] = (out[w.status] || 0) + 1;
  });
  return out;
}
