import { useEffect, useState } from 'react';
import { aircraftApi } from '../../api/aircraft.js';
import { useAsync } from '../../hooks/useAsync.js';
import { useNavigate, useParams } from '../../router/Router.jsx';
import { Link } from '../../router/Link.jsx';
import { PageHeader } from '../../components/PageHeader.jsx';
import { FormError, TextField } from '../../components/FormFields.jsx';
import { ErrorState } from '../../components/States.jsx';
import { SkeletonPage } from '../../components/Skeleton.jsx';
import { useToast } from '../../components/Toast.jsx';

const EMPTY = { registrationNumber: '', model: '', manufacturer: '', fleetGroup: '', serialNumber: '' };

export function AircraftFormPage() {
  const { id } = useParams();
  const isEdit = !!id;
  const navigate = useNavigate();
  const toast = useToast();
  const existing = useAsync(() => aircraftApi.get(id), [id], { enabled: isEdit });
  const groups = useAsync(() => aircraftApi.fleetGroups(), []);
  const [form, setForm] = useState(EMPTY);
  const [errors, setErrors] = useState({});
  const [serverError, setServerError] = useState(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (existing.data) {
      const a = existing.data;
      setForm({ registrationNumber: a.registrationNumber, model: a.model, manufacturer: a.manufacturer, fleetGroup: a.fleetGroup, serialNumber: a.serialNumber || '' });
    }
  }, [existing.data]);

  if (isEdit && existing.error) return <ErrorState error={existing.error} onRetry={existing.reload} />;
  if (isEdit && !existing.data) return <SkeletonPage />;

  const set = (k) => (e) => setForm((f) => ({ ...f, [k]: e.target.value }));

  const submit = async (e) => {
    e.preventDefault();
    const v = {};
    if (!form.registrationNumber.trim()) v.registrationNumber = 'Registration number is required.';
    if (!form.model.trim()) v.model = 'Model is required.';
    if (!form.manufacturer.trim()) v.manufacturer = 'Manufacturer is required.';
    if (!form.fleetGroup.trim()) v.fleetGroup = 'Fleet group is required.';
    setErrors(v);
    setServerError(null);
    if (Object.keys(v).length) return;
    const body = {
      registrationNumber: form.registrationNumber.trim().toUpperCase(),
      model: form.model.trim(),
      manufacturer: form.manufacturer.trim(),
      fleetGroup: form.fleetGroup.trim(),
    };
    if (form.serialNumber.trim()) body.serialNumber = form.serialNumber.trim();
    setBusy(true);
    try {
      const saved = isEdit ? await aircraftApi.update(id, body) : await aircraftApi.create(body);
      toast.success(isEdit ? 'Aircraft updated.' : 'Aircraft created.');
      navigate(`/fleet/${saved.id}`);
    } catch (err) {
      setBusy(false);
      if (err?.status === 409) {
        setErrors({ registrationNumber: 'An aircraft with this registration number already exists.' });
      } else if (err?.errors) {
        const mapped = {};
        Object.keys(EMPTY).forEach((k) => {
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
      <PageHeader eyebrow={<Link to="/fleet">Fleet</Link>} title={isEdit ? `Edit ${existing.data.registrationNumber}` : 'Add aircraft'} />
      <div className="card" style={{ maxWidth: 760 }}>
        <form className="card__body" onSubmit={submit} noValidate>
          <FormError error={serverError} />
          <div className="form-grid">
            <TextField label="Registration number" value={form.registrationNumber} onChange={set('registrationNumber')} error={errors.registrationNumber} maxLength={20} required />
            <TextField label="Serial number" value={form.serialNumber} onChange={set('serialNumber')} error={errors.serialNumber} maxLength={50} hint="Optional" />
            <TextField label="Manufacturer" value={form.manufacturer} onChange={set('manufacturer')} error={errors.manufacturer} maxLength={100} required />
            <TextField label="Model" value={form.model} onChange={set('model')} error={errors.model} maxLength={100} required />
            <TextField
              label="Fleet group"
              value={form.fleetGroup}
              onChange={set('fleetGroup')}
              error={errors.fleetGroup}
              maxLength={100}
              list="fleet-group-options"
              required
            />
            <datalist id="fleet-group-options">
              {(groups.data || []).map((g) => (
                <option key={g} value={g} />
              ))}
            </datalist>
          </div>
          <div className="form-actions">
            <Link to={isEdit ? `/fleet/${id}` : '/fleet'} className="btn btn--secondary">
              Cancel
            </Link>
            <button type="submit" className="btn btn--primary" disabled={busy}>
              {busy ? <span className="spinner" aria-hidden="true" /> : null}
              {isEdit ? 'Save changes' : 'Create aircraft'}
            </button>
          </div>
        </form>
      </div>
    </>
  );
}
