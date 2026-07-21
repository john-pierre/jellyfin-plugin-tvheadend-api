// Playwright configuration for the TVHeadend plugin end-to-end suite.
// All settings are overridable via environment variables (see fixtures/jellyfin.js and README.md).
const { defineConfig, devices } = require('@playwright/test');

module.exports = defineConfig({
  testDir: './tests',
  // The tests share a single Jellyfin + TVHeadend backend (and a limited number of tuners),
  // so they must run serially — never in parallel.
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  // Per-test default; the soak test overrides this to 0 (no timeout) via its own annotation.
  timeout: 180_000,
  expect: { timeout: 15_000 },
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: process.env.JELLYFIN_URL || 'http://localhost:8096',
    ignoreHTTPSErrors: true,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
    actionTimeout: 20_000,
    launchOptions: {
      // The three disable-*-throttling/backgrounding flags matter for live playback: after
      // 60s of being considered backgrounded, Chromium throttles timers to 1/min — hls.js
      // stops fetching segments and long-running playback tests stall at ~80s.
      args: [
        '--no-sandbox', '--disable-dev-shm-usage', '--autoplay-policy=no-user-gesture-required', '--start-maximized',
        '--disable-background-timer-throttling', '--disable-backgrounding-occluded-windows', '--disable-renderer-backgrounding',
      ],
      // Set SLOWMO=250 (ms) to slow each action down so a human can follow a headed run.
      slowMo: Number(process.env.SLOWMO) || 0,
    },
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
  ],
});
