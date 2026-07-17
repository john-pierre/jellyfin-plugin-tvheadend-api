// TVHeadend failure paths: what the plugin does while its backend is unreachable or rejects
// the credentials — and that it recovers fully afterwards (ResilienceHandler circuit breaker:
// 5 failures -> open, 30s cool-down; see Service/Resilience/).
//
// The backend is "broken" purely through the plugin configuration (Port 9909 on the same
// 'tvheadend' host -> immediate connection refused, no timeout hangs; credentials swapped ->
// TVHeadend digest auth fails with 403). Every mutation is guarded by try/finally with an
// exact restore of the pre-test configuration body, so an aborted test cannot strand the
// suite on a dead backend. This file is named to sort AFTER the functional specs on purpose.
//
// Failure-shape expectations were verified against the live stack before writing this spec:
// - Diagnose: 200 with OverallStatus=ERROR, Connection="Cannot reach … Connection refused" /
//   "… 403 (Forbidden)", ChannelCount=0.
// - Dashboard: 200 with IsReachable=false (also for auth failures) and IsAuthenticated=false.
// - Health: CircuitState=Open / Status=CircuitOpen after >=5 consecutive failures.
// - PlaybackInfo: 200 (NO 500 crash) — the relay URL is built without contacting TVHeadend.
// - Relay stream: fast clean 502/503, no bytes, no hang.
const { test, expect, request } = require('@playwright/test');
const JF = require('../fixtures/jellyfin');

test.describe.configure({ mode: 'serial' });

test.describe('Resilience: TVHeadend outage and recovery', () => {
  let api, token, userId, pluginId, channel, pristine;

  async function getJson(path) {
    const res = await api.get(`${JF.CONFIG.baseURL}${path}`, { headers: JF.authHeaders(token) });
    expect(res.ok(), `GET ${path} status ${res.status()}`).toBeTruthy();
    return res.json();
  }

  // Full recovery proof: Diagnose healthy, circuit closed, and a real stream delivers bytes.
  async function assertRecovered(tag) {
    const diag = await JF.waitForDiagnoseChannels(api, token, 120000);
    console.log(`[13|${tag}] recovered Diagnose -> ${diag.OverallStatus}, ${diag.ChannelCount} channels`);
    expect(JF.HEALTHY_DIAGNOSE_STATUSES, `Diagnose healthy again (got ${diag.OverallStatus})`).toContain(diag.OverallStatus);

    // Breaker-open lasts at most 30s — poll well past it.
    const health = await JF.pollUntil(async () => {
      const res = await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/Health`, { headers: JF.authHeaders(token) });
      if (!res.ok()) return null;
      const h = await res.json();
      return h.CircuitState === 'Closed' && h.Status === 'Healthy' ? h : null;
    }, { timeoutMs: 90000, intervalMs: 3000, label: 'circuit breaker closes again' });
    console.log(`[13|${tag}] Health -> ${health.Status}/${health.CircuitState}`);

    let liveStreamId = null;
    try {
      const stream = await JF.pollUntil(async () => {
        const pi = await JF.requestPlaybackInfo(api, token, userId, channel.id, JF.deviceProfiles.directPlay);
        liveStreamId = pi.liveStreamId;
        const r = await JF.fetchStreamBytes(JF.rewriteHost(pi.ms.Path, JF.CONFIG.baseURL), { minBytes: 65536, timeoutMs: 15000 });
        if (r.status === 200 && r.bytes >= 65536) return r;
        await JF.closeLiveStream(api, token, liveStreamId);
        liveStreamId = null;
        return null;
      }, { timeoutMs: 90000, intervalMs: 3000, label: 'live stream delivers bytes again' });
      console.log(`[13|${tag}] stream -> ${stream.status} ${stream.contentType}, ${stream.bytes}B`);
    } finally {
      await JF.closeLiveStream(api, token, liveStreamId);
    }
  }

  test.beforeAll(async () => {
    api = await request.newContext({ baseURL: JF.CONFIG.baseURL, ignoreHTTPSErrors: true });
    ({ token, userId } = await JF.authenticate(api));
    pluginId = await JF.getPluginId(api, token);
    expect(pluginId, 'plugin installed').toBeTruthy();
    // Capture everything needed for the outage tests BEFORE breaking anything.
    pristine = await JF.readPluginConfig(api, token, pluginId);
    expect(JF.canonicalConfigDeviations(pristine), 'suite starts canonical').toEqual([]);
    channel = await JF.pickChannel(api, token, userId);
  });

  test.afterAll(async () => {
    // Restore unconditionally — even when a test aborted mid-outage.
    await JF.writePluginConfig(api, token, pluginId, pristine);
    await JF.refreshProfileCache(api, token);
    await JF.waitForDiagnoseChannels(api, token, 120000);
    const cfg = await JF.readPluginConfig(api, token, pluginId);
    expect(JF.canonicalConfigDeviations(cfg), 'config back to canonical').toEqual([]);
    await api.dispose();
  });

  test('unreachable backend: every surface degrades cleanly, then recovers', async () => {
    test.setTimeout(420000);
    try {
      const broken = JSON.parse(JSON.stringify(pristine));
      broken.Port = 9909; // same host -> connection refused, fails fast
      await JF.writePluginConfig(api, token, pluginId, broken);

      // Diagnose reports the outage as a structured ERROR (no 5xx, no hang).
      const diag = await JF.pollUntil(async () => {
        const d = await getJson('/TvHeadendApi/Diagnose');
        return d.OverallStatus === 'ERROR' ? d : null;
      }, { timeoutMs: 60000, intervalMs: 3000, label: 'Diagnose reports ERROR' });
      console.log(`[13|down] Diagnose -> ${diag.OverallStatus}: ${diag.Connection}`);
      expect(diag.Connection, 'connection error names the refused endpoint').toMatch(/connection refused|cannot reach/i);
      expect(diag.ChannelCount, 'no channels while down').toBe(0);

      // Dashboard shows the backend as down.
      const dash = await getJson('/TvHeadendApi/Dashboard');
      console.log(`[13|down] Dashboard -> IsReachable=${dash.IsReachable}, IsAuthenticated=${dash.IsAuthenticated}`);
      expect(dash.IsReachable, 'dashboard reports unreachable').toBe(false);

      // Repeated failures must trip the circuit breaker (5 failures -> open).
      const health = await JF.pollUntil(async () => {
        await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/Diagnose`, { headers: JF.authHeaders(token) }).catch(() => {});
        const res = await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/Health`, { headers: JF.authHeaders(token) });
        if (!res.ok()) return null;
        const h = await res.json();
        return h.CircuitState === 'Open' ? h : null;
      }, { timeoutMs: 90000, intervalMs: 3000, label: 'circuit breaker opens' });
      console.log(`[13|down] Health -> ${health.Status}/${health.CircuitState}, reason=${health.LastFailureReason}`);
      expect(health.Status).toBe('CircuitOpen');
      expect(health.IsDegradedModeActive, 'degraded mode active').toBe(true);

      // PlaybackInfo fails CONTROLLED: 200 with a (currently unservable) source — never a 500.
      const pi = await JF.requestPlaybackInfo(api, token, userId, channel.id, JF.deviceProfiles.directPlay);
      console.log(`[13|down] PlaybackInfo -> 200, ${(pi.ms.Path || '').replace(/token=[^&]+/, 'token=…')}`);
      expect(pi.ms.Path, 'a media source is still assembled').toBeTruthy();

      // …and actually fetching the relay stream yields a clean upstream error, fast.
      const streamUrl = JF.rewriteHost(pi.ms.Path, JF.CONFIG.baseURL);
      const relay = await JF.fetchStreamBytes(streamUrl, { minBytes: 1024, timeoutMs: 20000 });
      console.log(`[13|down] relay stream -> ${relay.status} after ${relay.elapsedMs}ms, ${relay.bytes}B`);
      expect([502, 503], `clean upstream error (got ${relay.status})`).toContain(relay.status);
      expect(relay.bytes, 'no payload bytes').toBe(0);
      await JF.closeLiveStream(api, token, pi.liveStreamId);
    } finally {
      await JF.writePluginConfig(api, token, pluginId, pristine);
    }
    await assertRecovered('down');
  });

  test('wrong credentials: auth failure is reported distinctly, then recovers', async () => {
    test.setTimeout(420000);
    try {
      const broken = JSON.parse(JSON.stringify(pristine));
      broken.Username = pristine.Password; // swapped -> digest auth fails with 403
      broken.Password = pristine.Username;
      await JF.writePluginConfig(api, token, pluginId, broken);

      // Diagnose reports an auth-shaped error (403/Forbidden), not a generic refusal.
      const diag = await JF.pollUntil(async () => {
        const d = await getJson('/TvHeadendApi/Diagnose');
        return d.OverallStatus === 'ERROR' ? d : null;
      }, { timeoutMs: 60000, intervalMs: 3000, label: 'Diagnose reports ERROR for bad credentials' });
      console.log(`[13|auth] Diagnose -> ${diag.OverallStatus}: ${diag.Connection}`);
      expect(diag.Connection, 'error names the auth failure').toMatch(/403|forbidden|401|unauthori[sz]ed/i);
      expect(diag.ChannelCount, 'no channels with bad credentials').toBe(0);

      const dash = await getJson('/TvHeadendApi/Dashboard');
      console.log(`[13|auth] Dashboard -> IsReachable=${dash.IsReachable}, IsAuthenticated=${dash.IsAuthenticated}`);
      expect(dash.IsAuthenticated, 'dashboard reports the auth failure').toBe(false);
    } finally {
      await JF.writePluginConfig(api, token, pluginId, pristine);
    }
    await assertRecovered('auth');
  });
});
