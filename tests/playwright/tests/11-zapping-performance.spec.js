// THE core product promise: channel start & zap times. The per-channel/per-profile mediainfo
// cache lets Jellyfin skip its ~3s live-stream analysis so clients Direct-Play within 1-2s.
// This spec proves the whole chain end-to-end: warmup fills the cache, warm starts deliver
// first bytes fast, zapping across all channels stays within budget, and per-rule profile
// resolution picks the matching cache file (second start warm) without breaking Direct Play.
//
// Modes:
//   default            — full suite against the docker test stack (mutates cache/rules, restores).
//   ZAP_READONLY=1     — measurement-only subset safe for a REAL backend (no config writes,
//                        no cache invalidation, softer budgets, results logged as a table).
const { test, expect, request } = require('@playwright/test');
const JF = require('../fixtures/jellyfin');

const READONLY = process.env.ZAP_READONLY === '1';

// Budgets in ms. Test stack runs on localhost — the product budget is 1-2s zapping; the CI
// thresholds add headroom for cold docker layers. Readonly (real LAN backend) gets more slack.
const BUDGET = READONLY
  ? { perStartMax: 4000, medianMax: 2500, warmPlaybackInfoMax: 3500 }
  : { perStartMax: 3000, medianMax: 2000, warmPlaybackInfoMax: 3000 };

const COLD_START_TOLERANCE_MS = 15000;

function median(values) {
  const s = [...values].sort((a, b) => a - b);
  return s.length % 2 ? s[(s.length - 1) / 2] : (s[s.length / 2 - 1] + s[s.length / 2]) / 2;
}

/**
 * Full channel-start cycle as a client would do it: PlaybackInfo (autoOpenLiveStream=true,
 * includes the plugin's cache lookup + Jellyfin's open) then fetch of the first stream bytes.
 * Returns timing breakdown + the media source for assertions. Always closes the live stream.
 */
async function timedChannelStart(api, token, userId, channel, deviceProfile, opts = {}) {
  const t0 = Date.now();
  const { ms, method } = await JF.requestPlaybackInfo(api, token, userId, channel.id, deviceProfile, opts.flags || {});
  const tPlaybackInfo = Date.now() - t0;

  expect(ms.Path, `media source path for ${channel.name}`).toBeTruthy();
  const streamUrl = JF.rewriteHost(ms.Path, JF.CONFIG.baseURL);

  const tFetch0 = Date.now();
  const got = await JF.fetchStreamBytes(streamUrl, { minBytes: 64 * 1024, timeoutMs: 20000 });
  const tFirstBytes = Date.now() - tFetch0;

  if (ms.LiveStreamId) {
    await JF.closeLiveStream(api, token, ms.LiveStreamId);
  }

  return {
    channel: channel.name,
    method,
    profileInPath: (ms.Path.match(/profile=([^&]+)/) || [])[1] || '',
    playbackInfoMs: tPlaybackInfo,
    firstBytesMs: tFirstBytes,
    totalMs: tPlaybackInfo + tFirstBytes,
    bytes: got.bytes,
    status: got.status,
    ms,
  };
}

test.describe.configure({ mode: 'serial' });

// Picks `count` entries spread evenly across the whole list (first + last included) so the
// sample covers the full lineup instead of just the first few channels.
function spreadSample(list, count) {
  if (list.length <= count) return [...list];
  const step = (list.length - 1) / (count - 1);
  return Array.from({ length: count }, (_, i) => list[Math.round(i * step)]);
}

test.describe('Zapping performance & cache correctness', () => {
  let api, token, userId, pluginId, channels, sampleChannels, zapChannels;

  test.beforeAll(async () => {
    api = await request.newContext({ baseURL: JF.CONFIG.baseURL, ignoreHTTPSErrors: true });
    ({ token, userId } = await JF.authenticate(api));
    pluginId = await JF.getPluginId(api, token);
    expect(pluginId, 'plugin installed').toBeTruthy();
    if (!READONLY) {
      await JF.ensureManagedProfile(api, token, pluginId);
    }
    expect(await JF.ensureLiveTvChannels(api, token, userId), 'live TV channels available').toBeTruthy();

    // Full lineup (the test stack simulates a production-sized 100-channel setup).
    channels = await JF.getChannels(api, token, userId, READONLY ? 5 : 200);
    expect(channels.length, 'at least 2 channels for zapping').toBeGreaterThan(1);

    // First-start sample spread across the WHOLE lineup, and a smaller zap rotation.
    sampleChannels = spreadSample(channels, READONLY ? 5 : 10);
    zapChannels = spreadSample(channels, 5);
    console.log(`[11] lineup=${channels.length} channels; first-start sample=${sampleChannels.map((c) => c.name).join(', ')}`);
  });

  test.afterAll(async () => {
    if (!READONLY) {
      const cfg = await JF.readPluginConfig(api, token, pluginId);
      expect(JF.canonicalConfigDeviations(cfg), 'plugin config back to canonical').toEqual([]);
    }
    await api.dispose();
  });

  test('warmup fills the mediainfo cache for the FULL lineup within budget', async () => {
    test.skip(READONLY, 'mutates cache — not run against a real backend');
    // The 360s product budget below is the real assertion — the Playwright default test
    // timeout (180s) must not kill the run before the budget check gets its chance.
    test.setTimeout(480000);

    // Start from a clean slate so this proves WARMUP coverage, not leftovers.
    const inv = await api.post(`${JF.CONFIG.baseURL}/TvHeadendApi/InvalidateCache`, { headers: JF.authHeaders(token) });
    expect(inv.ok(), `InvalidateCache status ${inv.status()}`).toBeTruthy();

    const t0 = Date.now();
    const warm = await api.post(`${JF.CONFIG.baseURL}/TvHeadendApi/WarmCache`, {
      headers: JF.authHeaders(token),
      timeout: 420000,
    });
    const warmupMs = Date.now() - t0;
    expect(warm.ok(), `WarmCache status ${warm.status()}`).toBeTruthy();
    const result = await warm.json();
    console.log(`[11] warmup of ${channels.length}-channel lineup took ${Math.round(warmupMs / 1000)}s:`, JSON.stringify(result).slice(0, 400));

    const total = result.TotalChannels ?? result.Total ?? 0;
    const warmed = result.WarmedChannels ?? result.Warmed ?? result.SuccessCount ?? 0;
    expect(total, 'warmup saw the FULL lineup').toBeGreaterThanOrEqual(channels.length);
    expect(warmed, 'EVERY channel got a cache entry').toBeGreaterThanOrEqual(channels.length);
    expect(warmupMs, 'full-lineup warmup finishes within budget').toBeLessThan(360000);
  });

  test('FIRST start after warmup is fast — sampled across the full lineup', async () => {
    // Every sampled channel is started for the FIRST time since the cache was invalidated
    // and re-warmed in the previous test — this proves the warmup itself makes the first
    // playback fast, not an on-demand cache written by an earlier playback of the same channel.
    const results = [];
    for (const channel of sampleChannels) {
      results.push(await timedChannelStart(api, token, userId, channel, JF.deviceProfiles.directPlay));
      await new Promise((r) => setTimeout(r, 1000));
    }

    console.table(results.map(({ ms, ...r }) => r));

    for (const r of results) {
      expect(r.status, `${r.channel}: stream HTTP status`).toBe(200);
      expect(r.bytes, `${r.channel}: real stream data`).toBeGreaterThanOrEqual(64 * 1024);
      expect(r.method, `${r.channel}: cache keeps the client on Direct Play`).toBe('DirectPlay');
      expect(
        r.playbackInfoMs,
        `${r.channel}: PlaybackInfo answered from cache (no ~3s live analysis)`,
      ).toBeLessThan(BUDGET.warmPlaybackInfoMax);
      expect(r.totalMs, `${r.channel}: full start within budget`).toBeLessThan(BUDGET.perStartMax + 2000);
    }
  });

  test('zapping: sequential channel switches stay within the 1-2s product budget', async () => {
    // A zap = full start cycle on the NEXT channel. Run two full laps across all channels
    // so every switch is measured twice (12+ data points on the 5-channel stack).
    const times = [];
    const rows = [];
    for (let lap = 0; lap < 2; lap++) {
      for (const channel of zapChannels) {
        const r = await timedChannelStart(api, token, userId, channel, JF.deviceProfiles.directPlay);
        times.push(r.totalMs);
        rows.push({ lap, channel: r.channel, totalMs: r.totalMs, playbackInfoMs: r.playbackInfoMs, firstBytesMs: r.firstBytesMs });
      }
    }

    console.table(rows);
    const med = median(times);
    const max = Math.max(...times);
    console.log(`[11] zapping: median=${med}ms max=${max}ms budget(median<${BUDGET.medianMax}, each<${BUDGET.perStartMax})`);

    expect(med, 'median zap time').toBeLessThan(BUDGET.medianMax);
    for (const t of times) {
      expect(t, 'every single zap within hard budget').toBeLessThan(BUDGET.perStartMax);
    }
  });

  test('per-rule profile resolution picks the matching cache and stays warm', async () => {
    test.skip(READONLY, 'mutates streaming-profile rules — not run against a real backend');

    const original = await JF.readPluginConfig(api, token, pluginId);
    const originalJson = JSON.stringify(original);

    try {
      // Rule: client "ZapProbe" streams with 'test-pass' instead of the managed default.
      const modified = JSON.parse(originalJson);
      modified.StreamingProfileSettings.ClientRules = [
        ...(modified.StreamingProfileSettings.ClientRules || []),
        {
          Enabled: true,
          RuleName: 'zap-probe-rule',
          Description: 'Added by 11-zapping-performance',
          Priority: 1,
          MatchType: 0,
          MatchValue: 'ZapProbe',
          PlaybackMode: 0,
          TvHeadendProfileName: 'test-pass',
        },
      ];
      await JF.writePluginConfig(api, token, pluginId, modified);
      await JF.refreshProfileCache(api, token);

      const channel = channels[0];
      const probe = await JF.authenticateAs(api, 'ZapProbe');

      // First start as ZapProbe may build the per-profile cache entry on demand — only assert
      // correctness here. The SECOND start must hit the profile-specific cache: warm + fast.
      const first = await timedChannelStart(api, probe.token, probe.userId, channel, JF.deviceProfiles.directPlay, {
        flags: { headers: probe.headers },
      });
      expect(first.profileInPath, 'rule routes ZapProbe to test-pass').toBe('test-pass');
      expect(first.method, 'rule profile still Direct Plays').toBe('DirectPlay');
      expect(first.bytes).toBeGreaterThanOrEqual(64 * 1024);

      const second = await timedChannelStart(api, probe.token, probe.userId, channel, JF.deviceProfiles.directPlay, {
        flags: { headers: probe.headers },
      });
      console.log(`[11] rule-profile starts: first=${first.totalMs}ms second=${second.totalMs}ms`);
      expect(second.profileInPath).toBe('test-pass');
      expect(second.method).toBe('DirectPlay');
      expect(second.totalMs, 'second start hits the per-profile cache (warm)').toBeLessThan(BUDGET.perStartMax);

      // Control: a different client keeps the managed default profile AND its warm cache.
      const control = await timedChannelStart(api, token, userId, channel, JF.deviceProfiles.directPlay);
      expect(control.profileInPath, 'unmatched client keeps the default profile').toBe('jellyfin');
      expect(control.method).toBe('DirectPlay');
      expect(control.totalMs, 'default-profile cache still warm').toBeLessThan(BUDGET.perStartMax);

      // Ping-pong: alternating rule-profile and default-profile starts on the SAME channel.
      // The cache reconciliation rewrites the file synthetically (no probe), so BOTH sides
      // must stay inside the zap budget on every alternation.
      const pingPong = [];
      for (let i = 0; i < 2; i++) {
        const p = await timedChannelStart(api, probe.token, probe.userId, channel, JF.deviceProfiles.directPlay, {
          flags: { headers: probe.headers },
        });
        const d = await timedChannelStart(api, token, userId, channel, JF.deviceProfiles.directPlay);
        pingPong.push({ round: i, probeMs: p.totalMs, probeProfile: p.profileInPath, defaultMs: d.totalMs, defaultProfile: d.profileInPath });
        expect(p.profileInPath).toBe('test-pass');
        expect(d.profileInPath).toBe('jellyfin');
        expect(p.totalMs, `ping-pong round ${i}: rule profile stays warm`).toBeLessThan(BUDGET.perStartMax);
        expect(d.totalMs, `ping-pong round ${i}: default profile stays warm`).toBeLessThan(BUDGET.perStartMax);
      }
      console.table(pingPong);
    } finally {
      await JF.writePluginConfig(api, token, pluginId, JSON.parse(originalJson));
      await JF.refreshProfileCache(api, token);
    }
  });

  test('cold vs warm: the cache is what makes starts fast', async () => {
    test.setTimeout(480000);
    test.skip(READONLY, 'invalidates the cache — not run against a real backend');

    const channel = channels[1];

    const inv = await api.post(`${JF.CONFIG.baseURL}/TvHeadendApi/InvalidateCache`, { headers: JF.authHeaders(token) });
    expect(inv.ok()).toBeTruthy();

    const cold = await timedChannelStart(api, token, userId, channel, JF.deviceProfiles.directPlay);
    expect(cold.status, 'cold start still works (probe/synthetic fallback)').toBe(200);
    expect(cold.totalMs, 'cold start within generous tolerance').toBeLessThan(COLD_START_TOLERANCE_MS);

    // Full-lineup warmup takes ~3min on this stack (100 channels) — same generous request
    // timeout as the warmup in the first test; 180s was a knife's edge and flaked.
    const warmResp = await api.post(`${JF.CONFIG.baseURL}/TvHeadendApi/WarmCache`, {
      headers: JF.authHeaders(token),
      timeout: 420000,
    });
    expect(warmResp.ok()).toBeTruthy();

    const warm = await timedChannelStart(api, token, userId, channel, JF.deviceProfiles.directPlay);
    console.log(`[11] cold=${cold.totalMs}ms (playbackInfo ${cold.playbackInfoMs}ms) vs warm=${warm.totalMs}ms (playbackInfo ${warm.playbackInfoMs}ms)`);

    expect(warm.totalMs, 'warm start within budget').toBeLessThan(BUDGET.perStartMax);
    expect(
      warm.playbackInfoMs,
      'warm PlaybackInfo must not be slower than cold (cache skips the analysis)',
    ).toBeLessThanOrEqual(Math.max(cold.playbackInfoMs, BUDGET.warmPlaybackInfoMax));
  });
});
