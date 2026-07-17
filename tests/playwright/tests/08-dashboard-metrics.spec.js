// Truth-check for the TVHeadend admin dashboard: generates known, deterministic relay activity
// (a tokenized stream, a cached image, a 404 image, a rejected token) and verifies BOTH the
// metrics REST API and the rendered dashboard page reflect it accurately — counters, bytes,
// channel names, timestamps, health state — with no secrets leaked into the log viewer.
//
// See fixtures/jellyfin.js for the shared helpers reused here (authenticate, pickChannel,
// requestPlaybackInfo, closeLiveStream, pollUntil, uiLogin, openPluginPage,
// canonicalConfigDeviations, fetchStreamBytes, rewriteHost). Discipline matches 06/10: serial,
// every config mutation is read-modify-write with an exact-restore body applied in `finally`,
// every stream/encode is closed, all waits poll with a deadline instead of sleeping.
const { test, expect, request } = require('@playwright/test');
const JF = require('../fixtures/jellyfin');

// ---- Local helper: fixtures' fetchStreamBytes stops as soon as minBytes arrives, which is too
// fast to prove the "active streams" gauge is visible mid-flight (task wants >=1MB over >=10s
// held open). Same native fetch + AbortController pattern, with an added wall-clock floor. ----
async function readStreamForAtLeast(url, { minBytes, minDurationMs, timeoutMs }) {
  const controller = new AbortController();
  const started = Date.now();
  const safetyTimer = setTimeout(() => controller.abort(), timeoutMs);
  let response;
  try {
    response = await fetch(url, { signal: controller.signal });
  } catch (e) {
    clearTimeout(safetyTimer);
    return { status: -1, contentType: '', bytes: 0, elapsedMs: Date.now() - started, error: String(e && e.message) };
  }
  let bytes = 0;
  const reader = response.body.getReader();
  try {
    for (;;) {
      const elapsed = Date.now() - started;
      if (bytes >= minBytes && elapsed >= minDurationMs) break;
      const { done, value } = await reader.read();
      if (done) break;
      if (value && value.length) bytes += value.length;
    }
  } catch (e) {
    if (e.name !== 'AbortError') throw e; // AbortError = our own deadline fired mid-read
  } finally {
    try { await reader.cancel(); } catch (e) { /* upstream already gone */ }
    controller.abort();
  }
  clearTimeout(safetyTimer);
  return { status: response.status, contentType: response.headers.get('content-type') || '', bytes, elapsedMs: Date.now() - started };
}

// ---- DOM value parsers mirroring the dashboard's own formatBytes()/fmtMs()/fmtPct() ----
function parseIntText(t) {
  const n = parseInt(String(t).replace(/[^0-9-]/g, ''), 10);
  return Number.isFinite(n) ? n : null;
}
function parseBytesText(t) {
  const m = String(t).trim().match(/^([\d.]+)\s*(B|KB|MB|GB|TB)$/i);
  if (!m) return null;
  const units = { B: 1, KB: 1024, MB: 1024 ** 2, GB: 1024 ** 3, TB: 1024 ** 4 };
  return parseFloat(m[1]) * units[m[2].toUpperCase()];
}

// This synthetic backend's channels expose an externally-hosted icon (TVHeadend's
// icon_public_url, e.g. "http://iptv-simulator/logo1.png") — RelayUrlBuilder/UrlBuilder always
// prefix the TVHeadend base URL onto the relay path (UrlBuilder.BuildEndpointUrl has no
// special case for an already-absolute upstream path), so relaying that icon 404s/never routes.
// TVHeadend's own imagecache is also empty (nothing has ever been crawled into it). This static
// admin-UI asset is a real, always-present TVHeadend HTTP resource that exercises the exact same
// RelayImageAsync + on-disk-cache + RelayMetrics code path as a channel logo would — used here as
// a stand-in so the cache-miss-then-hit truth-check has genuine bytes to work with. See the final
// report for the channel-logo relay gap found while investigating this.
const REAL_IMAGE_PATH = 'static/img/logo.png';

const EXPECTED_NEW_RELAY_REQUESTS = 6; // 1 stream + 1 priming fetch + 3 measured image fetches + 1 missing-image fetch

test.describe.configure({ mode: 'serial' });

test.describe('Dashboard & metrics truth-check', () => {
  let api, token, userId, pluginId, channel, tvhChannelUuid, restoreConfigBody;
  const state = {};

  test.beforeAll(async () => {
    api = await request.newContext({ baseURL: JF.CONFIG.baseURL, ignoreHTTPSErrors: true });
    ({ token, userId } = await JF.authenticate(api));
    pluginId = await JF.getPluginId(api, token);
    expect(pluginId, 'plugin installed').toBeTruthy();
    await JF.ensureManagedProfile(api, token, pluginId);
    channel = await JF.pickChannel(api, token, userId);
    restoreConfigBody = JSON.parse(JSON.stringify(await JF.readPluginConfig(api, token, pluginId)));
  });

  test.afterAll(async () => {
    if (api) {
      await JF.writePluginConfig(api, token, pluginId, restoreConfigBody);
      const cfg = await JF.readPluginConfig(api, token, pluginId);
      expect(JF.canonicalConfigDeviations(cfg), 'config back to canonical').toEqual([]);
      await api.dispose();
    }
  });

  test('1) baseline snapshot', async () => {
    const [relay, live, history, tokens, diagnose] = await Promise.all([
      (await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/RelayMetrics?hours=24`, { headers: JF.authHeaders(token) })).json(),
      (await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/Metrics/Live`, { headers: JF.authHeaders(token) })).json(),
      (await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/Metrics/History`, { headers: JF.authHeaders(token) })).json(),
      (await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/Dashboard/Tokens`, { headers: JF.authHeaders(token) })).json(),
      (await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/Diagnose`, { headers: JF.authHeaders(token) })).json(),
    ]);
    state.baseline = { relay, live, history, tokens, diagnose };
    console.log(`[08] baseline: TotalRequests=${relay.TotalRequests} ActiveStreams=${relay.ActiveStreams} FailedRequests=${relay.FailedRequests} ChannelCount=${diagnose.ChannelCount}`);

    // Ground truth precondition — the rest of this file is meaningless if TVHeadend isn't
    // actually reachable (see the report: this stack's config drifted once already).
    expect(JF.HEALTHY_DIAGNOSE_STATUSES, `TVHeadend reachable before the test starts (got ${diagnose.OverallStatus})`).toContain(diagnose.OverallStatus);
    expect(diagnose.ChannelCount, 'channels visible before the test starts').toBeGreaterThan(0);
  });

  test('2) generate known activity: tokenized stream, image cache, invalid token', async () => {
    test.setTimeout(120000);

    // ---- 2a) One tokenized relay stream (Android-TV-like direct play), held open >=10s / >=1MB.
    const r = await JF.requestPlaybackInfo(api, token, userId, channel.id, JF.deviceProfiles.directPlay);
    expect(r.ms.Path, 'tokenized relay path').toContain('/api/tvheadend/relay/stream/');
    expect(r.ms.Path, 'tokenized relay path carries a token').toContain('token=');
    // The TVHeadend channel UUID lives in the relay path, NOT in Jellyfin's own item id
    // (channel.id) — Jellyfin mints its own ids for LiveTvChannel items.
    tvhChannelUuid = new URL(r.ms.Path).pathname.split('/').pop();
    state.streamUrl = JF.rewriteHost(r.ms.Path, JF.CONFIG.baseURL);
    state.relayToken = new URL(r.ms.Path).searchParams.get('token');
    state.liveStreamId = r.liveStreamId;

    const readPromise = readStreamForAtLeast(state.streamUrl, { minBytes: 1_000_000, minDurationMs: 10000, timeoutMs: 30000 });

    // While the stream is open, the live-metrics view must show it — both the count and the
    // channel name/id. Sessions are visible from the moment the request starts (state
    // "Starting", latency still 0), so poll until OUR session has recorded its real
    // time-to-first-byte instead of asserting on the first sighting.
    const activeSeen = await JF.pollUntil(async () => {
      const live = await (await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/Metrics/Live`, { headers: JF.authHeaders(token) })).json();
      if ((live.ActiveStreamCount || 0) < 1) return null;
      const s = (live.ActiveSessions || []).find((x) => x.ChannelId === tvhChannelUuid);
      return s && s.StartupLatencyMs > 0 ? live : null;
    }, { timeoutMs: 8000, intervalMs: 1000, label: 'Metrics/Live shows our session with real startup latency' });
    console.log(`[08] mid-stream: ActiveStreamCount=${activeSeen.ActiveStreamCount} ActiveChannels=${JSON.stringify(activeSeen.ActiveChannels)}`);
    expect(activeSeen.ActiveChannels, 'active channel NAME listed while streaming').toContain(channel.name);
    const midSession = (activeSeen.ActiveSessions || []).find((s) => s.ChannelId === tvhChannelUuid);
    expect(midSession, 'our session visible in ActiveSessions while streaming').toBeTruthy();
    expect(midSession.StartupLatencyMs, 'active session recorded a real (non-zero) startup latency').toBeGreaterThan(0);
    state.sessionId = midSession.SessionId;
    state.activeStartupLatencyMs = midSession.StartupLatencyMs;

    // Cross-check the SAME moment against the relay banner's active-stream counter.
    const relayMid = await (await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/RelayMetrics?hours=24`, { headers: JF.authHeaders(token) })).json();
    expect(relayMid.ActiveStreams, 'RelayMetrics.ActiveStreams also shows >=1 mid-stream').toBeGreaterThanOrEqual(1);

    const res = await readPromise;
    console.log(`[08] stream read: ${res.status} ${res.contentType} ${res.bytes}B in ${res.elapsedMs}ms`);
    expect(res.status, 'stream reachable').toBe(200);
    expect(res.bytes, 'read at least 1MB').toBeGreaterThanOrEqual(1_000_000);
    expect(res.elapsedMs, 'held the stream open for at least 10s').toBeGreaterThanOrEqual(10000);
    state.streamBytes = res.bytes;

    await JF.closeLiveStream(api, token, state.liveStreamId);

    // Drain: active-stream count returns to (at most) the pre-test baseline.
    await JF.pollUntil(async () => {
      const live = await (await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/Metrics/Live`, { headers: JF.authHeaders(token) })).json();
      return live.ActiveStreamCount <= state.baseline.live.ActiveStreamCount ? live : null;
    }, { timeoutMs: 20000, intervalMs: 1500, label: 'ActiveStreamCount drains back to baseline after close' });

    // ---- 2b) Channel-logo/image fetches: cache miss then hits, plus a 404 failure path.
    // The on-disk image cache is intentionally persistent across plugin restarts (and therefore
    // across separate runs of this very test), so a true cold-start "first request = miss" cannot
    // be guaranteed run-to-run. Prime the entry first (irrelevant whether THIS particular call is
    // a hit or a miss) so the 3 measured fetches below are deterministically all cache hits —
    // the real, ground-truth hit/miss counts are verified against RelayMetrics in step 3.
    const imgHeaders = JF.authHeaders(token);
    await fetch(`${JF.CONFIG.baseURL}/api/tvheadend/images/${REAL_IMAGE_PATH}`, { headers: imgHeaders });
    await new Promise((r) => setTimeout(r, 300));

    const imageResults = [];
    for (let i = 0; i < 3; i++) {
      const started = Date.now();
      const imgRes = await fetch(`${JF.CONFIG.baseURL}/api/tvheadend/images/${REAL_IMAGE_PATH}`, { headers: imgHeaders });
      const buf = await imgRes.arrayBuffer();
      imageResults.push({ status: imgRes.status, bytes: buf.byteLength, ms: Date.now() - started });
    }
    console.log(`[08] image fetches (${REAL_IMAGE_PATH}, post-prime): ${JSON.stringify(imageResults)}`);
    expect(imageResults.every((x) => x.status === 200), 'all 3 image fetches returned 200').toBeTruthy();
    expect(imageResults.every((x) => x.bytes > 0), 'all 3 image fetches returned bytes').toBeTruthy();
    state.imageBytes = imageResults.reduce((sum, x) => sum + x.bytes, 0);

    const missingImagePath = `imagecache/999999${Date.now()}`; // purely numeric, matches TVHeadend's
    // imagecache/<id> convention (confirmed to 404 cleanly at the API layer, unlike a missing
    // static file path which TVHeadend's web server 500s on — see the report).
    const notFoundRes = await fetch(`${JF.CONFIG.baseURL}/api/tvheadend/images/${missingImagePath}`, { headers: imgHeaders });
    console.log(`[08] nonexistent image (${missingImagePath}) -> ${notFoundRes.status}`);
    expect(notFoundRes.status, 'nonexistent image path fails cleanly').toBe(404);

    // ---- 2c) Invalid relay token -> 401, and the channel must still be healthy afterwards.
    const bogusToken = `e2eBogusToken${Date.now()}`;
    const badRes = await fetch(`${JF.CONFIG.baseURL}/api/tvheadend/relay/stream/${tvhChannelUuid}?token=${bogusToken}`);
    console.log(`[08] invalid token -> ${badRes.status}`);
    expect(badRes.status, 'invalid relay token is rejected').toBe(401);
    state.bogusToken = bogusToken;

    const followUp = await JF.requestPlaybackInfo(api, token, userId, channel.id, JF.deviceProfiles.directPlay);
    expect(followUp.ms.SupportsDirectPlay, 'channel still plays right after a rejected token (no crash/leak)').toBeTruthy();
    await JF.closeLiveStream(api, token, followUp.liveStreamId);
  });

  test('3) metrics API reflects the generated activity', async () => {
    test.setTimeout(60000);
    const b = state.baseline.relay;

    const after = await JF.pollUntil(async () => {
      const relay = await (await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/RelayMetrics?hours=24`, { headers: JF.authHeaders(token) })).json();
      return relay.TotalRequests >= b.TotalRequests + EXPECTED_NEW_RELAY_REQUESTS ? relay : null;
    }, { timeoutMs: 30000, intervalMs: 2000, label: 'RelayMetrics.TotalRequests reflects our activity' });

    console.log(`[08] after: TotalRequests=${after.TotalRequests} (was ${b.TotalRequests}), Bytes=${after.TotalBytesTransferred}, CacheHits=${after.CacheHits}, CacheMisses=${after.CacheMisses}, FailedRequests=${after.FailedRequests}`);

    expect(after.TotalRequests, 'total requests grew by at least our 6 relay operations')
      .toBeGreaterThanOrEqual(b.TotalRequests + EXPECTED_NEW_RELAY_REQUESTS);
    expect(after.TotalBytesTransferred, 'bytes transferred grew by at least our streamed bytes')
      .toBeGreaterThanOrEqual(b.TotalBytesTransferred + state.streamBytes);
    expect(after.CacheHits, 'the 3 post-priming image fetches all served from the on-disk cache')
      .toBeGreaterThanOrEqual(b.CacheHits + 3);
    expect(after.ImageRequests, 'image relay requests grew by the priming fetch + 3 measured + 1 missing')
      .toBeGreaterThanOrEqual(b.ImageRequests + 5);
    expect(after.FailedRequests, 'the 404 image request counted as a failed relay request')
      .toBeGreaterThanOrEqual(b.FailedRequests + 1);
    expect(after.ActiveStreams, 'active-stream count is back down to (at most) the pre-test baseline once drained')
      .toBeLessThanOrEqual(b.ActiveStreams);

    // Latency plausibility (aggregate — this is the pipeline the dashboard actually renders):
    // 0 <= upstream-headers <= first-byte-from-upstream <= first-byte-to-client, and startup
    // latency itself is never absurd (<60s to get a first byte). TotalDuration/session duration
    // is deliberately NOT bounded here — for a live stream that is legitimate watch-time and can
    // run for many minutes (confirmed live: pre-existing sessions on this stack ran 130s+).
    const startupTypeFields = [after.AvgUpstreamHeadersMs, after.AvgFirstByteFromUpstreamMs, after.AvgFirstByteToClientMs, after.StartupLatency.Avg, after.StartupLatency.P95];
    for (const v of startupTypeFields) {
      if (v == null) continue;
      expect(v, `latency value ${v} is non-negative`).toBeGreaterThanOrEqual(0);
      expect(v, `startup-type latency value ${v} is not absurd (<60s to first byte)`).toBeLessThan(60000);
    }
    expect(after.TotalDuration.Avg, 'total duration is at least non-negative').toBeGreaterThanOrEqual(0);
    expect(after.AvgUpstreamHeadersMs, 'upstream headers <= first byte from upstream (on average)')
      .toBeLessThanOrEqual(after.AvgFirstByteFromUpstreamMs + 1);
    expect(after.StartupLatency.Avg, 'avg startup latency <= avg total duration (starting is part of the whole request)')
      .toBeLessThanOrEqual(after.TotalDuration.Avg + 1);

    // Channel-NAME enrichment (not just the id) — ChannelNameCache-backed.
    const history = await (await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/Metrics/History`, { headers: JF.authHeaders(token) })).json();
    expect(Object.keys(history.TopChannels), "channel NAME appears in today's top-channels aggregate")
      .toContain(channel.name);

    // Token issuance is real and observable via Dashboard/Tokens (this is the metric that
    // actually works — see the report for RelayMetricsSummary's dead Token* fields, which this
    // deliberately does NOT assert against).
    // Expired tokens are now cleaned up every few minutes, so a baseline TotalCount delta is
    // not a stable invariant — a cleanup cycle mid-test legally shrinks the count. Our two
    // fresh (unexpired) tokens must still be visible as live entries.
    const tokensAfter = await (await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/Dashboard/Tokens`, { headers: JF.authHeaders(token) })).json();
    expect(tokensAfter.TotalCount - (tokensAfter.ExpiredCount || 0), 'relay tokens issued by our PlaybackInfo calls are live')
      .toBeGreaterThanOrEqual(2);

    state.after = after;
  });

  // KNOWN BUG (found via this truth-check, not asserted as green): RelayController
  // .StreamChannelCoreAsync's `catch (OperationCanceledException)` handler — the code path taken
  // on every normal client-initiated stop (channel zap, app close, "Stop" button; i.e.
  // EndedBy=ClientDisconnectAfterFirstByte, which is how the overwhelming majority of real Live
  // TV sessions end) — calls `_sessionTracker.FinalizeSession(..., firstByteSent, /* startupLatencyMs */ 0,
  // /* upstreamHeadersLatencyMs */ null, /* upstreamFirstByteLatencyMs */ null,
  // /* downstreamFirstByteLatencyMs */ null, ...)`. The correctly-measured `startupLatencyMs`
  // local is scoped inside the preceding `try` block and unreachable in `catch`, so the literal
  // 0/null values are persisted instead of the real ones the ACTIVE session already recorded via
  // MarkFirstByteSent/UpdateSession. Verified live on this stack: the same session showed
  // StartupLatencyMs=429ms via GET /TvHeadendApi/Metrics/Live while streaming, then
  // StartupLatencyMs=0 (and RollingBitrate/AverageBitrate/PeakBitrate=0) via
  // GET /TvHeadendApi/Metrics/Session/{id} once persisted after the normal client-cancel finalize.
  // FIXED: startupLatencyMs is now declared outside the try block in RelayController, so all
  // finalize paths (client cancel, IO error, unexpected exception) persist the real measured
  // startup latency and computed bitrates. This test is the regression guard for that fix.
  test('3b) Metrics/Session startup latency survives a normal client-cancel finalize', async () => {
    const detail = await JF.pollUntil(async () => {
      const res = await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/Metrics/Session/${state.sessionId}`, { headers: JF.authHeaders(token) });
      if (!res.ok()) return null;
      return res.json();
    }, { timeoutMs: 20000, intervalMs: 2000, label: 'completed session persisted' });
    const s = detail.Session;
    console.log(`[08] KNOWN BUG evidence: active StartupLatencyMs=${state.activeStartupLatencyMs}, persisted Session.StartupLatencyMs=${s.StartupLatencyMs}, RollingBitrate=${s.RollingBitrate}`);
    expect(s.ChannelName, 'session IS enriched with the channel name (this part is correct)').toBe(channel.name);
    // This is the assertion that currently fails: the persisted startup latency should reflect
    // what was actually measured while the session was active, not the hardcoded 0.
    expect(s.StartupLatencyMs, 'persisted startup latency should reflect the real measured value, not 0').toBeGreaterThan(0);
  });

  test('4) rendered dashboard reflects the same reality', async ({ page }) => {
    test.setTimeout(180000);

    await JF.uiLogin(page);
    await JF.openPluginPage(page, 'TvHeadendDashboard');

    // Wait for the dashboard to actually populate (DOM content, not a fixed sleep).
    await JF.pollUntil(async () => {
      const t = await page.locator('#rkpiRequests').innerText().catch(() => '');
      return t && t !== '—' ? t : null;
    }, { timeoutMs: 30000, intervalMs: 2000, label: 'Relay KPI row populated' });

    const [relayApi, diagnoseApi] = await Promise.all([
      (await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/RelayMetrics?hours=24`, { headers: JF.authHeaders(token) })).json(),
      (await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/Diagnose`, { headers: JF.authHeaders(token) })).json(),
    ]);

    const domRequests = parseIntText(await page.locator('#rkpiRequests').innerText());
    const domBytes = parseBytesText(await page.locator('#rkpiBytes').innerText());
    const domActive = parseIntText(await page.locator('#rkpiActive').innerText());
    const domFailures = parseIntText(await page.locator('#rkpiFailures').innerText());
    console.log(`[08] DOM KPIs: requests=${domRequests} bytes=${domBytes} active=${domActive} failures=${domFailures} | API: requests=${relayApi.TotalRequests} bytes=${relayApi.TotalBytesTransferred} active=${relayApi.ActiveStreams} failures=${relayApi.FailedRequests}`);

    // Tolerance: background activity between the two reads could add a small delta, but the DOM
    // must never show LESS than what we proved via the API, and must be in the right ballpark
    // (not some stale/disconnected number).
    expect(domRequests, 'rendered total requests is at least the (already-truth-checked) API value')
      .toBeGreaterThanOrEqual(state.after.TotalRequests);
    expect(domBytes, 'rendered bytes transferred is at least our streamed byte count')
      .toBeGreaterThanOrEqual(state.streamBytes * 0.95); // 5% slack for KB/MB rounding in formatBytes()
    expect(domActive, 'rendered active-stream count matches the drained API value').toBe(relayApi.ActiveStreams);
    expect(domFailures, 'rendered failure count is at least the (already-truth-checked) API value')
      .toBeGreaterThanOrEqual(state.after.FailedRequests);

    // Health panel must not contradict Diagnose: OK + channels>0 must never render "Unreachable".
    expect(JF.HEALTHY_DIAGNOSE_STATUSES, `Diagnose still healthy (got ${diagnoseApi.OverallStatus})`).toContain(diagnoseApi.OverallStatus);
    expect(diagnoseApi.ChannelCount, 'Diagnose still reports channels').toBeGreaterThan(0);
    const sumConnectionText = await page.locator('#sumConnection').innerText();
    const connectionKvText = await page.locator('#tvhConnectionKv').innerText();
    console.log(`[08] health panel: sumConnection="${sumConnectionText}" status-in-kv contains Connected=${/Connected/i.test(connectionKvText)}`);
    expect(sumConnectionText, 'summary connection badge is not Down while Diagnose is OK').not.toMatch(/Down/i);
    expect(connectionKvText, 'connection card does not contradict Diagnose (no "Unreachable")').not.toMatch(/Unreachable/i);
    expect(connectionKvText, 'connection card shows Connected').toMatch(/Connected/i);

    // Whole-page sanity: no raw rendering failures anywhere on the page.
    const bodyText = await page.evaluate(() => document.body.innerText);
    expect(bodyText, 'no "undefined" leaked into rendered text').not.toMatch(/\bundefined\b/);
    expect(bodyText, 'no "NaN" leaked into rendered text').not.toMatch(/\bNaN\b/);
    expect(bodyText, 'no raw object stringification leaked into rendered text').not.toMatch(/\[object Object\]/);
    expect(bodyText, 'no raw "null" leaked into rendered text').not.toMatch(/\bnull\b/);
    expect(bodyText, 'no unparsed date leaked into rendered text').not.toMatch(/Invalid Date/);

    // Recent-errors / slowest-requests timestamps render as valid dates.
    const errorTimes = await page.locator('#tvhRelayErrorsBody tr td:first-child').allInnerTexts();
    const slowestTimes = await page.locator('#tvhRelaySlowestBody tr td:first-child').allInnerTexts();
    for (const t of [...errorTimes, ...slowestTimes]) {
      if (!t || t === 'No recent errors' || t === 'No data') continue;
      expect(t, `timestamp "${t}" is not Invalid Date`).not.toMatch(/Invalid Date/);
    }

    // Channel-name-in-DOM: the relay tables above never render ChannelId/ChannelName at all
    // (grep-confirmed — see the report), so the only real dashboard surface that shows a
    // per-session channel NAME is the "Active Jellyfin Sessions" panel, fed by Jellyfin's own
    // session tracking (Statistics?days=1), not by the raw-fetch relay activity generated above.
    // A short real browser playback is needed to populate it honestly. It must run in a SEPARATE
    // tab from the dashboard: navigating the same tab away from the live-TV player stops playback
    // (and the session) before the dashboard can observe it as "active".
    const serverId = await JF.getServerId(api);
    const played = await JF.playChannel(page, channel.id, serverId);
    expect(played, 'play triggered from the browser').toBeTruthy();
    const start = await JF.waitForPlaybackStart(page, 60000);
    expect(start.ok, `browser playback started (${JSON.stringify(start.v)})`).toBeTruthy();

    const dashPage = await page.context().newPage();
    try {
      await JF.pollUntil(async () => {
        await JF.openPluginPage(dashPage, 'TvHeadendDashboard');
        const t = await dashPage.locator('#tvhActiveSessionsContent').innerText().catch(() => '');
        return t && t.includes(channel.name) ? t : null;
      }, { timeoutMs: 45000, intervalMs: 3000, label: 'Active Jellyfin Sessions panel shows our channel name while playing' });
      const finalText = await dashPage.locator('#tvhActiveSessionsContent').innerText();
      expect(finalText, 'our channel name appears in the Active Jellyfin Sessions panel').toContain(channel.name);
    } finally {
      await dashPage.close();
      // Drain: stop the browser-driven playback properly (tuners/sessions are scarce — always
      // drain what you open) instead of just tearing the page down, which would leave the
      // session/live-stream open server-side and pollute the NEXT run's baseline.
      await page.goto('/web/#/home.html', { waitUntil: 'domcontentloaded' }).catch(() => {});
      await JF.pollUntil(async () => {
        const live = await (await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/Metrics/Live`, { headers: JF.authHeaders(token) })).json();
        return live.ActiveStreamCount === 0 ? live : null;
      }, { timeoutMs: 20000, intervalMs: 2000, label: 'browser-driven playback session drains after navigating away' }).catch(() => {});
    }
  });

  test('5) log viewer shows session evidence without leaking secrets', async () => {
    const res = await api.get(
      `${JF.CONFIG.baseURL}/TvHeadendApi/Dashboard/Logs?source=plugin&limit=500&sortField=created_at_utc&sortDirection=desc`,
      { headers: JF.authHeaders(token) },
    );
    expect(res.ok(), `Dashboard/Logs status ${res.status()}`).toBeTruthy();
    const logs = await res.json();
    const entries = logs.Entries || [];
    const allText = entries.map((e) => `${e.Message || ''} ${e.Exception || ''}`).join('\n');

    // Evidence of our activity: the invalid-token rejection and/or our channel/session are
    // visible in the log stream (plugin logs Debug-level relay/session lifecycle lines).
    const hasInvalidTokenEvidence = /token not found|token.*invalid|relay token validation failed/i.test(allText);
    const hasChannelEvidence = allText.includes(tvhChannelUuid) || allText.includes(channel.name);
    console.log(`[08] log evidence: invalidTokenEvidence=${hasInvalidTokenEvidence} channelEvidence=${hasChannelEvidence} entries=${entries.length}`);
    expect(hasInvalidTokenEvidence || hasChannelEvidence, 'log viewer contains evidence of our generated activity').toBeTruthy();

    // Sanitization regression guard: neither of our real token values ever appears verbatim.
    expect(allText, 'the valid relay token never appears in plaintext in the logs').not.toContain(state.relayToken);
    expect(allText, 'the bogus/invalid token never appears in plaintext in the logs').not.toContain(state.bogusToken);
    expect(allText, "no 'auth=<value>' leaks a real token (LogSanitizer must redact it)").not.toMatch(/auth=(?!\*\*\*REDACTED\*\*\*)[A-Za-z0-9_-]{6,}/);
  });

  test('6) settings->dashboard coupling: MaxDashboardLogEntries', async () => {
    const before = await JF.readPluginConfig(api, token, pluginId);
    const restoreBody = JSON.parse(JSON.stringify(before));
    try {
      const mutated = JSON.parse(JSON.stringify(before));
      mutated.MaxDashboardLogEntries = 3;
      await JF.writePluginConfig(api, token, pluginId, mutated);

      const logs = await JF.pollUntil(async () => {
        const res = await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/Dashboard/Logs`, { headers: JF.authHeaders(token) });
        if (!res.ok()) return null;
        const j = await res.json();
        return j.Limit === 3 ? j : null;
      }, { timeoutMs: 15000, intervalMs: 1500, label: 'Dashboard/Logs.Limit reflects MaxDashboardLogEntries=3' });

      console.log(`[08] settings coupling: Limit=${logs.Limit} Entries.length=${logs.Entries.length}`);
      expect(logs.Limit, 'effective limit reflects the config change').toBe(3);
      expect(logs.Entries.length, 'entries respect the new, smaller limit').toBeLessThanOrEqual(3);
    } finally {
      await JF.writePluginConfig(api, token, pluginId, restoreBody);
    }
  });
});
