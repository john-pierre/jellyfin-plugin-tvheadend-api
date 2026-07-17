// EPG data quality + admin-authorization sweep.
//
// A) Guide: the bootstrap guarantees XMLTV EPG coverage (2 grab runs) for the 100 simulator
//    channels — GET /LiveTv/Programs must return plausible, titled programs across a broad
//    channel range, and the plugin's channel-group (tag) endpoint must serve the guide's tags.
// B) AuthZ: every /TvHeadendApi/* endpoint is admin-only ([Authorize(RequiresElevation)] on
//    every controller under Api/ except RelayController) — an anonymous request must get 401
//    for ALL of them. The route inventory below was compiled by grepping the controllers
//    (PluginController, DashboardController, MetricsController, MonitoringController,
//    StatisticsController, StreamingProfileController, LogsController, DashboardLogsController).
//    Mutating endpoints (ResetToDefaults, WarmCache, InvalidateCache, DELETEs, RevokeAll …)
//    are ONLY probed anonymously — an authenticated call would wreck the shared stack
//    (ResetToDefaults) or skew other specs' data (cache/metrics/statistics mutations).
//    RelayController routes are anonymous BY DESIGN (token-secured); they are asserted
//    separately: reachable without Jellyfin auth but closed without a relay token.
// C) A few authenticated read-only GETs prove the admin surface actually serves well-formed
//    payloads (Health, Statistics, ProfileOptions).
const { test, expect, request } = require('@playwright/test');
const JF = require('../fixtures/jellyfin');

test.describe.configure({ mode: 'serial' });

// ── B) Route inventory: [method, path] — every admin-only plugin endpoint. ──
const ADMIN_ENDPOINTS = [
  // PluginController
  ['GET', '/TvHeadendApi/PluginInfo'],
  ['POST', '/TvHeadendApi/ResetToDefaults'], // MUTATING — anonymous probe only!
  ['GET', '/TvHeadendApi/Diagnose'],
  ['POST', '/TvHeadendApi/CreateProfile'], // MUTATING — anonymous probe only!
  ['POST', '/TvHeadendApi/GenerateAuthToken'], // MUTATING — anonymous probe only!
  ['GET', '/TvHeadendApi/ProfileOptions'],
  ['POST', '/TvHeadendApi/WarmCache'], // MUTATING — anonymous probe only!
  ['POST', '/TvHeadendApi/WarmCacheStream'], // MUTATING — anonymous probe only!
  // KnownClients exists in the current source but not yet in the plugin build deployed to
  // this e2e stack (404 even when authenticated) — accept 401 (new build) or 404 (old build);
  // both prove the route serves nothing anonymously.
  ['GET', '/TvHeadendApi/KnownClients', [401, 404]],
  ['POST', '/TvHeadendApi/InvalidateCache'], // MUTATING — anonymous probe only!
  // DashboardController
  ['GET', '/TvHeadendApi/Dashboard'],
  ['GET', '/TvHeadendApi/RelayMetrics'],
  ['GET', '/TvHeadendApi/Dashboard/Tokens'],
  ['POST', '/TvHeadendApi/Dashboard/Tokens/RevokeAll'], // MUTATING — anonymous probe only!
  ['DELETE', '/TvHeadendApi/RelayMetrics'], // MUTATING — anonymous probe only!
  ['DELETE', '/TvHeadendApi/Dashboard/Logs'], // MUTATING — anonymous probe only!
  // DashboardLogsController / LogsController
  ['GET', '/TvHeadendApi/Dashboard/Logs'],
  ['GET', '/TvHeadendApi/Logs'],
  ['GET', '/TvHeadendApi/Logs/diskspace'],
  ['GET', '/TvHeadendApi/Logs/entries'],
  // MetricsController
  ['GET', '/TvHeadendApi/Metrics/Live'],
  ['GET', '/TvHeadendApi/Metrics/History'],
  ['GET', '/TvHeadendApi/Metrics/Session/e2e-authz-probe'],
  // MonitoringController
  ['GET', '/TvHeadendApi/Status'],
  ['GET', '/TvHeadendApi/Connections'],
  ['GET', '/TvHeadendApi/Inputs'],
  ['GET', '/TvHeadendApi/Subscriptions'],
  ['GET', '/TvHeadendApi/Health'],
  ['POST', '/TvHeadendApi/Health/Check'], // active check — anonymous probe only
  // StatisticsController
  ['GET', '/TvHeadendApi/Statistics'],
  ['DELETE', '/TvHeadendApi/Statistics'], // MUTATING — anonymous probe only!
  // StreamingProfileController
  ['GET', '/TvHeadendApi/StreamingProfiles/Resolve'],
  ['GET', '/TvHeadendApi/StreamingProfiles/Discovered'],
  ['GET', '/TvHeadendApi/StreamingProfiles/Validate'],
  ['POST', '/TvHeadendApi/StreamingProfiles/RefreshCache'], // MUTATING — anonymous probe only!
  ['GET', '/TvHeadendApi/StreamingProfiles/Channels'],
  ['GET', '/TvHeadendApi/StreamingProfiles/ChannelGroups'],
];

test.describe('Guide data and admin authorization', () => {
  let api, anon, token, userId;

  test.beforeAll(async () => {
    api = await request.newContext({ baseURL: JF.CONFIG.baseURL, ignoreHTTPSErrors: true });
    // A context that never carries ANY authentication header.
    anon = await request.newContext({ baseURL: JF.CONFIG.baseURL, ignoreHTTPSErrors: true });
    ({ token, userId } = await JF.authenticate(api));
    expect(await JF.ensureLiveTvChannels(api, token, userId), 'Live TV channels available').toBeTruthy();
  });

  test.afterAll(async () => {
    // Read-only spec, but prove nothing (e.g. an accidentally authorized mutation) changed
    // the canonical configuration or broke the plugin/backend link.
    const pluginId = await JF.getPluginId(api, token);
    const cfg = await JF.readPluginConfig(api, token, pluginId);
    expect(JF.canonicalConfigDeviations(cfg), 'config still canonical').toEqual([]);
    await JF.waitForDiagnoseChannels(api, token);
    await anon.dispose();
    await api.dispose();
  });

  test('EPG: programs exist across the channel lineup with plausible data', async () => {
    test.setTimeout(120000);
    const q = new URLSearchParams({
      userId,
      minStartDate: new Date().toISOString(),
      maxStartDate: new Date(Date.now() + 6 * 3600000).toISOString(),
      limit: '1000',
      sortBy: 'StartDate',
    });
    const res = await api.get(`${JF.CONFIG.baseURL}/LiveTv/Programs?${q}`, { headers: JF.authHeaders(token) });
    expect(res.ok(), `Programs status ${res.status()}`).toBeTruthy();
    const items = (await res.json()).Items || [];
    console.log(`[14|epg] ${items.length} programs in the next 6h window`);
    expect(items.length, 'a real EPG window').toBeGreaterThan(100);

    const channelsWithEpg = new Set();
    for (const p of items) {
      expect(p.Name && p.Name.trim().length, `program ${p.Id} has a non-empty title`).toBeTruthy();
      const start = new Date(p.StartDate).getTime();
      const end = new Date(p.EndDate).getTime();
      expect(Number.isFinite(start) && Number.isFinite(end), `program "${p.Name}" has parseable dates`).toBeTruthy();
      expect(end, `program "${p.Name}" ends after it starts`).toBeGreaterThan(start);
      expect(end - start, `program "${p.Name}" has a plausible duration (<= 24h)`).toBeLessThanOrEqual(24 * 3600000);
      channelsWithEpg.add(p.ChannelId);
    }
    console.log(`[14|epg] ${channelsWithEpg.size} distinct channels carry EPG`);
    expect(channelsWithEpg.size, 'EPG coverage across at least half the lineup').toBeGreaterThanOrEqual(50);

    // At least a broad slice of the lineup is airing something right now.
    const airingRes = await api.get(`${JF.CONFIG.baseURL}/LiveTv/Programs?userId=${userId}&isAiring=true&limit=500`, { headers: JF.authHeaders(token) });
    expect(airingRes.ok()).toBeTruthy();
    const airing = (await airingRes.json()).Items || [];
    console.log(`[14|epg] ${airing.length} programs airing now`);
    expect(airing.length, 'channels airing programs right now').toBeGreaterThanOrEqual(50);
  });

  test('channel groups (tags) are served', async () => {
    const res = await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/StreamingProfiles/ChannelGroups`, { headers: JF.authHeaders(token) });
    expect(res.ok(), `ChannelGroups status ${res.status()}`).toBeTruthy();
    const groups = await res.json();
    console.log(`[14|epg] ${groups.length} channel groups: ${groups.map((g) => g.Name).join(', ')}`);
    expect(Array.isArray(groups) && groups.length, 'at least one channel group/tag').toBeTruthy();
    for (const g of groups) {
      expect(g.Id && g.Id.length, 'group has an id').toBeTruthy();
      expect(g.Name && g.Name.trim().length, 'group has a name').toBeTruthy();
    }
  });

  test('authorization: every admin endpoint rejects anonymous requests with 401', async () => {
    test.setTimeout(120000);
    const failures = [];
    for (const [method, path, accepted = [401]] of ADMIN_ENDPOINTS) {
      const res = await anon.fetch(`${JF.CONFIG.baseURL}${path}`, { method });
      if (!accepted.includes(res.status())) failures.push(`${method} ${path} -> ${res.status()} (expected ${accepted.join('/')})`);
    }
    console.log(`[14|authz] ${ADMIN_ENDPOINTS.length} admin endpoints probed anonymously`);
    expect(failures, 'no admin endpoint reachable without elevation').toEqual([]);
  });

  test('authorization: relay endpoints are anonymous by design but token-gated', async () => {
    // /status is the deliberate anonymous health probe.
    const status = await anon.get(`${JF.CONFIG.baseURL}/api/tvheadend/status`);
    expect(status.status(), 'relay status endpoint is anonymous by design').toBe(200);

    // The token-secured stream/image routes must be CLOSED without a valid relay token
    // (EnableRelayTokenSecurity=true is a canonical-config invariant of this suite).
    const relayStream = await JF.fetchStreamBytes(`${JF.CONFIG.baseURL}/api/tvheadend/relay/stream/e2e-authz-probe`, { minBytes: 1, timeoutMs: 8000 });
    expect(relayStream.status, 'tokenized relay stream without token').toBe(401);
    const relayImage = await anon.get(`${JF.CONFIG.baseURL}/api/tvheadend/relay/images/static/img/logo.png`);
    expect(relayImage.status(), 'tokenized relay image without token').toBe(401);
    const openStream = await JF.fetchStreamBytes(`${JF.CONFIG.baseURL}/api/tvheadend/stream/e2e-authz-probe?profile=pass`, { minBytes: 1, timeoutMs: 8000 });
    expect(openStream.status, 'open stream endpoint without token (security on)').toBe(401);
    // The admin image relay requires a Jellyfin session (LiveTvAccess policy).
    const image = await anon.get(`${JF.CONFIG.baseURL}/api/tvheadend/images/static/img/logo.png`);
    expect(image.status(), 'image relay without Jellyfin auth').toBe(401);
    console.log('[14|authz] relay surface: status=200 anon, stream/image routes token-gated');
  });

  test('authenticated admin GETs serve well-formed payloads', async () => {
    const health = await (await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/Health`, { headers: JF.authHeaders(token) })).json();
    expect(health.Status, 'Health.Status present').toBeTruthy();
    expect(health.CircuitState, 'Health.CircuitState present').toBeTruthy();

    const statsRes = await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/Statistics?days=7`, { headers: JF.authHeaders(token) });
    expect(statsRes.ok(), `Statistics status ${statsRes.status()}`).toBeTruthy();
    const stats = await statsRes.json();
    expect(Array.isArray(stats.Sessions), 'Statistics.Sessions is an array').toBeTruthy();

    const options = await (await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/ProfileOptions`, { headers: JF.authHeaders(token) })).json();
    expect(options.StreamingProfiles, 'streaming profiles include the managed profile').toContain('jellyfin');
    expect(options.RecordingProfiles, 'recording profiles include the test DVR profile').toContain('test-dvr');
    console.log(`[14|admin] Health=${health.Status}, ${stats.Sessions.length} stat sessions, ${options.StreamingProfiles.length} streaming profiles`);
  });
});
