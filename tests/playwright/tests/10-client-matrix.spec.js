// Client-type compatibility matrix: each important Jellyfin client app is impersonated with
// its real client name (Authorization Client="…") and a realistic DeviceProfile, then
// (a) the playback decision is asserted and (b) the resulting artifact is proven to deliver
// real bytes (raw relay stream for direct players, HLS master+segment for transcode clients).
// Mirrors the HTTP-level expectations of the .NET AppleAvPlayerCompatibilityTests and
// CrossClientCompatibilityTests (HEAD discovery, TS sync byte, video/* content types).
const { test, expect, request } = require('@playwright/test');
const JF = require('../fixtures/jellyfin');

const HLS_TS = { Container: 'ts', Type: 'Video', VideoCodec: 'h264', AudioCodec: 'aac', Protocol: 'hls', Context: 'Streaming' };
const profile = (directPlayProfiles, transcodingProfiles = [HLS_TS]) => ({
  MaxStreamingBitrate: 120000000,
  MaxStaticBitrate: 120000000,
  DirectPlayProfiles: directPlayProfiles,
  TranscodingProfiles: transcodingProfiles,
  CodecProfiles: [], ContainerProfiles: [], SubtitleProfiles: [],
});

// One row per client app. `decision` is what Jellyfin must decide for a live mpegts/h264/aac
// source; `artifact` is how the delivery is verified ('stream' = raw relay bytes,
// 'hls' = master playlist + first transcoded segment).
const CLIENTS = [
  {
    key: 'web',
    client: 'Jellyfin Web',
    // Browsers can't play raw mpegts — MSE wants fmp4/mp4; live TV goes through HLS.
    profile: profile([{ Container: 'mp4', Type: 'Video', VideoCodec: 'h264', AudioCodec: 'aac' }]),
    decision: 'Transcode',
    artifact: 'hls',
  },
  {
    key: 'androidtv',
    client: 'Android TV',
    // ExoPlayer handles mpegts h264/aac natively -> Direct Play.
    profile: profile([{ Container: 'ts,mpegts', Type: 'Video', VideoCodec: 'h264,hevc,mpeg2video', AudioCodec: 'aac,ac3,eac3,mp2,mp3' }]),
    decision: 'DirectPlay',
    artifact: 'stream',
  },
  {
    key: 'swiftfin',
    client: 'Swiftfin iOS',
    // Apple AVPlayer: mp4/mov containers, AAC-LC only, HLS for everything else. AVPlayer also
    // probes stream URLs with HEAD before GET — verified separately below.
    profile: profile([{ Container: 'mp4,mov,m4v', Type: 'Video', VideoCodec: 'h264,hevc', AudioCodec: 'aac' }]),
    decision: 'Transcode',
    artifact: 'hls',
    headBeforeGet: true,
  },
  {
    key: 'kodi',
    client: 'Kodi',
    // Jellyfin for Kodi: widest codec support incl. raw mpegts -> Direct Play.
    profile: profile([{ Container: 'ts,mpegts,mkv,mp4,avi', Type: 'Video', VideoCodec: 'h264,hevc,mpeg2video,vc1,av1', AudioCodec: 'aac,ac3,eac3,mp2,mp3,dts,flac,opus' }]),
    decision: 'DirectPlay',
    artifact: 'stream',
  },
  {
    key: 'roku',
    client: 'Roku',
    // Roku: mkv/mp4 direct play, no raw mpegts -> remux/transcode via HLS.
    profile: profile([{ Container: 'mkv,mp4', Type: 'Video', VideoCodec: 'h264,hevc', AudioCodec: 'aac,ac3' }]),
    decision: 'NotDirectPlay',
    artifact: 'hls',
  },
  {
    key: 'webos',
    client: 'Jellyfin Web OS',
    // Smart-TV browsers (WebOS/Tizen) behave like web: HLS transcode.
    profile: profile([{ Container: 'mp4,webm', Type: 'Video', VideoCodec: 'h264,vp9', AudioCodec: 'aac,opus' }]),
    decision: 'Transcode',
    artifact: 'hls',
  },
  {
    key: 'chromecast',
    client: 'Chromecast',
    // Chromecast: mp4/webm constrained, HLS fallback.
    profile: profile([{ Container: 'mp4,webm', Type: 'Video', VideoCodec: 'h264,vp8', AudioCodec: 'aac,opus' }]),
    decision: 'Transcode',
    artifact: 'hls',
  },
  {
    key: 'firetv',
    client: 'Android TV',
    // Fire TV Stick runs the Android TV app on FireOS (same client name, different device).
    // Base-model sticks: ExoPlayer with h264-only hardware decode, aac/ac3/mp3 audio ->
    // raw mpegts h264/aac Direct Plays. Kept LAST in this list so the channel-rotation
    // slots of the pre-existing clients stay unchanged.
    deviceName: 'Fire TV Stick',
    profile: profile([{ Container: 'ts,mpegts', Type: 'Video', VideoCodec: 'h264', AudioCodec: 'aac,ac3,mp3' }]),
    decision: 'DirectPlay',
    artifact: 'stream',
  },
];

test.describe('Client-type compatibility matrix', () => {
  let api, adminToken, pluginId, channels;
  let rot = 2; // offset the rotation so token budgets don't pile on the channels 06 used most
  const nextChannel = () => channels[rot++ % channels.length];

  test.beforeAll(async () => {
    api = await request.newContext({ baseURL: JF.CONFIG.baseURL, ignoreHTTPSErrors: true });
    const admin = await JF.authenticate(api);
    adminToken = admin.token;
    pluginId = await JF.getPluginId(api, adminToken);
    expect(pluginId, 'plugin installed').toBeTruthy();
    await JF.ensureManagedProfile(api, adminToken, pluginId);
    await JF.pickChannel(api, adminToken, admin.userId);
    channels = await JF.getChannels(api, adminToken, admin.userId, 50);
  });
  test.afterAll(async () => {
    const cfg = await JF.readPluginConfig(api, adminToken, pluginId);
    expect(JF.canonicalConfigDeviations(cfg), 'config back to canonical').toEqual([]);
    await api.dispose();
  });

  for (const c of CLIENTS) {
    // Title carries the device name too: two rows can share a client name (the Android TV app
    // on Android TV vs Fire TV Stick) and Playwright rejects duplicate test titles at load time.
    test(`${c.client}${c.deviceName ? ` (${c.deviceName})` : ''}: decision + artifact bytes`, async () => {
      test.setTimeout(240000);
      const session = await JF.authenticateAs(api, c.client, `e2e-${c.key}`, c.deviceName);
      const channel = nextChannel();
      const r = await JF.requestPlaybackInfo(api, session.token, session.userId, channel.id, c.profile, { headers: session.headers });
      try {
        // (a) the decision is sane for this client type
        if (c.decision === 'DirectPlay') {
          expect(r.ms.SupportsDirectPlay, `${c.client} should Direct Play`).toBeTruthy();
        } else if (c.decision === 'Transcode') {
          expect(r.ms.SupportsDirectPlay, `${c.client} must not Direct Play raw mpegts`).toBeFalsy();
          expect(r.ms.TranscodingUrl, `${c.client} gets a transcode artifact`).toBeTruthy();
        } else { // NotDirectPlay: remux or transcode both acceptable, but never raw direct play
          expect(r.ms.SupportsDirectPlay, `${c.client} must not Direct Play raw mpegts`).toBeFalsy();
          expect(!!r.ms.TranscodingUrl || r.ms.SupportsDirectStream, `${c.client} playable artifact`).toBeTruthy();
        }

        // Swiftfin/AVPlayer: HEAD discovery on the tokenized relay stream URL. HEAD must
        // answer 200 with stream headers, return no body, and open no upstream tuner.
        // The follow-up GET runs AFTER the HLS artifact check (below) so this test never holds
        // two concurrent TVHeadend transcode subscriptions on the same channel — an overlapping
        // join can hand Jellyfin's FFmpeg an undecodable mid-stream start (decode_slice_header
        // errors, no keyframe) that wedges the HLS playlist forever.
        const streamUrl = JF.rewriteHost(r.ms.Path, JF.CONFIG.baseURL);
        if (c.headBeforeGet) {
          const head = await JF.fetchStreamBytes(streamUrl, { method: 'HEAD', timeoutMs: 10000 });
          console.log(`[10|${c.key}] HEAD -> ${head.status} ${head.contentType} accept-ranges=${head.acceptRanges}`);
          expect(head.status, 'HEAD discovery succeeds').toBe(200);
          expect(head.contentType, 'HEAD advertises the stream type').toBe('video/mp2t');
          expect(head.bytes, 'HEAD returns no body').toBe(0);
        }

        // (b) the artifact actually delivers bytes
        if (c.artifact === 'stream') {
          const res = await JF.fetchStreamBytes(streamUrl, { minBytes: 65536, timeoutMs: 20000 });
          console.log(`[10|${c.key}] ${c.client.padEnd(15)} -> ${r.method.padEnd(11)} | stream ${res.status} ${res.contentType} ${res.bytes}B in ${res.elapsedMs}ms [${channel.name}]`);
          expect(res.status).toBe(200);
          expect(res.contentType).toMatch(/^video\//);
          expect(res.bytes).toBeGreaterThanOrEqual(65536);
        } else {
          try {
            const hls = await JF.fetchHlsArtifact(api, session.token, r.ms.TranscodingUrl, session.headers);
            console.log(`[10|${c.key}] ${c.client.padEnd(15)} -> ${r.method.padEnd(11)} | hls master=${hls.masterStatus} segment=${hls.segmentBytes}B ts=${hls.segmentIsMpegTs} [${channel.name}]`);
            expect(hls.master, 'HLS master playlist').toContain('#EXTM3U');
            expect(hls.segmentBytes, 'transcoded segment bytes').toBeGreaterThan(10240);
            expect(hls.segmentIsMpegTs, 'segment is mpegts (sync byte 0x47)').toBeTruthy();
          } finally {
            await JF.stopActiveEncoding(api, session.token, r.ms.TranscodingUrl);
          }
        }

        // Swiftfin part 2: the same token must still authorize a plain GET after the HEAD and
        // after the whole FFmpeg session consumed it — HEAD must not burn the token budget in
        // a way that breaks follow-up requests. Let the encode's subscription drain first.
        if (c.headBeforeGet) {
          await new Promise((res) => setTimeout(res, 2000));
          const get = await JF.fetchStreamBytes(streamUrl, { minBytes: 32768, timeoutMs: 20000 });
          console.log(`[10|${c.key}] GET after HEAD -> ${get.status}, ${get.bytes}B`);
          expect(get.status, 'GET still authorized after HEAD (token budget intact)').toBe(200);
          expect(get.bytes).toBeGreaterThan(0);
        }
      } finally {
        await JF.closeLiveStream(api, session.token, r.liveStreamId);
        await new Promise((res) => setTimeout(res, 2000)); // let the TVHeadend subscription drain
      }
    });
  }

  test('ClientRules: a rule for one client changes only that client\'s resolved profile', async () => {
    test.setTimeout(180000);
    const before = await JF.readPluginConfig(api, adminToken, pluginId);
    const restoreBody = JSON.parse(JSON.stringify(before));
    try {
      const mutated = JSON.parse(JSON.stringify(before));
      mutated.StreamingProfileSettings.ClientRules = [{
        Enabled: true,
        RuleName: 'e2e Roku passthrough',
        Description: 'client-matrix e2e: Roku must use the test-pass profile',
        Priority: 10,
        MatchType: 'ClientNameExact',
        MatchValue: 'Roku',
        PlaybackMode: 'PassThrough',
        TvHeadendProfileName: 'test-pass',
      }];
      await JF.writePluginConfig(api, adminToken, pluginId, mutated);
      await JF.refreshProfileCache(api, adminToken);

      // Resolution endpoint: the ruled client resolves to the rule profile…
      const ruled = await (await api.get(
        `/TvHeadendApi/StreamingProfiles/Resolve?clientName=${encodeURIComponent('Roku')}`,
        { headers: JF.authHeaders(adminToken) },
      )).json();
      console.log(`[10|rules] Roku -> ${ruled.EffectiveTvHeadendProfile} (${ruled.Source}, rule=${ruled.MatchedRuleName})`);
      expect(ruled.EffectiveTvHeadendProfile).toBe('test-pass');
      expect(ruled.Source).toBe('ClientRule');
      expect(ruled.MatchedRuleName).toBe('e2e Roku passthrough');

      // …and every other client keeps the global default.
      const other = await (await api.get(
        `/TvHeadendApi/StreamingProfiles/Resolve?clientName=${encodeURIComponent('Android TV')}`,
        { headers: JF.authHeaders(adminToken) },
      )).json();
      console.log(`[10|rules] Android TV -> ${other.EffectiveTvHeadendProfile} (${other.Source})`);
      expect(other.EffectiveTvHeadendProfile).toBe('jellyfin');
      expect(other.Source).toBe('GlobalDefault');

      // End-to-end proof: a PlaybackInfo issued by the ruled client carries the rule profile
      // in its stream URL (poll past the plugin's 10s stream-build reuse window).
      // Skip one rotation slot first: with 8 nextChannel() calls per run over 5 channels the
      // rules test would otherwise always land on the same channel the NEXT run's Swiftfin
      // test uses (7 ≡ 2 mod 5), stacking heavy transcode joins on one tuner back-to-back.
      nextChannel();
      const roku = await JF.authenticateAs(api, 'Roku', 'e2e-roku-rule');
      const ruledPi = await JF.pollUntil(async () => {
        const attempt = await JF.requestPlaybackInfo(api, roku.token, roku.userId, nextChannel().id, CLIENTS.find((x) => x.key === 'roku').profile, { headers: roku.headers });
        if ((attempt.ms.Path || '').includes('profile=test-pass')) return attempt;
        await JF.closeLiveStream(api, roku.token, attempt.liveStreamId);
        return null;
      }, { timeoutMs: 45000, intervalMs: 3000, label: 'Roku stream URL carries profile=test-pass' });
      console.log(`[10|rules] Roku PlaybackInfo path=${(ruledPi.ms.Path || '').replace(/token=[^&]+/, 'token=…')}`);
      await JF.closeLiveStream(api, roku.token, ruledPi.liveStreamId);
    } finally {
      await JF.writePluginConfig(api, adminToken, pluginId, restoreBody);
      await JF.refreshProfileCache(api, adminToken);
      await JF.waitForDiagnoseChannels(api, adminToken);
    }
  });
  test('device rule: Fire TV Stick routes by DeviceName while other Android TV devices keep the default', async () => {
    test.setTimeout(240000);
    const before = await JF.readPluginConfig(api, adminToken, pluginId);
    const restoreBody = JSON.parse(JSON.stringify(before));
    try {
      const mutated = JSON.parse(JSON.stringify(before));
      mutated.StreamingProfileSettings.ClientRules = [{
        Enabled: true,
        RuleName: 'e2e FireTV passthrough',
        Description: 'client-matrix e2e: Fire TV Stick must use the test-pass profile',
        Priority: 10,
        MatchType: 'DeviceNameExact',
        MatchValue: 'Fire TV Stick',
        PlaybackMode: 'PassThrough',
        TvHeadendProfileName: 'test-pass',
      }];
      await JF.writePluginConfig(api, adminToken, pluginId, mutated);
      await JF.refreshProfileCache(api, adminToken);

      // Resolution: the ruled DEVICE resolves to the rule profile…
      const ruled = await (await api.get(
        `/TvHeadendApi/StreamingProfiles/Resolve?deviceName=${encodeURIComponent('Fire TV Stick')}`,
        { headers: JF.authHeaders(adminToken) },
      )).json();
      console.log(`[10|device-rule] Fire TV Stick -> ${ruled.EffectiveTvHeadendProfile} (${ruled.Source}, rule=${ruled.MatchedRuleName})`);
      expect(ruled.EffectiveTvHeadendProfile).toBe('test-pass');
      expect(ruled.MatchedRuleName).toBe('e2e FireTV passthrough');

      // …while a DIFFERENT device of the same app keeps the global default.
      const shield = await (await api.get(
        `/TvHeadendApi/StreamingProfiles/Resolve?deviceName=${encodeURIComponent('SHIELD Android TV')}&clientName=${encodeURIComponent('Android TV')}`,
        { headers: JF.authHeaders(adminToken) },
      )).json();
      console.log(`[10|device-rule] SHIELD -> ${shield.EffectiveTvHeadendProfile} (${shield.Source})`);
      expect(shield.EffectiveTvHeadendProfile).toBe('jellyfin');

      // End-to-end: a real Fire TV session's stream URL carries the rule profile
      // (poll past the plugin's 10s stream-build reuse window).
      nextChannel();
      const firetv = await JF.authenticateAs(api, 'Android TV', 'e2e-firetv-rule', 'Fire TV Stick');
      const fireProfile = CLIENTS.find((x) => x.key === 'firetv').profile;
      const ruledPi = await JF.pollUntil(async () => {
        const attempt = await JF.requestPlaybackInfo(api, firetv.token, firetv.userId, nextChannel().id, fireProfile, { headers: firetv.headers });
        if ((attempt.ms.Path || '').includes('profile=test-pass')) return attempt;
        await JF.closeLiveStream(api, firetv.token, attempt.liveStreamId);
        return null;
      }, { timeoutMs: 45000, intervalMs: 3000, label: 'Fire TV stream URL carries profile=test-pass' });
      console.log(`[10|device-rule] FireTV PlaybackInfo path=${(ruledPi.ms.Path || '').replace(/token=[^&]+/, 'token=…')}`);
      await JF.closeLiveStream(api, firetv.token, ruledPi.liveStreamId);
    } finally {
      await JF.writePluginConfig(api, adminToken, pluginId, restoreBody);
      await JF.refreshProfileCache(api, adminToken);
      await JF.waitForDiagnoseChannels(api, adminToken);
    }
  });
});
