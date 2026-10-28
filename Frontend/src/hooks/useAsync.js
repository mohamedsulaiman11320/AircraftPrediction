import { useCallback, useEffect, useRef, useState } from 'react';

/**
 * Run an async function and track { data, error, loading }. Re-runs when `deps` change.
 * Results of stale calls are ignored. `reload()` re-runs on demand.
 * Pass `{ enabled: false }` to skip running.
 */
export function useAsync(fn, deps = [], { enabled = true, keepData = true } = {}) {
  const [state, setState] = useState({ data: null, error: null, loading: enabled });
  const callId = useRef(0);
  const fnRef = useRef(fn);
  fnRef.current = fn;

  const run = useCallback(async () => {
    const id = ++callId.current;
    setState((s) => ({ data: keepData ? s.data : null, error: null, loading: true }));
    try {
      const data = await fnRef.current();
      if (id === callId.current) setState({ data, error: null, loading: false });
      return data;
    } catch (error) {
      if (id === callId.current) setState((s) => ({ data: keepData ? s.data : null, error, loading: false }));
      return undefined;
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [keepData]);

  useEffect(() => {
    if (!enabled) {
      setState((s) => ({ ...s, loading: false }));
      return undefined;
    }
    run();
    return () => {
      callId.current++;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [enabled, ...deps]);

  const setData = useCallback((updater) => {
    setState((s) => ({ ...s, data: typeof updater === 'function' ? updater(s.data) : updater }));
  }, []);

  return { ...state, reload: run, setData };
}

/** Alias kept for readability at call sites that fetch from the API. */
export const useApi = useAsync;
