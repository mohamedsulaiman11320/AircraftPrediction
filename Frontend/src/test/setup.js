import '@testing-library/jest-dom/vitest';
import { cleanup, configure } from '@testing-library/react';
import { afterEach, beforeEach, vi } from 'vitest';

// Full-app renders can be slow when test files run in parallel workers.
configure({ asyncUtilTimeout: 4000 });

// jsdom does not implement scrolling.
window.scrollTo = () => {};

// Never hit the network from tests: any request a test did not mock fails fast.
beforeEach(() => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url) => {
      throw new TypeError(`Unmocked fetch in test: ${url}`);
    }),
  );
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  window.sessionStorage.clear();
  window.history.replaceState(null, '', '/');
});
