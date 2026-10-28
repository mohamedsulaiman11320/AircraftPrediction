import { useId, useState } from 'react';

function describedBy(hintId, errorId, hint, error) {
  return [hint ? hintId : null, error ? errorId : null].filter(Boolean).join(' ') || undefined;
}

function Label({ htmlFor, label, required }) {
  return (
    <label className="field__label" htmlFor={htmlFor}>
      {label}
      {required ? (
        <span className="field__required" aria-hidden="true">
          *
        </span>
      ) : null}
    </label>
  );
}

export function TextField({ label, error, hint, required, className = '', id: idProp, type = 'text', ...rest }) {
  const auto = useId();
  const id = idProp || auto;
  return (
    <div className={`field ${className}`}>
      <Label htmlFor={id} label={label} required={required} />
      <input
        id={id}
        type={type}
        className="input"
        aria-invalid={error ? 'true' : undefined}
        aria-describedby={describedBy(`${id}-hint`, `${id}-err`, hint, error)}
        aria-required={required || undefined}
        {...rest}
      />
      {hint ? (
        <span className="field__hint" id={`${id}-hint`}>
          {hint}
        </span>
      ) : null}
      {error ? (
        <span className="field__error" id={`${id}-err`}>
          {error}
        </span>
      ) : null}
    </div>
  );
}

export function PasswordField({ label = 'Password', error, hint, required, className = '', id: idProp, ...rest }) {
  const auto = useId();
  const id = idProp || auto;
  const [visible, setVisible] = useState(false);
  return (
    <div className={`field ${className}`}>
      <Label htmlFor={id} label={label} required={required} />
      <div className="input-group">
        <input
          id={id}
          type={visible ? 'text' : 'password'}
          className="input"
          aria-invalid={error ? 'true' : undefined}
          aria-describedby={describedBy(`${id}-hint`, `${id}-err`, hint, error)}
          aria-required={required || undefined}
          {...rest}
        />
        <button
          type="button"
          className="input-group__btn"
          onClick={() => setVisible((v) => !v)}
          aria-pressed={visible}
          aria-label={visible ? `Hide ${label.toLowerCase()}` : `Show ${label.toLowerCase()}`}
        >
          {visible ? 'Hide' : 'Show'}
        </button>
      </div>
      {hint ? (
        <span className="field__hint" id={`${id}-hint`}>
          {hint}
        </span>
      ) : null}
      {error ? (
        <span className="field__error" id={`${id}-err`}>
          {error}
        </span>
      ) : null}
    </div>
  );
}

/** options: [{ value, label }] or strings */
export function SelectField({ label, error, hint, required, options = [], placeholder, className = '', id: idProp, ...rest }) {
  const auto = useId();
  const id = idProp || auto;
  return (
    <div className={`field ${className}`}>
      <Label htmlFor={id} label={label} required={required} />
      <select
        id={id}
        className="select"
        aria-invalid={error ? 'true' : undefined}
        aria-describedby={describedBy(`${id}-hint`, `${id}-err`, hint, error)}
        aria-required={required || undefined}
        {...rest}
      >
        {placeholder !== undefined ? <option value="">{placeholder}</option> : null}
        {options.map((o) => {
          const opt = typeof o === 'string' ? { value: o, label: o } : o;
          return (
            <option key={opt.value} value={opt.value}>
              {opt.label}
            </option>
          );
        })}
      </select>
      {hint ? (
        <span className="field__hint" id={`${id}-hint`}>
          {hint}
        </span>
      ) : null}
      {error ? (
        <span className="field__error" id={`${id}-err`}>
          {error}
        </span>
      ) : null}
    </div>
  );
}

export function TextArea({ label, error, hint, required, className = '', id: idProp, ...rest }) {
  const auto = useId();
  const id = idProp || auto;
  return (
    <div className={`field ${className}`}>
      <Label htmlFor={id} label={label} required={required} />
      <textarea
        id={id}
        className="textarea"
        aria-invalid={error ? 'true' : undefined}
        aria-describedby={describedBy(`${id}-hint`, `${id}-err`, hint, error)}
        aria-required={required || undefined}
        {...rest}
      />
      {hint ? (
        <span className="field__hint" id={`${id}-hint`}>
          {hint}
        </span>
      ) : null}
      {error ? (
        <span className="field__error" id={`${id}-err`}>
          {error}
        </span>
      ) : null}
    </div>
  );
}

export function Checkbox({ label, id: idProp, ...rest }) {
  const auto = useId();
  const id = idProp || auto;
  return (
    <div className="checkbox">
      <input id={id} type="checkbox" {...rest} />
      <label htmlFor={id}>{label}</label>
    </div>
  );
}

/** Form-level error summary. Accepts an ApiError or a string. */
export function FormError({ error, title }) {
  if (!error) return null;
  const isString = typeof error === 'string';
  const heading = title || (isString ? null : error.title);
  const detail = isString ? error : error.detail;
  const fieldErrors = !isString && error.errors ? Object.entries(error.errors) : [];
  return (
    <div className="alert alert--danger" role="alert">
      <div>
        {heading ? <div className="alert__title">{heading}</div> : null}
        {detail ? <p>{detail}</p> : null}
        {fieldErrors.length > 0 ? (
          <ul>
            {fieldErrors.map(([k, msgs]) => (
              <li key={k}>{Array.isArray(msgs) ? msgs.join(' ') : String(msgs)}</li>
            ))}
          </ul>
        ) : null}
        {!heading && !detail && fieldErrors.length === 0 ? <p>{error.message || 'Something went wrong.'}</p> : null}
      </div>
    </div>
  );
}
