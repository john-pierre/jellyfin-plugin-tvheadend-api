// Extended client-type coverage — builds on 10-client-matrix.spec.js (which byte-verifies
// Jellyfin Web, Android TV, Swiftfin, Kodi, Roku, WebOS, Chromecast, Fire TV):
//
// A) Six MORE real client apps are impersonated with their actual client names and
//    realistic device profiles. Direct players are proven with raw relay stream bytes.
//    The per-client HLS transcode pipeline is already byte-verified in 10 for five apps,
//    so among the new transcode clients ONE representative (Jellyfin Mobile iOS) fetches a
//    real segment and the other asserts the decision + HLS TranscodingUrl shape.
// B) Per-client streaming-profile RULES end-to-end with REAL client identities: a
//    ClientNameExact rule and a DeviceNameContains rule are configured, then each client
//    authenticates and the profile carried in its ACTUAL stream URL (plus Resolve()) must
//    follow its rule while unmatched clients stay on the global default. This is the
//    productive purpose of client rules — 07/B7 only covered rule precedence for one
//    synthetic name.
// C) The KnownClients endpoint (feeds the config UI's rule dropdowns) must list the
//    impersonated client and device names afterwards.
const { test, expect, request } = require('@playwright/test');
const JF = require('../fixtures/jellyfin');

test.describe.configure({ mode: 'serial' });

const HLS_TS = { Container: 'ts', Type: 'Video', VideoCodec: 'h264', AudioCodec: 'aac', Protocol: 'hls', Context: 'Streaming' };
const profile = (directPlayProfiles) => ({
  MaxStreamingBitrate: 120000000,
  MaxStaticBitrate: 120000000,
  DirectPlayProfiles: directPlayProfiles,
  TranscodingProfiles: [HLS_TS],
  CodecProfiles: [], ContainerProfiles: [], SubtitleProfiles: [],
});

// One row per additional client app (none of these are in 10-client-matrix).
const CLIENTS = [
  {
    key: 'jmp',
    client: 'Jellyfin Media Player',
    // Desktop player embeds mpv — plays raw mpegts with virtually any codec directly.
    profile: profile([{ Container: 'ts,mpegts,mkv,mp4', Type: 'Video', VideoCodec: 'h264,hevc,mpeg2video,av1', AudioCodec: 'aac,ac3,eac3,mp2,mp3,dts,opus,flac' }]),
    decision: 'DirectPlay',
    artifact: 'stream',
  },
  {
    key: 'findroid',
    client: 'Findroid',
    // Third-party Android app on ExoPlayer — mpegts h264/aac Direct Plays.
    profile: profile([{ Container: 'ts,mpegts', Type: 'Video', VideoCodec: 'h264,hevc', AudioCodec: 'aac,ac3,mp3' }]),
    decision: 'DirectPlay',
    artifact: 'stream',
  },
  {
    key: 'streamyfin',
    client: 'Streamyfin',
    // Streamyfin ships a VLC-based player — raw mpegts Direct Plays.
    profile: profile([{ Container: 'ts,mpegts,mkv', Type: 'Video', VideoCodec: 'h264,hevc', AudioCodec: 'aac,ac3,eac3,mp2,mp3' }]),
    decision: 'DirectPlay',
    artifact: 'stream',
  },
  {
    key: 'infuse',
    client: 'Infuse',
    // Infuse (Apple) brings its own demux/decode engine — plays ts/mkv directly.
    profile: profile([{ Container: 'ts,mpegts,mkv,mp4,mov', Type: 'Video', VideoCodec: 'h264,hevc', AudioCodec: 'aac,ac3,eac3,dts' }]),
    decision: 'DirectPlay',
    artifact: 'stream',
  },
  {
    key: 'jfmobile',
    client: 'Jellyfin Mobile',
    // iOS app wraps AVPlayer: mp4/mov only, live TV goes through HLS transcode.
    // Representative HLS byte check for the new rows (see file header).
    profile: profile([{ Container: 'mp4,mov,m4v', Type: 'Video', VideoCodec: 'h264,hevc', AudioCodec: 'aac' }]),
    decision: 'Transcode',
    artifact: 'hls',
  },
  {
    key: 'tizen',
    client: 'Jellyfin for Tizen',
    // Samsung TV web runtime — browser-like constraints, HLS transcode.
    profile: profile([{ Container: 'mp4,webm', Type: 'Video', VideoCodec: 'h264,vp9', AudioCodec: 'aac,opus' }]),
    decision: 'Transcode',
    artifact: 'hls-shape', // decision + TranscodingUrl shape only (pipeline bytes proven in 10 + jfmobile row)
  },
];

test.describe('Extended client types', () => {
  let api, adminToken, adminUserId, pluginId, channels;
  let rot = 5; // rotate channels away from the slots 06/10 hammer most
  const nextChannel = () => channels[rot++ % channels.length];

  test.beforeAll(async () => {
    api = await request.newContext({ baseURL: JF.CONFIG.baseURL, ignoreHTTPSErrors: true });
    const admin = await JF.authenticate(api);
    adminToken = admin.token;
    adminUserId = admin.userId;
    pluginId = await JF.getPluginId(api, adminToken);
    expect(pluginId, 'plugin installed').toBeTruthy();
    await JF.ensureManagedProfile(api, adminToken, pluginId);
    await JF.pickChannel(api, adminToken, adminUserId);
    channels = await JF.getChannels(api, adminToken, adminUserId, 50);
  });

  test.afterAll(async () => {
    const cfg = await JF.readPluginConfig(api, adminToken, pluginId);
    expect(JF.canonicalConfigDeviations(cfg), 'config back to canonical').toEqual([]);
    await JF.waitForDiagnoseChannels(api, adminToken);
    await api.dispose();
  });

  // ── A) decision + artifact per client ──────────────────────────────────
  for (const c of CLIENTS) {
    test(`${c.client}: playback decision + delivery`, async () => {
      test.setTimeout(240000);
      const session = await JF.authenticateAs(api, c.client, `e2e-16-${c.key}`);
      const channel = nextChannel();
      let liveStreamId = null;
      let transcodingUrl = null;
      try {
        const pi = await JF.requestPlaybackInfo(api, session.token, session.userId, channel.id, c.profile, { headers: session.headers });
        liveStreamId = pi.liveStreamId;
        console.log(`[16|${c.key}] ${c.client} on "${channel.name}" -> ${pi.method}`);
        expect(pi.method, `${c.client} playback decision`).toBe(c.decision);

        if (c.artifact === 'stream') {
          expect(pi.ms.Path, 'direct player gets the relay stream URL').toContain('/api/tvheadend/relay/stream/');
          const res = await JF.fetchStreamBytes(JF.rewriteHost(pi.ms.Path, JF.CONFIG.baseURL), { minBytes: 65536, timeoutMs: 20000 });
          console.log(`[16|${c.key}] stream -> ${res.status} ${res.contentType}, ${res.bytes}B`);
          expect(res.status, 'relay stream serves 200').toBe(200);
          expect(res.contentType, 'video content type').toMatch(/^video\//);
          expect(res.bytes, '64KB of live video').toBeGreaterThanOrEqual(65536);
        } else {
          transcodingUrl = pi.ms.TranscodingUrl;
          expect(transcodingUrl, 'transcode client gets an HLS TranscodingUrl').toBeTruthy();
          expect(transcodingUrl, 'HLS master playlist URL').toMatch(/master\.m3u8/);
          if (c.artifact === 'hls') {
            const hls = await JF.fetchHlsArtifact(api, session.token, transcodingUrl, session.headers);
            console.log(`[16|${c.key}] HLS -> master ${hls.masterStatus}, segment ${hls.segmentBytes}B (TS=${hls.segmentIsMpegTs})`);
            expect(hls.masterStatus, 'HLS master serves 200').toBe(200);
            expect(hls.segmentBytes, 'a real transcoded segment arrived').toBeGreaterThan(10240);
            expect(hls.segmentIsMpegTs, 'segment is MPEG-TS').toBeTruthy();
          } else {
            console.log(`[16|${c.key}] TranscodingUrl shape verified (bytes covered by 10 + jfmobile row)`);
          }
        }
      } finally {
        if (transcodingUrl) await JF.stopActiveEncoding(api, session.token, transcodingUrl);
        await JF.closeLiveStream(api, session.token, liveStreamId);
        await new Promise((r) => setTimeout(r, 1500));
      }
    });
  }

  // ── B) per-client profile rules with real client identities ────────────
  test('client rules route real client identities to their profiles', async () => {
    test.setTimeout(240000);
    const before = await JF.readPluginConfig(api, adminToken, pluginId);
    const restoreBody = JSON.parse(JSON.stringify(before));
    let liveStreamId = null;
    try {
      const mutated = JSON.parse(JSON.stringify(before));
      mutated.StreamingProfileSettings.ClientRules = [{
        Enabled: true, RuleName: 'e2e Findroid rule', Description: 'client-types e2e', Priority: 1,
        MatchType: 'ClientNameExact', MatchValue: 'Findroid', PlaybackMode: 'Auto', TvHeadendProfileName: 'test-pass',
      }, {
        Enabled: true, RuleName: 'e2e Shield rule', Description: 'client-types e2e', Priority: 2,
        MatchType: 'DeviceNameContains', MatchValue: 'Shield', PlaybackMode: 'Auto', TvHeadendProfileName: 'pass',
      }];
      await JF.writePluginConfig(api, adminToken, pluginId, mutated);
      await JF.refreshProfileCache(api, adminToken);

      // Resolve() reflects both rules and leaves unmatched clients on the global default.
      const cases = [
        { q: 'clientName=Findroid', profile: 'test-pass', source: 'ClientRule' },
        { q: 'deviceName=NVIDIA%20Shield%20Pro', profile: 'pass', source: 'ClientRule' },
        { q: 'clientName=Jellyfin%20Media%20Player', profile: 'jellyfin', source: 'GlobalDefault' },
      ];
      for (const c of cases) {
        const r = await (await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/StreamingProfiles/Resolve?${c.q}`, { headers: JF.authHeaders(adminToken) })).json();
        console.log(`[16|rules] Resolve ${c.q} -> ${r.EffectiveTvHeadendProfile} (${r.Source})`);
        expect(r.EffectiveTvHeadendProfile, `Resolve(${c.q})`).toBe(c.profile);
        expect(r.Source, `Resolve(${c.q}) source`).toBe(c.source);
      }

      // The rule fires end-to-end for the REAL client identity: Findroid's actual stream URL
      // carries profile=test-pass.
      const findroid = await JF.authenticateAs(api, 'Findroid', 'e2e-16-rule-findroid');
      const ruledPi = await JF.pollUntil(async () => {
        const attempt = await JF.requestPlaybackInfo(api, findroid.token, findroid.userId, nextChannel().id, CLIENTS[1].profile, { headers: findroid.headers });
        if ((attempt.ms.Path || '').includes('profile=test-pass')) return attempt;
        await JF.closeLiveStream(api, findroid.token, attempt.liveStreamId);
        return null;
      }, { timeoutMs: 45000, intervalMs: 3000, label: 'Findroid stream URL carries profile=test-pass' });
      liveStreamId = ruledPi.liveStreamId;
      console.log(`[16|rules] Findroid path -> ${(ruledPi.ms.Path || '').replace(/token=[^&]+/, 'token=…')}`);
      const bytes = await JF.fetchStreamBytes(JF.rewriteHost(ruledPi.ms.Path, JF.CONFIG.baseURL), { minBytes: 65536, timeoutMs: 20000 });
      expect(bytes.status, 'ruled profile stream serves bytes').toBe(200);
      expect(bytes.bytes).toBeGreaterThanOrEqual(65536);
      await JF.closeLiveStream(api, findroid.token, liveStreamId);
      liveStreamId = null;

      // An unmatched client keeps the managed default in its stream URL.
      const jmp = await JF.authenticateAs(api, 'Jellyfin Media Player', 'e2e-16-rule-jmp');
      const defaultPi = await JF.pollUntil(async () => {
        const attempt = await JF.requestPlaybackInfo(api, jmp.token, jmp.userId, nextChannel().id, CLIENTS[0].profile, { headers: jmp.headers });
        if ((attempt.ms.Path || '').includes('profile=jellyfin')) return attempt;
        await JF.closeLiveStream(api, jmp.token, attempt.liveStreamId);
        return null;
      }, { timeoutMs: 45000, intervalMs: 3000, label: 'unmatched client stays on profile=jellyfin' });
      liveStreamId = defaultPi.liveStreamId;
      console.log(`[16|rules] JMP path -> ${(defaultPi.ms.Path || '').replace(/token=[^&]+/, 'token=…')}`);
    } finally {
      await JF.closeLiveStream(api, adminToken, liveStreamId);
      await JF.writePluginConfig(api, adminToken, pluginId, restoreBody);
      await JF.refreshProfileCache(api, adminToken);
      await JF.waitForDiagnoseChannels(api, adminToken);
    }
  });

  // ── B2) user rules + channel-group overrides (completes the rules matrix:
  // channel override + client-rule precedence live in 07/B7, client/device rules in the
  // test above — user rules and group overrides had no e2e coverage anywhere) ──────────
  test('user rules and channel-group overrides resolve and apply end-to-end', async () => {
    test.setTimeout(240000);
    const before = await JF.readPluginConfig(api, adminToken, pluginId);
    const restoreBody = JSON.parse(JSON.stringify(before));
    let liveStreamId = null;
    try {
      // A real channel group from the live guide (bootstrap tags: Documentary, Kids, …).
      const groups = await (await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/StreamingProfiles/ChannelGroups`, { headers: JF.authHeaders(adminToken) })).json();
      expect(groups.length, 'channel groups exist').toBeGreaterThan(0);
      const groupName = groups[0].Name;

      const mutated = JSON.parse(JSON.stringify(before));
      mutated.StreamingProfileSettings.UserRules = [{
        Enabled: true, RuleName: 'e2e admin user rule', Description: 'client-types e2e', Priority: 1,
        MatchType: 'UserIdExact', MatchValue: adminUserId, PlaybackMode: 'Auto', TvHeadendProfileName: 'test-pass',
      }];
      mutated.StreamingProfileSettings.ChannelGroupOverrides = [{
        ChannelGroup: groupName, PlaybackMode: 'Auto', TvHeadendProfileName: 'pass', Description: 'client-types e2e',
      }];
      await JF.writePluginConfig(api, adminToken, pluginId, mutated);
      await JF.refreshProfileCache(api, adminToken);

      // Resolve: user rule fires, group override fires, and the group override OUTRANKS the
      // user rule when both contexts are present (resolution hierarchy).
      const cases = [
        { q: `userId=${adminUserId}`, profile: 'test-pass', source: 'UserRule' },
        { q: `channelGroup=${encodeURIComponent(groupName)}`, profile: 'pass', source: 'ChannelGroupOverride' },
        { q: `channelGroup=${encodeURIComponent(groupName)}&userId=${adminUserId}`, profile: 'pass', source: 'ChannelGroupOverride' },
      ];
      for (const c of cases) {
        const r = await (await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/StreamingProfiles/Resolve?${c.q}`, { headers: JF.authHeaders(adminToken) })).json();
        console.log(`[16|userrules] Resolve ${c.q} -> ${r.EffectiveTvHeadendProfile} (${r.Source})`);
        expect(r.EffectiveTvHeadendProfile, `Resolve(${c.q})`).toBe(c.profile);
        expect(r.Source, `Resolve(${c.q}) source`).toBe(c.source);
      }

      // End-to-end: a REAL playback session of the admin user picks up the user rule — the
      // actual stream URL carries the ruled profile (the resolver reads the user id from the
      // live session context, not from a query parameter).
      const pi = await JF.pollUntil(async () => {
        const attempt = await JF.requestPlaybackInfo(api, adminToken, adminUserId, nextChannel().id, CLIENTS[0].profile);
        if ((attempt.ms.Path || '').includes('profile=test-pass')) return attempt;
        await JF.closeLiveStream(api, adminToken, attempt.liveStreamId);
        return null;
      }, { timeoutMs: 45000, intervalMs: 3000, label: 'admin playback stream URL carries the user-rule profile' });
      liveStreamId = pi.liveStreamId;
      console.log(`[16|userrules] admin path -> ${(pi.ms.Path || '').replace(/token=[^&]+/, 'token=…')}`);
      const bytes = await JF.fetchStreamBytes(JF.rewriteHost(pi.ms.Path, JF.CONFIG.baseURL), { minBytes: 65536, timeoutMs: 20000 });
      expect(bytes.status, 'user-ruled stream serves bytes').toBe(200);
      expect(bytes.bytes).toBeGreaterThanOrEqual(65536);
    } finally {
      await JF.closeLiveStream(api, adminToken, liveStreamId);
      await JF.writePluginConfig(api, adminToken, pluginId, restoreBody);
      await JF.refreshProfileCache(api, adminToken);
      await JF.waitForDiagnoseChannels(api, adminToken);
    }
  });

  // ── C) KnownClients surfaces the impersonated identities ───────────────
  test('KnownClients lists the impersonated client names for the rules UI', async () => {
    const res = await api.get(`${JF.CONFIG.baseURL}/TvHeadendApi/KnownClients`, { headers: JF.authHeaders(adminToken) });
    expect(res.ok(), `KnownClients status ${res.status()}`).toBeTruthy();
    const known = await res.json();
    expect(Array.isArray(known.Clients), 'Clients array present').toBeTruthy();
    expect(Array.isArray(known.Users) && known.Users.length, 'Users present').toBeTruthy();
    for (const name of CLIENTS.map((c) => c.client)) {
      expect(known.Clients, `KnownClients contains "${name}"`).toContain(name);
    }
    console.log(`[16|known] ${known.Clients.length} clients, ${known.Devices.length} devices, ${known.Users.length} users`);
  });
});
