// UI action buttons on the two embedded admin pages — the pieces 02 (render) and 09 (form
// round-trip) do not cover: buttons that trigger REAL plugin actions from the browser.
//
// Order matters and this file is named to run LAST (after 13/14): the clear-logs action is
// destructive for accumulated log data, which is acceptable only once every other spec has
// finished asserting against it — so all read-only assertions happen first and the clear
// comes at the very end.
//
// ConfigPage: GenerateAuthTokenBtn (token appears, alphanumeric — the FFmpeg-URL-safe
// invariant), CreateProfileBtn (idempotent managed-profile provisioning), WarmCacheBtn
// (SSE-driven cache warmup; progress must appear — the run is aborted early by navigating
// away, which cancels the server-side warmup cooperatively, and the stack must be healthy
// afterwards). DashboardPage: tvhRefreshBtn (reloads all panels), tvhClearLogsBtn (confirm
// dialog -> DELETE -> the log display is really empty afterwards).
//
// Interaction notes (same as 01/09): emby-button/emby-checkbox custom elements can intercept
// plain clicks — use click({ force: true }) where needed.
const { test, expect, request } = require('@playwright/test');
const JF = require('../fixtures/jellyfin');

test.describe.configure({ mode: 'serial' });

test.describe('UI action buttons', () => {
  let api, token, pluginId;

  test.beforeAll(async () => {
    api = await request.newContext({ baseURL: JF.CONFIG.baseURL, ignoreHTTPSErrors: true });
    ({ token } = await JF.authenticate(api));
    pluginId = await JF.getPluginId(api, token);
    expect(pluginId, 'plugin installed').toBeTruthy();
  });

  test.afterAll(async () => {
    const cfg = await JF.readPluginConfig(api, token, pluginId);
    expect(JF.canonicalConfigDeviations(cfg), 'config still canonical').toEqual([]);
    await JF.waitForDiagnoseChannels(api, token);
    await api.dispose();
  });

  test('dashboard: refresh button reloads status, statistics, and logs', async ({ page }) => {
    test.setTimeout(120000);
    await JF.uiLogin(page);
    await JF.openPluginPage(page, 'TvHeadendDashboard');

    // Initial load done — the logs panel shows its "X of Y entries" status line.
    await expect(page.locator('#tvhLogsStatus')).toHaveText(/\d+ of \d+ entries/, { timeout: 30000 });

    await page.locator('#tvhRefreshBtn').click({ force: true });
    await expect(page.locator('#tvhLogsLastUpdated')).toHaveText(/Last updated: /, { timeout: 30000 });
    await expect(page.locator('#tvhLogsStatus')).toHaveText(/\d+ of \d+ entries/);
    const body = await page.evaluate(() => document.body.innerText);
    expect(body, 'dashboard shows live content after refresh').toMatch(/tvheadend|tuner|relay|health|connection/i);
    console.log('[15|refresh] dashboard refreshed, logs panel re-populated');
  });

  test('config page: GenerateAuthTokenBtn produces an alphanumeric token', async ({ page }) => {
    test.setTimeout(120000);
    await JF.uiLogin(page);
    await JF.openPluginPage(page, 'TvHeadendApiConfig');

    await page.locator('.tvh-tab-btn[data-tab="tab-connection"]').click({ force: true });
    const before = await page.locator('#AuthToken').inputValue();

    await page.locator('#GenerateAuthTokenBtn').click({ force: true });
    // The handler saves the form, calls GenerateAuthToken, then fills the field + message.
    await expect(page.locator('#AuthTokenActionMsg')).not.toHaveText(/^(Generating…)?$/, { timeout: 60000 });
    const msg = (await page.locator('#AuthTokenActionMsg').textContent()) || '';
    console.log(`[15|token] message: "${msg.trim()}"`);
    expect(msg, 'no failure message').not.toMatch(/failed/i);

    // NOTE: the backend is deliberately idempotent — TokenService saves the TVHeadend user
    // without a token refresh on the first attempt, so TVHeadend keeps returning the user's
    // existing persistent token as long as it is format-valid. The value may therefore equal
    // the previous one; what matters is that a valid token is present and persisted.
    const tokenValue = await page.locator('#AuthToken').inputValue();
    console.log(`[15|token] token length ${tokenValue.length}, changed=${tokenValue !== before}`);
    expect(tokenValue.length, 'a token was generated').toBeGreaterThan(0);
    expect(tokenValue, 'token is strictly alphanumeric (FFmpeg-URL-safe)').toMatch(/^[A-Za-z0-9]+$/);

    // The generated token is persisted into the plugin configuration.
    const cfg = await JF.readPluginConfig(api, token, pluginId);
    expect(cfg.AuthToken, 'generated token persisted to the configuration').toBe(tokenValue);
  });

  test('config page: CreateProfileBtn provisions the managed profile idempotently', async ({ page }) => {
    test.setTimeout(180000);
    await JF.uiLogin(page);
    await JF.openPluginPage(page, 'TvHeadendApiConfig');

    await page.locator('#CreateProfileBtn').click({ force: true });
    // Provisioning talks to TVHeadend (create/update codec + transcode profiles) — allow time.
    await expect(page.locator('#ProfileActionMsg')).not.toHaveText(/^(Creating profiles…)?$/, { timeout: 120000 });
    const msg = (await page.locator('#ProfileActionMsg').textContent()) || '';
    console.log(`[15|profile] message: "${msg.trim()}"`);
    // Idempotent: success or an already-exists/updated notice — but never a failure.
    expect(msg, 'no failure message').not.toMatch(/failed|error/i);
    expect(msg.trim().length, 'a result message is shown').toBeGreaterThan(0);

    // The managed profile really exists on the TVHeadend side and stays the default.
    const options = await (await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/ProfileOptions`, { headers: JF.authHeaders(token) })).json();
    expect(options.StreamingProfiles, 'managed profile exists in TVHeadend').toContain('jellyfin');
    const cfg = await JF.readPluginConfig(api, token, pluginId);
    expect(cfg.StreamingProfileSettings.DefaultTvHeadendProfile, 'managed profile is the default').toBe('jellyfin');
  });

  test('config page: WarmCacheBtn streams progress; abort leaves the stack healthy', async ({ page }) => {
    test.setTimeout(240000);
    // Only the warmup's own probe subscriptions matter — the periodic OTA EPG grabber
    // ('epggrab') opens/closes its own subscriptions on its own schedule.
    const nonGrabberSubs = async () =>
      (((await JF.tvhApi('/api/status/subscriptions')).entries || []).filter((s) => s.title !== 'epggrab')).length;
    const subsBefore = await nonGrabberSubs();

    await JF.uiLogin(page);
    await JF.openPluginPage(page, 'TvHeadendApiConfig');

    await page.locator('#WarmCacheBtn').click({ force: true });
    // Progress must appear: the status element fades in with "Preparing…", then per-channel
    // progress ("n/m" on the button) or — when everything is already cached — a Done summary.
    await expect(page.locator('#WarmCacheStatus')).not.toHaveText(/^$/, { timeout: 30000 });
    const status1 = (await page.locator('#WarmCacheStatus').textContent()) || '';
    const btn1 = (await page.locator('#WarmCacheBtn span').textContent()) || '';
    console.log(`[15|warm] progress visible: status="${status1.trim()}", button="${btn1.trim()}"`);
    expect(`${status1} ${btn1}`, 'warmup progress or completion is displayed').toMatch(/Preparing|Done|already|failed|\d+\s*\/\s*\d+|warmed|Warming/i);

    // Watch briefly for real per-channel progress, then abort early (budget). Jellyfin web is
    // a SPA — a hash navigation would NOT tear down the page's fetch, so a full reload is used
    // to abort the SSE request; the controller then cancels the warmup cooperatively
    // (WarmCacheStream propagates HttpContext.RequestAborted into the warmup task).
    await page.waitForTimeout(8000);
    const btn2 = (await page.locator('#WarmCacheBtn span').textContent()) || '';
    const status2 = (await page.locator('#WarmCacheStatus').textContent()) || '';
    console.log(`[15|warm] after 8s: status="${status2.trim()}", button="${btn2.trim()}"`);
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(3000);

    // The aborted warmup must drain its probe subscriptions and leave the backend healthy.
    await JF.pollUntil(async () => {
      const subs = await nonGrabberSubs();
      return subs <= subsBefore ? true : null;
    }, { timeoutMs: 120000, intervalMs: 5000, label: 'TVHeadend subscriptions drain back to baseline' });
    const diag = await JF.waitForDiagnoseChannels(api, token);
    expect(JF.HEALTHY_DIAGNOSE_STATUSES, `stack healthy after aborted warmup (got ${diag.OverallStatus})`).toContain(diag.OverallStatus);
    console.log(`[15|warm] aborted cleanly, subscriptions back to <=${subsBefore}, Diagnose ${diag.OverallStatus}`);
  });

  // LAST: destructive for accumulated log data — must run after every read assertion above.
  test('dashboard: tvhClearLogsBtn empties the log store and the display', async ({ page }) => {
    test.setTimeout(180000);
    // Seed the store deterministically instead of relying on residue from earlier specs (a
    // previous run of THIS spec may have just cleared it): invalid relay-token hits and a
    // Diagnose call produce plugin log entries. This doubles as a live probe of the log
    // pipeline itself — PluginLogService carries a PERMANENT per-session kill switch
    // (_dbLoggingFailed): one failed SQLite flush silently disables ALL log persistence
    // until Jellyfin restarts. If seeding surfaces nothing the pipeline is dead, and this
    // fails loudly instead of green-washing an empty panel.
    for (let i = 0; i < 3; i++) {
      await JF.fetchStreamBytes(`${JF.CONFIG.baseURL}/api/tvheadend/relay/stream/e2e-log-seed-${i}?token=e2eLogSeedToken${i}00000000`, { minBytes: 1, timeoutMs: 5000 });
    }
    await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/Diagnose`, { headers: JF.authHeaders(token) });
    const logsBefore = await JF.pollUntil(async () => {
      const res = await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/Dashboard/Logs?limit=1`, { headers: JF.authHeaders(token) });
      if (!res.ok()) return null;
      const j = await res.json();
      return j.TotalCount > 0 ? j : null;
    }, { timeoutMs: 60000, intervalMs: 3000, label: 'log store ingests seeded entries (dead pipeline = tripped _dbLoggingFailed kill switch)' });
    console.log(`[15|clear] logs before: ${logsBefore.TotalCount}`);

    await JF.uiLogin(page);
    await JF.openPluginPage(page, 'TvHeadendDashboard');
    await expect(page.locator('#tvhLogsStatus')).toHaveText(/\d+ of \d+ entries/, { timeout: 30000 });

    page.on('dialog', (dialog) => dialog.accept());
    await page.locator('#tvhClearLogsBtn').click({ force: true });

    // Store side: the DELETE really emptied the log tables (a handful of fresh entries may
    // trickle in immediately — the plugin keeps logging — so "empty" means a tiny residue).
    const after = await JF.pollUntil(async () => {
      const res = await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/Dashboard/Logs?limit=1`, { headers: JF.authHeaders(token) });
      if (!res.ok()) return null;
      const j = await res.json();
      return j.TotalCount < logsBefore.TotalCount && j.TotalCount <= 20 ? j : null;
    }, { timeoutMs: 30000, intervalMs: 2000, label: 'log store emptied' });
    console.log(`[15|clear] logs after: ${after.TotalCount}`);

    // Display side: the panel re-loaded and shows the emptied store.
    await expect(page.locator('#tvhLogsStatus')).toHaveText(/^\d{1,2} of \d{1,2} entries/, { timeout: 30000 });
    const shown = (await page.locator('#tvhLogsStatus').textContent()) || '';
    console.log(`[15|clear] display: "${shown.trim()}"`);
  });
});
