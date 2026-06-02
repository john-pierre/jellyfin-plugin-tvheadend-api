// Verifies the dashboard/metrics APIs return well-formed data and the dashboard page renders.
const { test, expect, request } = require('@playwright/test');
const JF = require('../fixtures/jellyfin');

test.describe('Dashboard & metrics', () => {
  let api, token;

  test.beforeAll(async () => {
    api = await request.newContext({ baseURL: JF.CONFIG.baseURL, ignoreHTTPSErrors: true });
    ({ token } = await JF.authenticate(api));
  });
  test.afterAll(async () => { await api.dispose(); });

  test('dashboard status reports TVHeadend reachable', async () => {
    const res = await api.get('/TvHeadendApi/Dashboard', { headers: JF.authHeaders(token) });
    expect(res.ok()).toBeTruthy();
    const d = await res.json();
    // Reachability is expressed slightly differently across versions; accept any truthy signal.
    const reachable = d.Reachable ?? d.IsReachable ?? d.TvHeadend?.Reachable ?? d.Status?.Reachable;
    expect(reachable, `dashboard payload: ${JSON.stringify(d).slice(0, 200)}`).toBeTruthy();
  });

  test('relay metrics expose the expected fields', async () => {
    const res = await api.get('/TvHeadendApi/RelayMetrics?hours=24', { headers: JF.authHeaders(token) });
    expect(res.ok()).toBeTruthy();
    const m = await res.json();
    for (const f of ['TotalRequests', 'SuccessRate', 'ActiveStreams', 'CacheHitRatio']) {
      expect(m, `metrics field ${f}`).toHaveProperty(f);
    }
    expect(typeof m.SuccessRate).toBe('number');
  });

  test('live streaming metrics endpoint responds', async () => {
    const res = await api.get('/TvHeadendApi/Metrics/Live', { headers: JF.authHeaders(token) });
    expect(res.ok(), `Metrics/Live status ${res.status()}`).toBeTruthy();
  });

  test('dashboard page renders in the browser', async ({ page }) => {
    await JF.uiLogin(page);
    await JF.openPluginPage(page, 'TvHeadendDashboard');
    const body = await page.evaluate(() => document.body.innerText);
    expect(body, 'dashboard shows content').toMatch(/tvheadend|tuner|relay|health|connection/i);
  });
});
