import { useState } from 'react';
import { workOrdersApi } from '../../api/workOrders.js';
import { useAsync } from '../../hooks/useAsync.js';
import { useNavigate, useQuery } from '../../router/Router.jsx';
import { Link } from '../../router/Link.jsx';
import { PageHeader } from '../../components/PageHeader.jsx';
import { FormError, SelectField, TextArea, TextField } from '../../components/FormFields.jsx';
import { Alert } from '../../components/Misc.jsx';
import { useToast } from '../../components/Toast.jsx';
import { AircraftSelect } from '../shared/AircraftSelect.jsx';
import { WO_PRIORITY, options } from '../../utils/enums.js';
import { toUserId } from './WorkOrderActions.jsx';
import { fromInputDateTime } from '../../utils/format.js';

export function WorkOrderCreatePage() {
  const [query] = useQuery();
  const navigate = useNavigate();
  const toast = useToast();
  const techs = useAsync(() => workOrdersApi.technicians(), []);
  const [form, setForm] = useState({
    aircraftId: query.aircraftId || '',
    priority: query.priority || 'Medium',
    title: '',
    description: '',
    dueAtUtc: '',
    assignedTechnicianId: '',
    componentId: query.componentId || '',
  });
  const anomalyId = query.anomalyId || '';
  const predictionId = query.predictionId || '';
  const [errors, setErrors] = useState({});
  const [serverError, setServerError] = useState(null);
  const [busy, setBusy] = useState(false);
  const set = (k) => (e) => setForm((f) => ({ ...f, [k]: e.target.value }));

  const submit = async (e) => {
    e.preventDefault();
    const v = {};
    if (!form.aircraftId) v.aircraftId = 'Choose an aircraft.';
    if (!form.title.trim()) v.title = 'Title is required.';
    if (!form.description.trim()) v.description = 'Description is required.';
    if (form.dueAtUtc && !fromInputDateTime(form.dueAtUtc)) v.dueAtUtc = 'Enter a valid date and time.';
    setErrors(v);
    setServerError(null);
    if (Object.keys(v).length) return;
    const body = {
      aircraftId: form.aircraftId,
      priority: form.priority,
      title: form.title.trim(),
      description: form.description.trim(),
    };
    if (form.dueAtUtc) body.dueAtUtc = fromInputDateTime(form.dueAtUtc);
    if (form.assignedTechnicianId) body.assignedTechnicianId = toUserId(form.assignedTechnicianId);
    if (form.componentId.trim()) body.componentId = form.componentId.trim();
    if (anomalyId) body.anomalyId = anomalyId;
    if (predictionId) body.predictionId = predictionId;
    setBusy(true);
    try {
      const wo = await workOrdersApi.create(body);
      toast.success(`Work order ${wo.workOrderNumber} created.`);
      navigate(`/work-orders/${wo.id}`);
    } catch (err) {
      setBusy(false);
      if (err?.errors) {
        const mapped = {};
        ['aircraftId', 'title', 'description', 'priority', 'dueAtUtc', 'assignedTechnicianId'].forEach((k) => {
          const m = err.fieldError(k);
          if (m) mapped[k] = m;
        });
        setErrors(mapped);
      }
      setServerError(err);
    }
  };

  return (
    <>
      <PageHeader eyebrow={<Link to="/work-orders">Work orders</Link>} title="New work order" />
      <div className="card" style={{ maxWidth: 820 }}>
        <form className="card__body" onSubmit={submit} noValidate>
          {anomalyId || predictionId ? (
            <Alert tone="info" title="Linked record">
              {anomalyId ? (
                <p>
                  Linked to anomaly <Link to={`/anomalies/${anomalyId}`}>{anomalyId}</Link>.
                </p>
              ) : null}
              {predictionId ? <p>Linked to prediction {predictionId}.</p> : null}
            </Alert>
          ) : null}
          <FormError error={serverError} />
          <div className="form-grid">
            <AircraftSelect value={form.aircraftId} onChange={(v) => setForm((f) => ({ ...f, aircraftId: v }))} error={errors.aircraftId} required />
            <SelectField label="Priority" value={form.priority} onChange={set('priority')} options={options(WO_PRIORITY)} error={errors.priority} required />
            <TextField label="Title" value={form.title} onChange={set('title')} error={errors.title} maxLength={200} required className="field--full" />
            <TextArea label="Description" value={form.description} onChange={set('description')} error={errors.description} maxLength={8000} required className="field--full" />
            <TextField label="Due (UTC)" type="datetime-local" value={form.dueAtUtc} onChange={set('dueAtUtc')} error={errors.dueAtUtc} hint="Optional" />
            <SelectField
              label="Assign technician"
              value={form.assignedTechnicianId}
              onChange={set('assignedTechnicianId')}
              options={(techs.data || []).map((t) => ({ value: t.id, label: `${t.displayName} (${t.username})` }))}
              placeholder={techs.loading ? 'Loading…' : 'Leave unassigned'}
              error={errors.assignedTechnicianId}
              hint="Optional"
            />
            <TextField label="Component ID" value={form.componentId} onChange={set('componentId')} hint="Optional" />
          </div>
          <div className="form-actions">
            <Link to="/work-orders" className="btn btn--secondary">
              Cancel
            </Link>
            <button type="submit" className="btn btn--primary" disabled={busy}>
              {busy ? <span className="spinner" aria-hidden="true" /> : null}
              Create work order
            </button>
          </div>
        </form>
      </div>
    </>
  );
}
