import { createContext, useCallback, useContext, useMemo, useRef, useState } from 'react';
import { errorMessage } from '../api/client.js';

const ToastContext = createContext(null);

export function ToastProvider({ children }) {
  const [toasts, setToasts] = useState([]);
  const nextId = useRef(1);

  const dismiss = useCallback((id) => setToasts((t) => t.filter((x) => x.id !== id)), []);

  const push = useCallback(
    (type, message, { timeout = type === 'error' ? 8000 : 4500 } = {}) => {
      const id = nextId.current++;
      setToasts((t) => [...t.slice(-4), { id, type, message }]);
      if (timeout) setTimeout(() => dismiss(id), timeout);
      return id;
    },
    [dismiss],
  );

  const api = useMemo(
    () => ({
      success: (m, o) => push('success', m, o),
      info: (m, o) => push('info', m, o),
      error: (m, o) => push('error', typeof m === 'string' ? m : errorMessage(m), o),
      dismiss,
    }),
    [push, dismiss],
  );

  return (
    <ToastContext.Provider value={api}>
      {children}
      <div className="toast-region" role="region" aria-label="Notifications">
        {toasts.map((t) => (
          <div key={t.id} className={`toast toast--${t.type}`} role={t.type === 'error' ? 'alert' : 'status'}>
            <span className="toast__msg">{t.message}</span>
            <button type="button" className="toast__close" onClick={() => dismiss(t.id)} aria-label="Dismiss notification">
              ×
            </button>
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  );
}

const noop = () => 0;
const fallback = { success: noop, info: noop, error: noop, dismiss: noop };

export function useToast() {
  return useContext(ToastContext) || fallback;
}
