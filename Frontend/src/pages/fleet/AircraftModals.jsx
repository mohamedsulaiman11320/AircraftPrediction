import { useEffect, useState } from 'react';
import { aircraftApi, maintenanceLogsApi } from '../../api/aircraft.js';
import { ConfirmDialog } from '../../components/Modal.jsx';
import { Checkbox, SelectField, TextArea, TextField } from '../../components/FormFields.jsx';
import { Alert } from '../../components/Misc.jsx';
import { AIRCRAFT_STATUS, FLAG_SEVERITY, MAINTENANCE_EVENT, options } from '../../utils/enums.js';
import { fromInputDateTime, toInputDateTime } from '../../utils/format.js';

export function ChangeStatusModal({ open, aircraft, onClose, onDone }) {
  const [status, setStatus] = useState('');
  const [reason, setReason] = useState('');
  const [errors, setErrors] = useState({});
  useEffect(() => {
    if (open) {
      setStatus('');
      setReason('');
      setErrors({});
    }
  }, [open]);

  const confirm = async () => {
    const v = { status: status ? null : 'Choose a status.', reason: reason.trim() ? null : 'A reason is required.' };
    setErrors(v);
    if (v.status || v.reason) return;
    const updated = await aircraftApi.changeStatus(aircraft.id, status, reason.trim());
    onDone(updated, `Status changed to ${status}.`);
  };

  return (
    <ConfirmDialog open={open} title={`Change status · ${aircraft?.registrationNumber || ''}`} confirmLabel="Change status" onConfirm={confirm} onClose={onClose}>
      <Alert tone="info">
        Setting <strong>Available</strong> is refused while a Grounding flag is active or a Critical work order is open. Model output never
        changes aircraft status.
      </Alert>
      <SelectField
        label="New status"
        value={status}
        onChange={(e) => setStatus(e.target.value)}
        options={options(AIRCRAFT_STATUS.filter((s) => s !== aircraft?.operationalStatus))}
        placeholder="Select…"
        error={errors.status}
        required
      />
      <TextArea label="Reason" value={reason} onChange={(e) => setReason(e.target.value)} error={errors.reason} maxLength={1000} required />
    </ConfirmDialog>
  );
}

export function RaiseFlagModal({ open, aircraft, onClose, onDone, anomalyId, predictionId, workOrderId }) {
  const [severity, setSeverity] = useState('Advisory');
  const [reason, setReason] = useState('');
  const [error, setError] = useState(null);
  useEffect(() => {
    if (open) {
      setSeverity('Advisory');
      setReason('');
      setError(null);
    }
  }, [open]);
  const confirm = async () => {
    if (!reason.trim()) {
      setError('A reason is required.');
      return;
    }
    const body = { severity, reason: reason.trim() };
    if (anomalyId) body.anomalyId = anomalyId;
    if (predictionId) body.predictionId = predictionId;
    if (workOrderId) body.workOrderId = workOrderId;
    const flag = await aircraftApi.raiseFlag(aircraft.id, body);
    onDone(flag, `${severity} inspection flag raised.`);
  };
  return (
    <ConfirmDialog
      open={open}
      title={`Raise inspection flag · ${aircraft?.registrationNumber || ''}`}
      confirmLabel="Raise flag"
      tone={severity === 'Grounding' ? 'danger' : 'primary'}
      onConfirm={confirm}
      onClose={onClose}
    >
      <SelectField label="Severity" value={severity} onChange={(e) => setSeverity(e.target.value)} options={options(FLAG_SEVERITY)} required />
      {severity === 'Grounding' ? (
        <Alert tone="warning" title="Grounding flag">
          A Grounding flag sets the aircraft status to Grounded until the flag is cleared.
        </Alert>
      ) : null}
      <TextArea label="Reason" value={reason} onChange={(e) => setReason(e.target.value)} error={error} maxLength={1000} required />
    </ConfirmDialog>
  );
}

const MAINTENANCE_FIELDS = ['eventType', 'occurredAtUtc', 'description', 'componentId', 'actualFailureConfirmed', 'isVerified'];

/** Client-side validation for a maintenance event. Returns { field: message }. */
export function validateMaintenanceEvent(f, now = Date.now()) {
  const e = {};
  if (!MAINTENANCE_EVENT.includes(f.eventType)) e.eventType = 'Choose an event type.';
  const occurred = fromInputDateTime(f.occurredAt);
  if (!f.occurredAt || !occurred) e.occurredAtUtc = 'Enter when the event occurred.';
  else if (Date.parse(occurred) > now + 60000) e.occurredAtUtc = 'The event time cannot be in the future.';
  const d = (f.description || '').trim();
  if (!d) e.description = 'A description is required.';
  else if (d.length > 4000) e.description = 'The description must be at most 4000 characters.';
  if (!['', 'true', 'false'].includes(f.actualFailureConfirmed)) e.actualFailureConfirmed = 'Choose Yes, No or Unknown.';
  return e;
}

export function maintenanceEventBody(aircraftId, f) {
  const body = {
    aircraftId,
    eventType: f.eventType,
    occurredAtUtc: fromInputDateTime(f.occurredAt),
    description: f.description.trim(),
    actualFailureConfirmed: f.actualFailureConfirmed === '' ? null : f.actualFailureConfirmed === 'true',
    isVerified: !!f.isVerified,
  };
  if (f.componentId.trim()) body.componentId = f.componentId.trim();
  return body;
}

const emptyEvent = () => ({
  eventType: 'Inspection',
  occurredAt: toInputDateTime(new Date().toISOString()),
  description: '',
  componentId: '',
  actualFailureConfirmed: '',
  isVerified: false,
});

export function RecordMaintenanceModal({ open, aircraft, onClose, onDone }) {
  const [f, setF] = useState(emptyEvent);
  const [errors, setErrors] = useState({});
  useEffect(() => {
    if (open) {
      setF(emptyEvent());
      setErrors({});
    }
  }, [open]);
  const set = (k) => (e) => setF((s) => ({ ...s, [k]: e.target.type === 'checkbox' ? e.target.checked : e.target.value }));

  const confirm = async () => {
    const v = validateMaintenanceEvent(f);
    setErrors(v);
    if (Object.keys(v).length) return;
    try {
      const log = await maintenanceLogsApi.create(maintenanceEventBody(aircraft.id, f));
      onDone(log, 'Maintenance event recorded.');
    } catch (err) {
      if (err?.errors) {
        const mapped = {};
        MAINTENANCE_FIELDS.forEach((k) => {
          const m = err.fieldError?.(k);
          if (m) mapped[k] = m;
        });
        setErrors(mapped);
      }
      throw err;
    }
  };

  return (
    <ConfirmDialog
      open={open}
      title={`Record maintenance event · ${aircraft?.registrationNumber || ''}`}
      confirmLabel="Record event"
      onConfirm={confirm}
      onClose={onClose}
      size="lg"
    >
      <Alert tone="info" title="Why this matters">
        Verified <strong>Unscheduled</strong> events are the label source for future failure-prediction training. Record what actually
        happened, and mark the event verified only when it has been confirmed against maintenance records.
      </Alert>
      <div className="form-grid">
        <SelectField label="Event type" value={f.eventType} onChange={set('eventType')} options={options(MAINTENANCE_EVENT)} error={errors.eventType} required />
        <TextField
          label="Occurred (UTC)"
          type="datetime-local"
          value={f.occurredAt}
          onChange={set('occurredAt')}
          max={toInputDateTime(new Date().toISOString())}
          error={errors.occurredAtUtc}
          required
        />
        <TextArea
          label="Description"
          value={f.description}
          onChange={set('description')}
          error={errors.description}
          maxLength={4000}
          hint={`${f.description.length}/4000`}
          required
          className="field--full"
        />
        <TextField label="Component ID" value={f.componentId} onChange={set('componentId')} error={errors.componentId} hint="Optional" />
        <SelectField
          label="Actual failure confirmed?"
          value={f.actualFailureConfirmed}
          onChange={set('actualFailureConfirmed')}
          options={[
            { value: '', label: 'Unknown' },
            { value: 'true', label: 'Yes — a failure was confirmed' },
            { value: 'false', label: 'No — no failure found' },
          ]}
          error={errors.actualFailureConfirmed}
        />
      </div>
      <Checkbox label="Verified against maintenance records" checked={f.isVerified} onChange={set('isVerified')} />
      {errors.isVerified ? <p className="field__error">{errors.isVerified}</p> : null}
    </ConfirmDialog>
  );
}

export function ClearFlagModal({ open, flag, onClose, onDone }) {
  const [notes, setNotes] = useState('');
  const [error, setError] = useState(null);
  useEffect(() => {
    if (open) {
      setNotes('');
      setError(null);
    }
  }, [open]);
  const confirm = async () => {
    if (!notes.trim()) {
      setError('Clearance notes are required.');
      return;
    }
    const cleared = await aircraftApi.clearFlag(flag.id, notes.trim());
    onDone(cleared, 'Inspection flag cleared.');
  };
  return (
    <ConfirmDialog
      open={open}
      title="Clear inspection flag"
      message={flag ? `${flag.severity}: ${flag.reason}` : ''}
      confirmLabel="Clear flag"
      onConfirm={confirm}
      onClose={onClose}
    >
      <TextArea label="Clearance notes" value={notes} onChange={(e) => setNotes(e.target.value)} error={error} maxLength={2000} required />
    </ConfirmDialog>
  );
}
