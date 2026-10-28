import { Icon } from './Icon.jsx';
import { errorMessage } from '../api/client.js';

export function EmptyState({ title = 'Nothing to show', message, action, icon = 'inbox' }) {
  return (
    <div className="state">
      <div className="state__icon">
        <Icon name={icon} size={22} />
      </div>
      <div className="state__title">{title}</div>
      {message ? <p className="muted">{message}</p> : null}
      {action || null}
    </div>
  );
}

export function ErrorState({ error, title = 'Could not load data', onRetry }) {
  const forbidden = error?.status === 403;
  return (
    <div className="state state--error" role="alert">
      <div className="state__icon">
        <Icon name={forbidden ? 'lock' : 'alert'} size={22} />
      </div>
      <div className="state__title">{forbidden ? 'Access denied' : title}</div>
      <p className="muted">{errorMessage(error)}</p>
      {error?.traceId ? <p className="subtle">Trace ID: <span className="mono">{error.traceId}</span></p> : null}
      {onRetry && !forbidden ? (
        <button type="button" className="btn btn--secondary" onClick={onRetry}>
          <Icon name="refresh" size={16} /> Retry
        </button>
      ) : null}
    </div>
  );
}

/** Convenience: render loading / error / empty / content. */
export function AsyncBoundary({ loading, error, data, onRetry, skeleton, isEmpty, empty, children }) {
  if (error && !data) return <ErrorState error={error} onRetry={onRetry} />;
  if (loading && !data) return skeleton || null;
  if (isEmpty ? isEmpty(data) : false) return empty || <EmptyState />;
  return children(data);
}
