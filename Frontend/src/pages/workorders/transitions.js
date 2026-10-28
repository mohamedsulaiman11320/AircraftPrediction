/**
 * Normalises the server's `allowedTransitions` strings to action keys. The contract lists the
 * values only as strings; we accept action names ("assign", "start-inspection", "StartInspection")
 * as well as target-status names ("Assigned", "InInspection") so the UI follows whatever the server sends.
 */
const ALIASES = {
  assign: 'assign',
  reassign: 'assign',
  assigned: 'assign',
  startinspection: 'startInspection',
  start: 'startInspection',
  ininspection: 'startInspection',
  inspection: 'recordInspection',
  recordinspection: 'recordInspection',
  inspectionfindings: 'recordInspection',
  findings: 'recordInspection',
  resolve: 'resolve',
  resolved: 'resolve',
  reopen: 'reopen',
  reopened: 'reopen',
  close: 'close',
  closed: 'close',
  cancel: 'cancel',
  cancelled: 'cancel',
  canceled: 'cancel',
  update: 'edit',
  edit: 'edit',
};

export const ACTION_ORDER = ['assign', 'startInspection', 'recordInspection', 'resolve', 'reopen', 'close', 'cancel'];

export const ACTION_LABELS = {
  assign: 'Assign technician',
  startInspection: 'Start inspection',
  recordInspection: 'Record inspection findings',
  resolve: 'Resolve',
  reopen: 'Send back to technician',
  close: 'Complete',
  cancel: 'Cancel work order',
};

/** Set of action keys allowed for a work order. */
export function allowedActions(workOrder) {
  const out = new Set();
  (workOrder?.allowedTransitions || []).forEach((t) => {
    const key = String(t).toLowerCase().replace(/[^a-z]/g, '');
    let action = ALIASES[key];
    // "InInspection" as a target from Resolved means reopen, not start.
    if (action === 'startInspection' && key === 'ininspection' && workOrder.status === 'Resolved') action = 'reopen';
    if (action) out.add(action);
  });
  return out;
}
