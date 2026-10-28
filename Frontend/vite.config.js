import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: { port: 5173, strictPort: true },
  preview: { port: 5173 },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.js'],
    css: false,
    restoreMocks: true,
    // Playwright owns ./e2e; Vitest must not try to run those specs (`npm run test:e2e`).
    exclude: ['**/node_modules/**', '**/dist/**', 'e2e/**'],
  },
});
