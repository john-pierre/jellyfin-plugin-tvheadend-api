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
  let api, token, userId, pluginId, channel, pristine, breakerBaseline;

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

    // If this recovery closed an open episode, its duration metrics must be recorded.
    if (health.Breaker.TimesOpened > breakerBaseline.TimesOpened) {
      console.log(`[13|${tag}] breaker episode: lastOpen=${health.Breaker.LastOpenDurationMs}ms, totalOpen=${health.Breaker.TotalOpenDurationMs}ms, trials=${health.Breaker.HalfOpenTrials}/${health.Breaker.HalfOpenTrialSuccesses} ok`);
      expect(health.Breaker.LastOpenDurationMs, 'recovered episode has a duration').toBeGreaterThan(0);
      expect(health.Breaker.TotalOpenDurationMs, 'cumulative open time includes the episode')
        .toBeGreaterThanOrEqual(health.Breaker.LastOpenDurationMs);
      expect(health.Breaker.HalfOpenTrialSuccesses, 'recovery went through a successful half-open trial')
        .toBeGreaterThan(breakerBaseline.HalfOpenTrialSuccesses);
    }

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
    // Breaker metrics baseline — all assertions below are DELTAS so the spec is independent
    // of how much breaker history the running plugin instance already accumulated.
    breakerBaseline = (await getJson('/TvHeadendApi/Health')).Breaker;
    expect(breakerBaseline, 'Health exposes the Breaker metrics block').toBeTruthy();
    console.log(`[13] breaker baseline: opened=${breakerBaseline.TimesOpened}, rejected=${breakerBaseline.RejectedWhileOpen}`);
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

      // Breaker metrics tell the story of THIS outage (delta vs the beforeAll baseline).
      expect(health.Breaker.TimesOpened, 'the open was counted').toBeGreaterThan(breakerBaseline.TimesOpened);
      expect(health.Breaker.LastOpenReason, 'open attributed to a connection-level reason')
        .toMatch(/ConnectionRefused|UpstreamUnavailable|Timeout|DnsFailure/);
      expect(health.Breaker.LastOpenedAtUtc, 'open timestamp recorded').toBeTruthy();
      expect(health.Breaker.CircuitFailures, 'breaker-relevant failures counted').toBeGreaterThan(breakerBaseline.CircuitFailures);
      expect(
        health.Breaker.FailureCountsByReason.ConnectionRefused || 0,
        'per-reason counter tracks the refusals',
      ).toBeGreaterThan((breakerBaseline.FailureCountsByReason || {}).ConnectionRefused || 0);

      // While open, blocked requests are counted as rejections (Diagnose fast-fails).
      await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/Diagnose`, { headers: JF.authHeaders(token) }).catch(() => {});
      const whileOpen = (await getJson('/TvHeadendApi/Health')).Breaker;
      console.log(`[13|down] breaker: opened=${whileOpen.TimesOpened}, rejected=${whileOpen.RejectedWhileOpen}, reason=${whileOpen.LastOpenReason}`);
      expect(whileOpen.RejectedWhileOpen, 'rejections while open are counted').toBeGreaterThan(breakerBaseline.RejectedWhileOpen);

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

  // Backlog #4 regression: per-request relay failures where TVHeadend RESPONDED (dead channel,
  // exhausted tuner, bad path -> upstream 4xx/5xx) must stay VISIBLE in metrics but must NOT
  // open the global circuit breaker nor downgrade the global health banner. Before the fix a
  // rapid burst of such errors flipped Health to Unreachable/Degraded while Diagnose still read
  // "Connected, 100" — the exact dashboard contradiction. The backend stays UP the whole time.
  test('server-responded relay failures stay visible but do not trip the global breaker', async () => {
    test.setTimeout(120000);
    // Backend healthy at the start.
    const diagBefore = await JF.waitForDiagnoseChannels(api, token, 60000);
    expect(diagBefore.OverallStatus, 'backend healthy before the burst').not.toBe('ERROR');
    const before = (await getJson('/TvHeadendApi/Health')).Breaker;

    // Fire a burst well past the breaker threshold. The admin image relay to a nonexistent
    // TVHeadend asset makes TVHeadend answer with an error (upstream 4xx/5xx) — the plugin
    // relays a 5xx to us but records the failure as non-breaker (server was reachable).
    const statuses = [];
    for (let i = 0; i < 12; i++) {
      const res = await api.get(`${JF.CONFIG.baseURL}/api/tvheadend/images/static/img/e2e-nonexistent-${i}.png`, { headers: JF.authHeaders(token) });
      statuses.push(res.status());
    }
    console.log(`[13|relay] 12 relay-error statuses: ${statuses.join(',')}`);

    const health = await getJson('/TvHeadendApi/Health');
    const after = health.Breaker;
    console.log(`[13|relay] Health -> ${health.Status}/${health.CircuitState} | reported +${after.ReportedFailures - before.ReportedFailures}, circuitFails +${after.CircuitFailures - before.CircuitFailures}, opened +${after.TimesOpened - before.TimesOpened}`);

    // The failures ARE visible in metrics …
    expect(after.ReportedFailures, 'relay failures recorded in metrics').toBeGreaterThan(before.ReportedFailures);
    // … but did NOT count toward the circuit or open it, and did not block requests.
    expect(after.CircuitFailures, 'relay failures do NOT count toward the breaker').toBe(before.CircuitFailures);
    expect(after.TimesOpened, 'the breaker did NOT open').toBe(before.TimesOpened);
    expect(health.CircuitState, 'circuit stays closed').toBe('Closed');
    expect(health.Status, 'global health is not downgraded by per-channel failures').toBe('Healthy');
    expect(health.IsDegradedModeActive, 'degraded mode not active').toBe(false);

    // No contradiction: Diagnose still reports the backend as reachable, and a real stream works.
    const diagAfter = await getJson('/TvHeadendApi/Diagnose');
    expect(diagAfter.OverallStatus, 'Diagnose stays consistent with health (no #4 contradiction)').not.toBe('ERROR');
    expect(diagAfter.ChannelCount, 'channels still visible').toBeGreaterThan(0);

    let liveStreamId = null;
    try {
      const pi = await JF.requestPlaybackInfo(api, token, userId, channel.id, JF.deviceProfiles.directPlay);
      liveStreamId = pi.liveStreamId;
      const bytes = await JF.fetchStreamBytes(JF.rewriteHost(pi.ms.Path, JF.CONFIG.baseURL), { minBytes: 65536, timeoutMs: 15000 });
      console.log(`[13|relay] real stream after the burst -> ${bytes.status}, ${bytes.bytes}B`);
      expect(bytes.status, 'real streaming unaffected by the dead-channel burst').toBe(200);
    } finally {
      await JF.closeLiveStream(api, token, liveStreamId);
    }
  });
});
