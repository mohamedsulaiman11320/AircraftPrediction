import { useEffect, useRef } from 'react';

/**
 * Calls `fn` every `intervalMs` while `active` is true. `fn` may return true to stop polling.
 * Errors are passed to `onError` and polling continues with back-off (up to 4x interval).
 */
export function usePolling(fn, { intervalMs = 2000, active = true, onError } = {}) {
  const fnRef = useRef(fn);
  fnRef.current = fn;
  const errRef = useRef(onError);
  errRef.current = onError;

  useEffect(() => {
    if (!active) return undefined;
    let cancelled = false;
    let timer = null;
    let delay = intervalMs;
    const tick = async () => {
      if (cancelled) return;
      try {
        const done = await fnRef.current();
        delay = intervalMs;
        if (done || cancelled) return;
      } catch (err) {
        errRef.current?.(err);
        delay = Math.min(delay * 2, intervalMs * 4);
      }
      if (!cancelled) timer = setTimeout(tick, delay);
    };
    timer = setTimeout(tick, intervalMs);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [active, intervalMs]);
}
