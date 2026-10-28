/** Thin wrapper over the History API so navigation can be triggered outside React (e.g. on a 401). */
const NAV_EVENT = 'afp:navigate';

export function getLocation() {
  return { pathname: window.location.pathname, search: window.location.search };
}

export function navigate(to, { replace = false, state = null } = {}) {
  const current = window.location.pathname + window.location.search;
  if (to === current && !replace) return;
  if (replace) window.history.replaceState(state, '', to);
  else window.history.pushState(state, '', to);
  window.dispatchEvent(new Event(NAV_EVENT));
}

export function subscribe(listener) {
  window.addEventListener('popstate', listener);
  window.addEventListener(NAV_EVENT, listener);
  return () => {
    window.removeEventListener('popstate', listener);
    window.removeEventListener(NAV_EVENT, listener);
  };
}
