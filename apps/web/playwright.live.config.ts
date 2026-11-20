import { defineConfig, devices } from '@playwright/test';

/**
 * The live screens (src/live), built with VITE_API_MODE=http and answered by the fixed API stub in
 * e2e/live/api-stub.ts, so no server is needed:
 *
 *   npx playwright test -c playwright.live.config.ts
 *
 * The build goes to dist-live so it never replaces the demo build in dist.
 */
const PORT = Number(process.env.E2E_PORT ?? 5179);

export default defineConfig({
  testDir: './e2e/live',
  outputDir: './test-results/live',
  fullyParallel: true,
  retries: 2,
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'playwright-report/live' }]],
  expect: {
    toHaveScreenshot: { maxDiffPixelRatio: 0.01, animations: 'disabled' },
  },
  use: {
    baseURL: `http://127.0.0.1:${PORT}`,
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: `VITE_API_MODE=http VITE_POS_SIMULATOR_TOOLS=true npx vite build --outDir dist-live --emptyOutDir && npx vite preview --outDir dist-live --host 127.0.0.1 --port ${PORT} --strictPort`,
    timeout: 180_000,
    url: `http://127.0.0.1:${PORT}`,
    reuseExistingServer: !process.env.CI,
  },
});
