// Settings matrix (API level): every plugin setting is verified for persistence, and every
// behavior-affecting setting is verified to ACTUALLY change behavior.
//
// A) Persistence sweep — one mutated copy of the WHOLE configuration (every field gets a
//    distinguishable non-default marker, except connectivity-critical fields and the
//    StreamingProfile/DefaultTvHeadendProfile pair, which are behaviorally tested in section B)
//    is saved, re-read, and asserted field-by-field, then the pre-sweep body is restored and
//    re-verified. This catches any field the save/load path silently drops or coerces.
// B) Behavioral checks — each setting that changes plugin BEHAVIOR (not just a stored value)
//    gets its own test with a read-modify-write body captured before mutation and restored in
//    `finally`, streams drained afterwards.
// C) Final sanity: canonical config, healthy Diagnose, one more real PlaybackInfo + stream read.
//
// See fixtures/jellyfin.js for shared helpers. Discipline matches 06/08/10: serial, exact
// read-modify-write restores, poll-with-deadline waits, drained streams.
const { test, expect, request } = require('@playwright/test');
const JF = require('../fixtures/jellyfin');

test.describe.configure({ mode: 'serial' });

// ─────────────────────────────────────────────────────────────────────────
// A) Persistence sweep — field mutation plan
// ─────────────────────────────────────────────────────────────────────────

// Connectivity-critical fields: touching these could break the stack for every other test in
// the suite if a save races a read elsewhere, and they carry no interesting persistence risk
// beyond the string/int/bool round-trip already exercised by every other field below.
const UNTOUCHED_TOP_FIELDS = [
  'Host', 'Port', 'Username', 'Password', 'UseSSL', 'IgnoreCertificateErrors',
  'AllowAnonymousAccess', 'Webroot',
  // Legacy/behavioral — covered by B6 (DefaultTvHeadendProfile) instead of a blind marker sweep.
  'StreamingProfile',
];
const UNTOUCHED_SPS_FIELDS = ['DefaultTvHeadendProfile'];

// AuthToken must stay alphanumeric-only (FFmpeg-URL-safe convention — see CLAUDE.md), so it
// gets a fixed alphanumeric marker instead of the generic "-e2e-sweep-marker" string suffix.
const SPECIAL_TOP_VALUES = { AuthToken: 'e2eSweepMarkerAuthToken123' };

// String enum fields serialize as their C# name (System.Text.Json string enum converter) — pick
// a concrete, different, valid member rather than appending a marker suffix (which would not be
// a valid enum value).
const TOP_ENUM_ALT = {
  StreamDeliveryMode: 'DirectToTvheadend',
  StatisticsRetentionPeriod: 'SixMonths',
  PluginLogLevelOverride: 'Debug',
};
const SPS_ENUM_ALT = { DefaultPlaybackMode: 'PassThrough' };

// The only array fields anywhere in PluginConfiguration live under StreamingProfileSettings.
// Each gets exactly one synthetic, schema-shaped element (task requirement: "arrays get one
// synthetic element").
const SPS_ARRAY_SYNTHETIC = {
  ChannelOverrides: [{
    ChannelId: 'e2e-sweep-channel', PlaybackMode: 'PassThrough', TvHeadendProfileName: 'pass', Description: 'e2e sweep marker',
  }],
  ChannelGroupOverrides: [{
    ChannelGroup: 'e2e-sweep-group', PlaybackMode: 'Auto', TvHeadendProfileName: '', Description: 'e2e sweep marker',
  }],
  ClientRules: [{
    Enabled: true, RuleName: 'e2e-sweep-client-rule', Description: 'e2e sweep marker', Priority: 1,
    MatchType: 'ClientNameExact', MatchValue: 'e2e-sweep-client', PlaybackMode: 'Auto', TvHeadendProfileName: '',
  }],
  UserRules: [{
    Enabled: true, RuleName: 'e2e-sweep-user-rule', Description: 'e2e sweep marker', Priority: 1,
    MatchType: 'UserIdExact', MatchValue: 'e2e-sweep-user', PlaybackMode: 'Auto', TvHeadendProfileName: '',
  }],
};

function mutateScalar(key, value, enumAlt) {
  if (Object.prototype.hasOwnProperty.call(enumAlt, key)) return enumAlt[key];
  if (typeof value === 'boolean') return !value;
  if (typeof value === 'number') return value + 1; // +1 is within every declared min/max in ConfigPage.html for every field this sweep touches.
  if (typeof value === 'string') return `${value}-e2e-sweep-marker`;
  throw new Error(`sweep: no mutation strategy for field '${key}' (type ${typeof value})`);
}

// Builds ONE modified copy of the whole configuration: every field gets a distinguishable
// marker except the untouched connectivity/legacy fields above.
function buildSweptConfig(before) {
  const swept = JSON.parse(JSON.stringify(before));
  for (const key of Object.keys(before)) {
    if (key === 'StreamingProfileSettings') continue;
    if (UNTOUCHED_TOP_FIELDS.includes(key)) continue;
    swept[key] = Object.prototype.hasOwnProperty.call(SPECIAL_TOP_VALUES, key)
      ? SPECIAL_TOP_VALUES[key]
      : mutateScalar(key, before[key], TOP_ENUM_ALT);
  }
  const sps = before.StreamingProfileSettings;
  const sweptSps = swept.StreamingProfileSettings;
  for (const key of Object.keys(sps)) {
    if (UNTOUCHED_SPS_FIELDS.includes(key)) continue;
    if (Array.isArray(sps[key])) {
      sweptSps[key] = SPS_ARRAY_SYNTHETIC[key];
      continue;
    }
    sweptSps[key] = mutateScalar(key, sps[key], SPS_ENUM_ALT);
  }
  return swept;
}

test.describe('A) Full configuration persistence sweep', () => {
  let api, token, pluginId;

  test.beforeAll(async () => {
    api = await request.newContext({ baseURL: JF.CONFIG.baseURL, ignoreHTTPSErrors: true });
    ({ token } = await JF.authenticate(api));
    pluginId = await JF.getPluginId(api, token);
    expect(pluginId, 'plugin installed').toBeTruthy();
  });
  test.afterAll(async () => {
    const cfg = await JF.readPluginConfig(api, token, pluginId);
    expect(JF.canonicalConfigDeviations(cfg), 'config back to canonical').toEqual([]);
    await api.dispose();
  });

  test('every settable field round-trips exactly through save/load, then restores exactly', async () => {
    test.setTimeout(60000);
    const before = await JF.readPluginConfig(api, token, pluginId);
    const restoreBody = JSON.parse(JSON.stringify(before));
    const swept = buildSweptConfig(before);

    try {
      await JF.writePluginConfig(api, token, pluginId, swept);
      const after = await JF.readPluginConfig(api, token, pluginId);

      let mutatedCount = 0;
      let untouchedCount = 0;
      for (const key of Object.keys(before)) {
        if (key === 'StreamingProfileSettings') continue;
        if (UNTOUCHED_TOP_FIELDS.includes(key)) {
          expect(after[key], `${key} must stay untouched by the sweep`).toEqual(before[key]);
          untouchedCount++;
          continue;
        }
        expect(after[key], `${key} persisted the swept marker value exactly`).toEqual(swept[key]);
        mutatedCount++;
      }
      for (const key of Object.keys(before.StreamingProfileSettings)) {
        const path = `StreamingProfileSettings.${key}`;
        if (UNTOUCHED_SPS_FIELDS.includes(key)) {
          expect(after.StreamingProfileSettings[key], `${path} must stay untouched by the sweep`).toEqual(before.StreamingProfileSettings[key]);
          untouchedCount++;
          continue;
        }
        expect(after.StreamingProfileSettings[key], `${path} persisted the swept marker value exactly`).toEqual(swept.StreamingProfileSettings[key]);
        mutatedCount++;
      }
      console.log(`[07|A] persistence sweep: ${mutatedCount} fields mutated+verified, ${untouchedCount} fields verified untouched`);
    } finally {
      await JF.writePluginConfig(api, token, pluginId, restoreBody);
      const restored = await JF.readPluginConfig(api, token, pluginId);
      expect(restored, 'config restored exactly to the pre-sweep body').toEqual(restoreBody);
      await JF.refreshProfileCache(api, token);
      await JF.waitForDiagnoseChannels(api, token);
    }
  });
});

// ─────────────────────────────────────────────────────────────────────────
// B) Behavioral checks
// ─────────────────────────────────────────────────────────────────────────

test.describe('B) Behavioral checks', () => {
  let api, token, userId, pluginId, channels, tvhChannels;
  let rot = 0;
  const nextChannel = () => channels[rot++ % channels.length];

  test.beforeAll(async () => {
    api = await request.newContext({ baseURL: JF.CONFIG.baseURL, ignoreHTTPSErrors: true });
    ({ token, userId } = await JF.authenticate(api));
    pluginId = await JF.getPluginId(api, token);
    expect(pluginId, 'plugin installed').toBeTruthy();
    await JF.ensureManagedProfile(api, token, pluginId);
    await JF.pickChannel(api, token, userId);
    channels = await JF.getChannels(api, token, userId, 50);
    tvhChannels = await JF.getTvhChannels(api, token);
    expect(tvhChannels.length, 'at least one TVHeadend channel').toBeGreaterThan(0);
  });
  test.afterAll(async () => {
    const cfg = await JF.readPluginConfig(api, token, pluginId);
    expect(JF.canonicalConfigDeviations(cfg), 'config back to canonical').toEqual([]);
    await api.dispose();
  });

  test('B1: EnableRelayTokenSecurity gates the OPEN stream endpoint', async () => {
    test.setTimeout(60000);
    const uuid = tvhChannels[tvhChannels.length - 1].Id;
    const before = await JF.readPluginConfig(api, token, pluginId);
    const restoreBody = JSON.parse(JSON.stringify(before));
    expect(before.EnableRelayTokenSecurity, 'canonical security is on').toBeTruthy();
    try {
      const denied = await JF.fetchStreamBytes(`${JF.CONFIG.baseURL}/api/tvheadend/stream/${uuid}?profile=pass`, { minBytes: 1024, timeoutMs: 8000 });
      console.log(`[07|B1] security on, no token -> ${denied.status}`);
      expect(denied.status, 'security on: open endpoint rejects without a token').toBe(401);

      const mutated = JSON.parse(JSON.stringify(before));
      mutated.EnableRelayTokenSecurity = false;
      await JF.writePluginConfig(api, token, pluginId, mutated);

      const allowed = await JF.pollUntil(async () => {
        const r = await JF.fetchStreamBytes(`${JF.CONFIG.baseURL}/api/tvheadend/stream/${uuid}?profile=pass`, { minBytes: 16384, timeoutMs: 15000 });
        return r.status === 200 ? r : null;
      }, { timeoutMs: 30000, intervalMs: 2000, label: 'open stream 200 with security off' });
      console.log(`[07|B1] security off, no token -> ${allowed.status} ${allowed.contentType}, ${allowed.bytes}B`);
      expect(allowed.contentType, 'video content type').toMatch(/^video\//);
      expect(allowed.bytes, 'bytes arrived').toBeGreaterThan(0);
    } finally {
      await JF.writePluginConfig(api, token, pluginId, restoreBody);
      await new Promise((r) => setTimeout(r, 1500));
    }
  });

  test('B2: RelayEnabled kill-switch — stream and image endpoints', async () => {
    test.setTimeout(90000);
    const before = await JF.readPluginConfig(api, token, pluginId);
    const restoreBody = JSON.parse(JSON.stringify(before));
    expect(before.RelayEnabled, 'canonical relay is enabled').toBeTruthy();
    let liveStreamId = null;
    try {
      // true (canonical): both a tokenized relay stream and the image relay serve real bytes.
      const pi = await JF.requestPlaybackInfo(api, token, userId, nextChannel().id, JF.deviceProfiles.directPlay);
      liveStreamId = pi.liveStreamId;
      expect(pi.ms.Path, 'tokenized relay stream path').toContain('/api/tvheadend/relay/stream/');
      const streamUrl = JF.rewriteHost(pi.ms.Path, JF.CONFIG.baseURL);
      const streamOk = await JF.fetchStreamBytes(streamUrl, { minBytes: 16384, timeoutMs: 15000 });
      console.log(`[07|B2] RelayEnabled=true stream -> ${streamOk.status}`);
      expect(streamOk.status, 'RelayEnabled=true: tokenized stream serves 200').toBe(200);

      // Real, always-present TVHeadend static asset — the image-relay equivalent of the stream
      // check above (uses the admin-authenticated relay/images endpoint, which shares the exact
      // same RelayEnabled guard as the token-secured relay/images variant in RelayController).
      const imgOkRes = await api.get(`${JF.CONFIG.baseURL}/api/tvheadend/images/static/img/logo.png`, { headers: JF.authHeaders(token) });
      console.log(`[07|B2] RelayEnabled=true image -> ${imgOkRes.status()}`);
      expect(imgOkRes.status(), 'RelayEnabled=true: image relay serves 200').toBe(200);

      await JF.closeLiveStream(api, token, liveStreamId);
      liveStreamId = null;
      await new Promise((r) => setTimeout(r, 1500));

      // false: both refuse with 503 — even the previously-valid tokenized stream URL.
      const mutated = JSON.parse(JSON.stringify(before));
      mutated.RelayEnabled = false;
      await JF.writePluginConfig(api, token, pluginId, mutated);

      const streamOff = await JF.pollUntil(async () => {
        const r = await JF.fetchStreamBytes(streamUrl, { minBytes: 1024, timeoutMs: 8000 });
        return r.status === 503 ? r : null;
      }, { timeoutMs: 20000, intervalMs: 1500, label: 'RelayEnabled=false: stream 503' });
      console.log(`[07|B2] RelayEnabled=false stream -> ${streamOff.status}`);
      expect(streamOff.status).toBe(503);

      const imgOffRes = await api.get(`${JF.CONFIG.baseURL}/api/tvheadend/images/static/img/logo.png`, { headers: JF.authHeaders(token) });
      console.log(`[07|B2] RelayEnabled=false image -> ${imgOffRes.status()}`);
      expect(imgOffRes.status(), 'RelayEnabled=false: image relay 503').toBe(503);
    } finally {
      await JF.closeLiveStream(api, token, liveStreamId);
      await JF.writePluginConfig(api, token, pluginId, restoreBody);
      await JF.refreshProfileCache(api, token);
      await JF.waitForDiagnoseChannels(api, token);
    }
  });

  test('B3: StreamTokenTtlSeconds — usable immediately, rejected once expired', async () => {
    test.setTimeout(90000);
    const before = await JF.readPluginConfig(api, token, pluginId);
    const restoreBody = JSON.parse(JSON.stringify(before));
    let liveStreamId = null;
    try {
      const mutated = JSON.parse(JSON.stringify(before));
      mutated.StreamTokenTtlSeconds = 15; // above RelayTokenOptions.MinStreamTtlSeconds (10)
      await JF.writePluginConfig(api, token, pluginId, mutated);
      await JF.waitForDiagnoseChannels(api, token);

      const pi = await JF.requestPlaybackInfo(api, token, userId, nextChannel().id, JF.deviceProfiles.directPlay);
      liveStreamId = pi.liveStreamId;
      expect(pi.ms.Path, 'tokenized relay path').toContain('/api/tvheadend/relay/stream/');
      const url = JF.rewriteHost(pi.ms.Path, JF.CONFIG.baseURL);

      const immediate = await JF.fetchStreamBytes(url, { minBytes: 16384, timeoutMs: 15000 });
      console.log(`[07|B3] immediate use -> ${immediate.status}`);
      expect(immediate.status, 'token usable immediately after issuance').toBe(200);
      await JF.closeLiveStream(api, token, liveStreamId);
      liveStreamId = null;

      // TTL(15s) + default clock-skew tolerance(5s) = 20s effective expiry. Wait well past it.
      await new Promise((r) => setTimeout(r, 25000));

      const expired = await JF.fetchStreamBytes(url, { minBytes: 1, timeoutMs: 8000 });
      console.log(`[07|B3] after ~25s -> ${expired.status}`);
      expect([401, 403, 410], `expired token rejected, got ${expired.status}`).toContain(expired.status);
    } finally {
      await JF.closeLiveStream(api, token, liveStreamId);
      await JF.writePluginConfig(api, token, pluginId, restoreBody);
      await JF.refreshProfileCache(api, token);
      await JF.waitForDiagnoseChannels(api, token);
    }
  });

  test('B4: StreamTokenMaxUses — 3rd use of the same token is rejected', async () => {
    test.setTimeout(60000);
    const before = await JF.readPluginConfig(api, token, pluginId);
    const restoreBody = JSON.parse(JSON.stringify(before));
    let liveStreamId = null;
    try {
      const mutated = JSON.parse(JSON.stringify(before));
      mutated.StreamTokenMaxUses = 2;
      await JF.writePluginConfig(api, token, pluginId, mutated);
      await JF.waitForDiagnoseChannels(api, token);

      const pi = await JF.requestPlaybackInfo(api, token, userId, nextChannel().id, JF.deviceProfiles.directPlay);
      liveStreamId = pi.liveStreamId;
      expect(pi.ms.Path, 'tokenized relay path').toContain('/api/tvheadend/relay/stream/');
      const url = JF.rewriteHost(pi.ms.Path, JF.CONFIG.baseURL);

      const first = await JF.fetchStreamBytes(url, { minBytes: 16384, timeoutMs: 15000 });
      const second = await JF.fetchStreamBytes(url, { minBytes: 16384, timeoutMs: 15000 });
      const third = await JF.fetchStreamBytes(url, { minBytes: 1, timeoutMs: 8000 });
      console.log(`[07|B4] use 1 -> ${first.status}, use 2 -> ${second.status}, use 3 -> ${third.status}`);
      expect(first.status, 'use 1/2 accepted').toBe(200);
      expect(second.status, 'use 2/2 accepted').toBe(200);
      expect(third.status, 'use 3/2 rejected (max uses exceeded)').toBe(403);
    } finally {
      await JF.closeLiveStream(api, token, liveStreamId);
      await JF.writePluginConfig(api, token, pluginId, restoreBody);
      const restored = await JF.readPluginConfig(api, token, pluginId);
      expect(restored.StreamTokenMaxUses, 'default 25 restored').toBe(25);
      await JF.refreshProfileCache(api, token);
      await JF.waitForDiagnoseChannels(api, token);
    }
  });

  test('B5: StreamDeliveryMode flips the stream URL shape (Relay vs DirectToTvheadend)', async () => {
    test.setTimeout(120000);
    // DirectToTvheadend needs a TVHeadend auth token in the config — without one the plugin
    // (deliberately) falls back to relay delivery and this test would chase a URL shape that
    // can never appear. AuthToken is NOT a canonical-config invariant, so self-provision it
    // instead of relying on residue from other specs.
    if (!((await JF.readPluginConfig(api, token, pluginId)).AuthToken || '').length) {
      await JF.generateAuthToken(api, token);
      console.log('[07|B5] provisioned a TVHeadend auth token (config had none)');
    }
    const before = await JF.readPluginConfig(api, token, pluginId);
    const restoreBody = JSON.parse(JSON.stringify(before));
    expect(before.StreamDeliveryMode, 'canonical delivery mode is Relay').toBe('Relay');
    expect((before.AuthToken || '').length, 'auth token available for direct delivery').toBeGreaterThan(0);
    let liveStreamId = null;
    try {
      const relayPi = await JF.requestPlaybackInfo(api, token, userId, nextChannel().id, JF.deviceProfiles.directPlay);
      liveStreamId = relayPi.liveStreamId;
      console.log(`[07|B5] Relay path -> ${(relayPi.ms.Path || '').replace(/token=[^&]+/, 'token=…')}`);
      expect(relayPi.ms.Path).toContain('/api/tvheadend/relay/stream/');
      expect(relayPi.ms.Path).toContain('token=');
      await JF.closeLiveStream(api, token, liveStreamId);
      liveStreamId = null;

      const mutated = JSON.parse(JSON.stringify(before));
      mutated.StreamDeliveryMode = 'DirectToTvheadend';
      await JF.writePluginConfig(api, token, pluginId, mutated);
      await JF.refreshProfileCache(api, token);
      await JF.waitForDiagnoseChannels(api, token);

      // Poll past the plugin's ~10s stream-build reuse window until a direct URL shows up.
      const directPi = await JF.pollUntil(async () => {
        const attempt = await JF.requestPlaybackInfo(api, token, userId, nextChannel().id, JF.deviceProfiles.directPlay);
        if (/\/stream\/channel\//.test(attempt.ms.Path || '')) return attempt;
        await JF.closeLiveStream(api, token, attempt.liveStreamId);
        return null;
      }, { timeoutMs: 45000, intervalMs: 3000, label: 'direct-to-TVHeadend stream URL' });
      liveStreamId = directPi.liveStreamId;
      console.log(`[07|B5] Direct path -> ${(directPi.ms.Path || '').replace(/auth=[^&]+/, 'auth=…')}`);
      expect(directPi.ms.Path).toContain('http://tvheadend:9981/stream/channel/');
      expect(directPi.ms.Path).toContain('auth=');
    } finally {
      await JF.closeLiveStream(api, token, liveStreamId);
      await JF.writePluginConfig(api, token, pluginId, restoreBody);
      await JF.refreshProfileCache(api, token);
      await JF.waitForDiagnoseChannels(api, token);
    }
  });

  test('B6: DefaultTvHeadendProfile drives Resolve AND the stream URL profile', async () => {
    test.setTimeout(150000);
    const before = await JF.readPluginConfig(api, token, pluginId);
    const restoreBody = JSON.parse(JSON.stringify(before));
    let liveStreamId = null;
    try {
      for (const profile of ['test-pass', 'jellyfin']) {
        const mutated = JSON.parse(JSON.stringify(await JF.readPluginConfig(api, token, pluginId)));
        mutated.StreamingProfileSettings.DefaultTvHeadendProfile = profile;
        await JF.writePluginConfig(api, token, pluginId, mutated);
        await JF.refreshProfileCache(api, token);
        await JF.waitForDiagnoseChannels(api, token);

        const resolvedRes = await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/StreamingProfiles/Resolve`, { headers: JF.authHeaders(token) });
        const resolved = await resolvedRes.json();
        console.log(`[07|B6] Resolve() -> ${resolved.EffectiveTvHeadendProfile} (${resolved.Source})`);
        expect(resolved.EffectiveTvHeadendProfile, `Resolve reflects DefaultTvHeadendProfile=${profile}`).toBe(profile);
        expect(resolved.Source).toBe('GlobalDefault');

        const pi = await JF.pollUntil(async () => {
          const attempt = await JF.requestPlaybackInfo(api, token, userId, nextChannel().id, JF.deviceProfiles.directPlay);
          if ((attempt.ms.Path || '').includes(`profile=${profile}`)) return attempt;
          await JF.closeLiveStream(api, token, attempt.liveStreamId);
          return null;
        }, { timeoutMs: 45000, intervalMs: 3000, label: `stream URL carries profile=${profile}` });
        liveStreamId = pi.liveStreamId;
        console.log(`[07|B6] stream path carries profile=${profile}: ${(pi.ms.Path || '').includes(`profile=${profile}`)}`);
        await JF.closeLiveStream(api, token, liveStreamId);
        liveStreamId = null;
      }
    } finally {
      await JF.closeLiveStream(api, token, liveStreamId);
      await JF.writePluginConfig(api, token, pluginId, restoreBody);
      await JF.refreshProfileCache(api, token);
      await JF.waitForDiagnoseChannels(api, token);
    }
  });

  // Resolution hierarchy (StreamingProfileResolver.cs): channel override -> channel group
  // override -> client rule -> user rule -> global default -> legacy -> safe fallback. A
  // ChannelOverride for the test channel AND a ClientRule matching 'MatrixProbe' are configured
  // with DIFFERENT profiles; Resolve with both contexts set must pick the channel override
  // (higher precedence), and Resolve with only the client context must pick the client rule.
  test('B7: resolution precedence — channel override outranks a matching client rule', async () => {
    test.setTimeout(60000);
    const before = await JF.readPluginConfig(api, token, pluginId);
    const restoreBody = JSON.parse(JSON.stringify(before));
    try {
      const uuid = tvhChannels[0].Id;
      const mutated = JSON.parse(JSON.stringify(before));
      mutated.StreamingProfileSettings.ChannelOverrides = [{
        ChannelId: uuid, PlaybackMode: 'PassThrough', TvHeadendProfileName: 'test-pass', Description: 'e2e precedence channel override',
      }];
      mutated.StreamingProfileSettings.ClientRules = [{
        Enabled: true, RuleName: 'MatrixProbe rule', Description: 'e2e precedence client rule', Priority: 1,
        MatchType: 'ClientNameExact', MatchValue: 'MatrixProbe', PlaybackMode: 'Auto', TvHeadendProfileName: 'jellyfin',
      }];
      await JF.writePluginConfig(api, token, pluginId, mutated);
      await JF.refreshProfileCache(api, token);

      const bothRes = await api.get(
        `${JF.CONFIG.baseURL}/TvHeadendApi/StreamingProfiles/Resolve?channelId=${encodeURIComponent(uuid)}&clientName=${encodeURIComponent('MatrixProbe')}`,
        { headers: JF.authHeaders(token) },
      );
      const both = await bothRes.json();
      console.log(`[07|B7] channel+client context -> ${both.EffectiveTvHeadendProfile} (${both.Source})`);
      expect(both.Source, 'channel override outranks the client rule').toBe('ChannelOverride');
      expect(both.EffectiveTvHeadendProfile).toBe('test-pass');

      const clientOnlyRes = await api.get(
        `${JF.CONFIG.baseURL}/TvHeadendApi/StreamingProfiles/Resolve?clientName=${encodeURIComponent('MatrixProbe')}`,
        { headers: JF.authHeaders(token) },
      );
      const clientOnly = await clientOnlyRes.json();
      console.log(`[07|B7] client-only context -> ${clientOnly.EffectiveTvHeadendProfile} (${clientOnly.Source})`);
      expect(clientOnly.Source, 'client rule applies on its own').toBe('ClientRule');
      expect(clientOnly.EffectiveTvHeadendProfile).toBe('jellyfin');
    } finally {
      await JF.writePluginConfig(api, token, pluginId, restoreBody);
      await JF.refreshProfileCache(api, token);
      await JF.waitForDiagnoseChannels(api, token);
    }
  });

  // ImageTokenTtlMinutes regression guard (DateTime.MaxValue overflow bug).
  //
  // Investigation finding: a literal per-channel-logo tokenized relay URL is NOT independently
  // observable from this suite's black-box HTTP/browser vantage point in this environment —
  // confirmed live: (a) this synthetic backend's channel icons are external absolute URLs
  // (icon_public_url), which GuideService.ResolveTvhImageUrlAsync deliberately passes through
  // UNCHANGED instead of relaying (same gap 08-dashboard-metrics.spec.js already documents for
  // channel logos); (b) the plugin's relay-token architecture is designed so a raw token is
  // NEVER echoed anywhere an external client/log can observe it (RelayTokenHasher derives/hashes
  // with an HMAC server secret, and 08's own log-sanitization test asserts real tokens never
  // appear in logs) — so even the one real, non-expiring "reusable anonymous image token" this
  // stack has already issued (confirmed via GET /TvHeadendApi/Dashboard/Tokens ->
  // ActiveImageTokens, minted the moment GuideService resolved a TVHeadend-relative icon during
  // a channel fetch) cannot be recovered to build a URL ourselves without breaking that
  // invariant. Testing it via a real per-channel logo would therefore require either a
  // TVHeadend-backend change or a plugin debug endpoint that doesn't exist.
  //
  // Instead this test drives the SAME real, end-to-end code path that would have broken under
  // the original bug: GuideService issues an image relay token (IssueImageTokenAsync) for every
  // TVHeadend-relative icon it resolves while building the channel list; with
  // ImageTokenTtlMinutes=0 that token is persisted with ExpiresAtUtc=DateTime.MaxValue — the
  // exact shape that once overflowed when RelayTokenValidatorService added the clock-skew
  // tolerance during validation (now guarded, see RelayTokenValidatorService.cs). If that
  // regressed, channel/guide resolution — and therefore Diagnose — would degrade or throw.
  // Alongside that, the same admin-authenticated image relay endpoint 08 uses
  // (api/tvheadend/images/static/img/logo.png, a real always-present TVHeadend asset) is
  // exercised at both TTL settings to prove the shared image-relay/cache pipeline itself stays
  // fully functional.
  test('B8: ImageTokenTtlMinutes 0 vs 2 — image relay pipeline stays overflow-safe', async () => {
    test.setTimeout(90000);
    const before = await JF.readPluginConfig(api, token, pluginId);
    const restoreBody = JSON.parse(JSON.stringify(before));
    expect(before.ImageTokenTtlMinutes, 'canonical TTL is 0 (never expire)').toBe(0);
    try {
      for (const ttl of [0, 2]) {
        const mutated = JSON.parse(JSON.stringify(await JF.readPluginConfig(api, token, pluginId)));
        mutated.ImageTokenTtlMinutes = ttl;
        await JF.writePluginConfig(api, token, pluginId, mutated);

        const imgRes = await api.get(`${JF.CONFIG.baseURL}/api/tvheadend/images/static/img/logo.png`, { headers: JF.authHeaders(token) });
        console.log(`[07|B8] ttl=${ttl} image relay -> ${imgRes.status()}`);
        expect(imgRes.status(), `image relay pipeline serves 200 with ImageTokenTtlMinutes=${ttl}`).toBe(200);

        const diag = await JF.waitForDiagnoseChannels(api, token);
        console.log(`[07|B8] ttl=${ttl} Diagnose -> ${diag.OverallStatus}, ChannelCount=${diag.ChannelCount}`);
        expect(JF.HEALTHY_DIAGNOSE_STATUSES, `guide/channel resolution (which mints image tokens) stays healthy at ttl=${ttl} (got ${diag.OverallStatus})`).toContain(diag.OverallStatus);

        const tokensRes = await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/Dashboard/Tokens`, { headers: JF.authHeaders(token) });
        const tokens = await tokensRes.json();
        console.log(`[07|B8] ttl=${ttl} ActiveImageTokens=${tokens.ActiveImageTokens}`);
        expect(tokensRes.ok(), 'token accounting endpoint stays healthy (no overflow exception)').toBeTruthy();
        expect(tokens.ActiveImageTokens, 'ActiveImageTokens is a sane non-negative count').toBeGreaterThanOrEqual(0);
      }
    } finally {
      await JF.writePluginConfig(api, token, pluginId, restoreBody);
      await JF.refreshProfileCache(api, token);
      await JF.waitForDiagnoseChannels(api, token);
    }
  });

  test('B9: DefaultPlaybackMode drives Resolve.EffectivePlaybackMode', async () => {
    test.setTimeout(60000);
    const before = await JF.readPluginConfig(api, token, pluginId);
    const restoreBody = JSON.parse(JSON.stringify(before));
    try {
      for (const mode of ['Auto', 'PassThrough', 'TvHeadendTranscode', 'JellyfinTranscode']) {
        const mutated = JSON.parse(JSON.stringify(await JF.readPluginConfig(api, token, pluginId)));
        mutated.StreamingProfileSettings.DefaultPlaybackMode = mode;
        await JF.writePluginConfig(api, token, pluginId, mutated);

        const resolvedRes = await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/StreamingProfiles/Resolve`, { headers: JF.authHeaders(token) });
        const resolved = await resolvedRes.json();
        console.log(`[07|B9] DefaultPlaybackMode=${mode} -> EffectivePlaybackMode=${resolved.EffectivePlaybackMode}`);
        expect(resolved.EffectivePlaybackMode, `Resolve reflects DefaultPlaybackMode=${mode}`).toBe(mode);
      }
    } finally {
      await JF.writePluginConfig(api, token, pluginId, restoreBody);
      await JF.refreshProfileCache(api, token);
      await JF.waitForDiagnoseChannels(api, token);
    }
  });
});

// ─────────────────────────────────────────────────────────────────────────
// C) Final sanity — canonical config, healthy Diagnose, one more real stream.
// ─────────────────────────────────────────────────────────────────────────

test.describe('C) Final sanity', () => {
  test('canonical config restored, Diagnose healthy, stream still delivers bytes', async () => {
    test.setTimeout(60000);
    const api = await request.newContext({ baseURL: JF.CONFIG.baseURL, ignoreHTTPSErrors: true });
    let liveStreamId = null;
    const { token, userId } = await JF.authenticate(api);
    try {
      const pluginId = await JF.getPluginId(api, token);
      const cfg = await JF.readPluginConfig(api, token, pluginId);
      expect(JF.canonicalConfigDeviations(cfg), 'config is canonical after the full settings matrix').toEqual([]);

      const diag = await JF.waitForDiagnoseChannels(api, token);
      expect(JF.HEALTHY_DIAGNOSE_STATUSES, `Diagnose healthy (got ${diag.OverallStatus})`).toContain(diag.OverallStatus);
      expect(diag.ChannelCount, 'channels visible').toBeGreaterThan(0);

      const channel = await JF.pickChannel(api, token, userId);
      const pi = await JF.requestPlaybackInfo(api, token, userId, channel.id, JF.deviceProfiles.directPlay);
      liveStreamId = pi.liveStreamId;
      expect(pi.ms.SupportsDirectPlay, 'Direct Play still works').toBeTruthy();
      const res = await JF.fetchStreamBytes(JF.rewriteHost(pi.ms.Path, JF.CONFIG.baseURL), { minBytes: 65536, timeoutMs: 15000 });
      console.log(`[07|C] final stream check -> ${res.status} ${res.contentType}, ${res.bytes}B`);
      expect(res.status).toBe(200);
      expect(res.bytes, '64KB read').toBeGreaterThanOrEqual(65536);
    } finally {
      await JF.closeLiveStream(api, token, liveStreamId);
      await api.dispose();
    }
  });
});
