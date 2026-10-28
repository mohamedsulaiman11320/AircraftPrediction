import { useEffect, useState } from 'react';
import { workOrdersApi } from '../../api/workOrders.js';
import { ApiError } from '../../api/client.js';
import { useAsync } from '../../hooks/useAsync.js';
import { ConfirmDialog } from '../../components/Modal.jsx';
import { SelectField, TextArea, TextField } from '../../components/FormFields.jsx';
import { ACTION_LABELS, ACTION_ORDER, allowedActions } from './transitions.js';
import { WO_PRIORITY, options } from '../../utils/enums.js';
import { fromInputDateTime, toInputDateTime } from '../../utils/format.js';

/** Buttons for the workflow transitions the server says are allowed. */
export function WorkOrderActionButtons({ workOrder, canAct, canEdit = false, onAction }) {
  if (!canAct) return null;
  const allowed = allowedActions(workOrder);
  // PATCH (edit details) is dispatcher-only; workflow buttons follow the role-filtered allowedTransitions.
  const editable = canEdit && !['Closed', 'Cancelled'].includes(workOrder.status);
  const buttons = ACTION_ORDER.filter((a) => allowed.has(a));
  if (buttons.length === 0 && !editable) return null;
  return (
    <div className="row" role="group" aria-label="Work order actions">
      {buttons.map((a) => (
        <button
          key={a}
          type="button"
          className={`btn ${a === 'cancel' ? 'btn--danger' : a === buttons[0] ? 'btn--primary' : 'btn--secondary'}`}
          onClick={() => onAction(a)}
        >
          {ACTION_LABELS[a]}
        </button>
      ))}
      {editable ? (
        <button type="button" className="btn btn--ghost" onClick={() => onAction('edit')}>
          Edit details
        </button>
      ) : null}
    </div>
  );
}

/** User ids are numbers (RBAC v2); <select> values are strings. */
export function toUserId(v) {
  const n = Number(v);
  return Number.isSafeInteger(n) ? n : v;
}

function friendlyConflict(err) {
  if (err instanceof ApiError && err.status === 409) {
    return new ApiError({
      ...err,
      status: 409,
      code: err.code,
      title: 'This work order was changed by someone else',
      detail: 'Close this dialog to load the latest version, then try again.',
    });
  }
  return err;
}

/** Minimum length of the technician's resolution notes (enforced server-side too). */
export const MIN_NOTES = 10;
const FORM_FIELDS = ['technicianId', 'inspectionFindings', 'actionCode', 'correctiveAction', 'actualFailureConfirmed', 'resolutionNotes', 'reason', 'title', 'description'];

/** Dialog for a single workflow action. Calls onDone(updatedWorkOrder, message) on success. */
export function WorkOrderActionDialog({ action, workOrder, onClose, onDone, onConflict }) {
  const open = !!action;
  const [f, setF] = useState({});
  const [errors, setErrors] = useState({});
  const techs = useAsync(() => workOrdersApi.technicians(), [], { enabled: action === 'assign' });
  const codes = useAsync(() => workOrdersApi.actionCodes(), [], { enabled: action === 'resolve' });

  useEffect(() => {
    if (!open) return;
    setErrors({});
    setF(
      action === 'edit'
        ? {
            title: workOrder.title,
            description: workOrder.description,
            priority: workOrder.priority,
            dueAtUtc: toInputDateTime(workOrder.dueAtUtc),
          }
        : action === 'assign'
          ? { technicianId: workOrder.assignedTechnicianId != null ? String(workOrder.assignedTechnicianId) : '' }
          : action === 'recordInspection'
            ? { inspectionFindings: workOrder.inspectionFindings || '' }
            : { actualFailureConfirmed: '', actionCode: '' },
    );
  }, [open, action, workOrder]);

  if (!open) return null;
  const set = (k) => (e) => setF((s) => ({ ...s, [k]: e.target.value }));
  const req = (keys, extra) => {
    const e = {};
    keys.forEach(([k, msg]) => {
      if (!String(f[k] ?? '').trim()) e[k] = msg;
    });
    extra?.(e);
    setErrors(e);
    return Object.keys(e).length === 0;
  };

  const id = workOrder.id;
  const run = async () => {
    let result;
    try {
      switch (action) {
        case 'assign':
          if (!req([['technicianId', 'Choose a technician.']])) return;
          result = await workOrdersApi.assign(id, toUserId(f.technicianId));
          break;
        case 'startInspection':
          result = await workOrdersApi.startInspection(id);
          break;
        case 'recordInspection':
          if (!req([['inspectionFindings', 'Inspection findings are required.']])) return;
          result = await workOrdersApi.recordInspection(id, f.inspectionFindings.trim());
          break;
        case 'resolve':
          if (
            !req(
              [
                ['actionCode', 'Choose the maintenance action code.'],
                ['correctiveAction', 'Describe the work performed.'],
                ['actualFailureConfirmed', 'State whether an actual failure was confirmed.'],
                ['resolutionNotes', 'Resolution notes are required.'],
              ],
              (e) => {
                const notes = String(f.resolutionNotes ?? '').trim();
                if (notes && notes.length < MIN_NOTES) e.resolutionNotes = `Resolution notes must be at least ${MIN_NOTES} characters.`;
              },
            )
          )
            return;
          result = await workOrdersApi.resolve(id, {
            actionCode: f.actionCode,
            correctiveAction: f.correctiveAction.trim(),
            actualFailureConfirmed: f.actualFailureConfirmed === 'true',
            resolutionNotes: f.resolutionNotes.trim(),
          });
          break;
        case 'reopen':
          if (!req([['reason', 'A reason is required.']])) return;
          result = await workOrdersApi.reopen(id, f.reason.trim());
          break;
        case 'close':
          result = await workOrdersApi.close(id, f.resolutionNotes?.trim() || undefined);
          break;
        case 'cancel':
          if (!req([['reason', 'A reason is required.']])) return;
          result = await workOrdersApi.cancel(id, f.reason.trim());
          break;
        case 'edit': {
          if (
            !req([
              ['title', 'Title is required.'],
              ['description', 'Description is required.'],
            ])
          )
            return;
          const body = { concurrencyStamp: workOrder.concurrencyStamp };
          if (f.title.trim() !== workOrder.title) body.title = f.title.trim();
          if (f.description.trim() !== workOrder.description) body.description = f.description.trim();
          if (f.priority !== workOrder.priority) body.priority = f.priority;
          const due = fromInputDateTime(f.dueAtUtc);
          if ((due || null) !== (workOrder.dueAtUtc ? new Date(workOrder.dueAtUtc).toISOString() : null) && due) body.dueAtUtc = due;
          result = await workOrdersApi.update(id, body);
          break;
        }
        default:
          return;
      }
    } catch (err) {
      if (err?.status === 409) onConflict?.();
      // ProblemDetails field errors go next to the matching inputs (the summary still shows above).
      if (err instanceof ApiError && err.errors) {
        const fieldErrors = {};
        FORM_FIELDS.forEach((k) => {
          const msg = err.fieldError(k);
          if (msg) fieldErrors[k] = msg;
        });
        if (Object.keys(fieldErrors).length) setErrors(fieldErrors);
      }
      throw friendlyConflict(err);
    }
    onDone(result, action === 'edit' ? 'Work order updated.' : `${ACTION_LABELS[action]} completed.`);
  };

  const title = action === 'edit' ? `Edit ${workOrder.workOrderNumber}` : `${ACTION_LABELS[action]} · ${workOrder.workOrderNumber}`;
  const messages = {
    startInspection: 'The work order moves to In inspection.',
    reopen: 'The ticket goes back to the technician (ASSIGNED). Their submitted report stays in the report history.',
    close: 'Completing accepts the technician’s report: it writes the maintenance log outcome and records the outcome on any linked prediction or anomaly.',
    cancel: 'Only open or assigned work orders can be cancelled.',
  };

  return (
    <ConfirmDialog
      open={open}
      title={title}
      message={messages[action]}
      confirmLabel={action === 'edit' ? 'Save changes' : ACTION_LABELS[action]}
      tone={action === 'cancel' ? 'danger' : 'primary'}
      onConfirm={run}
      onClose={onClose}
    >
      {action === 'assign' ? (
        <SelectField
          label="Technician"
          value={f.technicianId || ''}
          onChange={set('technicianId')}
          options={(techs.data || []).map((t) => ({ value: t.id, label: `${t.displayName} (${t.username})` }))}
          placeholder={techs.loading ? 'Loading technicians…' : 'Select…'}
          error={errors.technicianId}
          required
        />
      ) : null}
      {action === 'recordInspection' ? (
        <TextArea label="Inspection findings" value={f.inspectionFindings || ''} onChange={set('inspectionFindings')} error={errors.inspectionFindings} maxLength={8000} required />
      ) : null}
      {action === 'resolve' ? (
        <>
          <SelectField
            label="Action code"
            value={f.actionCode || ''}
            onChange={set('actionCode')}
            options={(codes.data || []).map((c) => ({ value: c.code, label: `${c.code} — ${c.description}` }))}
            placeholder={codes.loading ? 'Loading action codes…' : codes.error ? 'Action codes could not be loaded' : 'Select…'}
            error={errors.actionCode || (codes.error ? 'The action code list could not be loaded. Close and try again.' : undefined)}
            required
          />
          <TextArea label="Work performed" value={f.correctiveAction || ''} onChange={set('correctiveAction')} error={errors.correctiveAction} maxLength={8000} required />
          <fieldset className="field" style={{ border: 0, padding: 0 }}>
            <legend className="field__label">
              Actual failure confirmed?<span className="field__required" aria-hidden="true">*</span>
            </legend>
            <div className="row">
              <label className="row">
                <input type="radio" name="afc" value="true" checked={f.actualFailureConfirmed === 'true'} onChange={set('actualFailureConfirmed')} /> Yes — a component failure was confirmed
              </label>
              <label className="row">
                <input type="radio" name="afc" value="false" checked={f.actualFailureConfirmed === 'false'} onChange={set('actualFailureConfirmed')} /> No
              </label>
            </div>
            {errors.actualFailureConfirmed ? <span className="field__error">{errors.actualFailureConfirmed}</span> : null}
          </fieldset>
          <TextArea
            label="Resolution notes"
            value={f.resolutionNotes || ''}
            onChange={set('resolutionNotes')}
            error={errors.resolutionNotes}
            hint={`At least ${MIN_NOTES} characters.`}
            maxLength={8000}
            required
          />
        </>
      ) : null}
      {action === 'reopen' || action === 'cancel' ? (
        <TextArea
          label={action === 'reopen' ? 'Reason for sending back' : 'Reason'}
          hint={action === 'reopen' ? 'Tell the technician what needs to be redone or clarified.' : undefined}
          value={f.reason || ''}
          onChange={set('reason')}
          error={errors.reason}
          maxLength={2000}
          required
        />
      ) : null}
      {action === 'close' ? (
        <TextArea
          label="Dispatcher review notes"
          hint="Optional. Stored as your review, separately from the technician’s report."
          value={f.resolutionNotes || ''}
          onChange={set('resolutionNotes')}
          error={errors.resolutionNotes}
          maxLength={8000}
        />
      ) : null}
      {action === 'edit' ? (
        <>
          <TextField label="Title" value={f.title || ''} onChange={set('title')} error={errors.title} maxLength={200} required />
          <TextArea label="Description" value={f.description || ''} onChange={set('description')} error={errors.description} maxLength={8000} required />
          <div className="form-grid">
            <SelectField label="Priority" value={f.priority || ''} onChange={set('priority')} options={options(WO_PRIORITY)} />
            <TextField label="Due (UTC)" type="datetime-local" value={f.dueAtUtc || ''} onChange={set('dueAtUtc')} />
          </div>
        </>
      ) : null}
    </ConfirmDialog>
  );
}
