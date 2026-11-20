import { defineConfig, devices } from '@playwright/test';

// E2E_PORT lets a second checkout run its suite without meeting another one's server.
const PORT = Number(process.env.E2E_PORT ?? 5180);

export default defineConfig({
  testDir: './e2e',
  // The live screens run against a build in API mode: playwright.live.config.ts.
  testIgnore: 'live/**',
  outputDir: './test-results',
  fullyParallel: true,
  // On the development machine some local requests are intermittently refused with
  // net::ERR_NETWORK_ACCESS_DENIED (outside the app). Retries absorb that; real failures still fail.
  retries: 2,
  reporter: [['list'], ['html', { open: 'never' }]],
  expect: {
    // Visual baselines (e2e/visual.spec.ts): small anti-aliasing differences are not regressions.
    toHaveScreenshot: { maxDiffPixelRatio: 0.01, animations: 'disabled' },
  },
  use: {
    baseURL: `http://127.0.0.1:${PORT}`,
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    // Tests run against the production build, as the investor demo will be served.
    command: `npm run build && npm run preview -- --host 127.0.0.1 --port ${PORT} --strictPort`,
    timeout: 180_000,
    url: `http://127.0.0.1:${PORT}`,
    reuseExistingServer: !process.env.CI,
  },
});
