// Exhaustive streaming/transcoding decision-and-delivery matrix:
//   A) play-method decisions for five crafted client shapes under both TVHeadend default
//      profiles ('pass' passthrough and 'jellyfin' managed transcode),
//   B) delivery modes (Relay proxy vs DirectToTvheadend URLs) with real byte verification,
//   C) relay token security on/off against the open stream endpoint,
//   D) real-browser playback + channel zapping (tuner/subscription-leak regression).
// Every config mutation is read-modify-write with an exact-restore body built BEFORE the
// mutation, applied in finally. After each describe the config must be canonical again.
const { test, expect, request } = require('@playwright/test');
const JF = require('../fixtures/jellyfin');

// Client shapes used by the play-method matrix (the source is mpegts/h264/aac either way:
// 'pass' forwards it untouched, 'jellyfin' transcodes to the same codecs server-side).
const HLS_TS = { Container: 'ts', Type: 'Video', VideoCodec: 'h264', AudioCodec: 'aac', Protocol: 'hls', Context: 'Streaming' };
const shape = (dp, maxBitrate = 120000000) => ({
  MaxStreamingBitrate: maxBitrate,
  MaxStaticBitrate: maxBitrate,
  DirectPlayProfiles: dp,
  TranscodingProfiles: [HLS_TS],
  CodecProfiles: [], ContainerProfiles: [], SubtitleProfiles: [],
});
const matrixClients = {
  directPlay: shape([{ Container: 'ts,mpegts', Type: 'Video', VideoCodec: 'h264', AudioCodec: 'aac,mp2,ac3,eac3,mp3' }]),
  containerMismatch: shape([{ Container: 'mp4,fmp4', Type: 'Video', VideoCodec: 'h264', AudioCodec: 'aac' }]),
  browser: shape([{ Container: 'mp4', Type: 'Video', VideoCodec: 'h264', AudioCodec: 'aac' }]),
  audioMp3Only: shape([{ Container: 'ts,mpegts', Type: 'Video', VideoCodec: 'h264', AudioCodec: 'mp3' }]),
  bitrate300k: shape([{ Container: 'ts,mpegts', Type: 'Video', VideoCodec: 'h264', AudioCodec: 'aac' }], 300000),
};

test.describe('A) Play-method matrix × TVHeadend default profile', () => {
  let api, token, userId, pluginId, channels;
  let rot = 0;
  const nextChannel = () => channels[rot++ % channels.length];

  test.beforeAll(async () => {
    api = await request.newContext({ baseURL: JF.CONFIG.baseURL, ignoreHTTPSErrors: true });
    ({ token, userId } = await JF.authenticate(api));
    pluginId = await JF.getPluginId(api, token);
    expect(pluginId, 'plugin installed').toBeTruthy();
    await JF.ensureManagedProfile(api, token, pluginId);
    await JF.pickChannel(api, token, userId); // ensures the guide is populated
    channels = await JF.getChannels(api, token, userId, 50);
  });
  test.afterAll(async () => {
    // Release TVHeadend tuner slots held by lingering web-client transcodes.
    await JF.stopAllPlaybackSessions(api, token);
    const cfg = await JF.readPluginConfig(api, token, pluginId);
    expect(JF.canonicalConfigDeviations(cfg), 'config back to canonical').toEqual([]);
    await api.dispose();
  });

  for (const profile of ['pass', 'jellyfin']) {
    test(`decision matrix with DefaultTvHeadendProfile='${profile}'`, async () => {
      test.setTimeout(360000);
      const before = await JF.readPluginConfig(api, token, pluginId);
      const restoreBody = JSON.parse(JSON.stringify(before));
      try {
        const mutated = JSON.parse(JSON.stringify(before));
        mutated.StreamingProfileSettings.DefaultTvHeadendProfile = profile;
        await JF.writePluginConfig(api, token, pluginId, mutated);
        await JF.refreshProfileCache(api, token);
        await JF.waitForDiagnoseChannels(api, token);

        // 1. Direct-Play-capable client (ts + h264 + aac). Poll until a freshly built stream
        // URL carries the new profile — the plugin reuses stream builds for ~10s.
        const r1 = await JF.pollUntil(async () => {
          const r = await JF.requestPlaybackInfo(api, token, userId, nextChannel().id, matrixClients.directPlay);
          if ((r.ms.Path || '').includes(`profile=${profile}`)) return r;
          await JF.closeLiveStream(api, token, r.liveStreamId);
          return null;
        }, { timeoutMs: 45000, intervalMs: 3000, label: `stream URL carries profile=${profile}` });
        const codecs = (r1.ms.MediaStreams || []).map((s) => `${s.Type}:${s.Codec}`).join(',');
        console.log(`[A|${profile}] directPlay        -> ${r1.method} (${codecs})`);
        expect(r1.ms.SupportsDirectPlay, 'Direct Play supported').toBeTruthy();
        expect(codecs).toMatch(/Video:h264/i);
        expect(codecs).toMatch(/Audio:aac/i);
        await JF.closeLiveStream(api, token, r1.liveStreamId);

        // 2. Container-mismatch client (mp4-only direct play, codecs fine, HLS fallback).
        // Jellyfin 10.10 effectively disables plain-HTTP DirectStream for live TV, so the
        // consistent artifact is an HLS remux/transcode with TranscodeReasons=ContainerNotSupported.
        const r2 = await JF.requestPlaybackInfo(api, token, userId, nextChannel().id, matrixClients.containerMismatch);
        console.log(`[A|${profile}] containerMismatch -> ${r2.method} (reasons=${new URL(r2.ms.TranscodingUrl || '/x', JF.CONFIG.baseURL).searchParams.get('TranscodeReasons') || 'n/a'})`);
        const playable2 = r2.ms.SupportsDirectStream || r2.ms.SupportsDirectPlay || !!r2.ms.TranscodingUrl;
        expect(playable2, 'a playable artifact is produced').toBeTruthy();
        if (r2.ms.TranscodingUrl) {
          const tu = new URL(r2.ms.TranscodingUrl, JF.CONFIG.baseURL);
          expect(tu.pathname, 'HLS master playlist artifact').toMatch(/master\.m3u8$/);
          expect(tu.searchParams.get('TranscodeReasons'), 'container was the (only) reason')
            .toBe('ContainerNotSupported');
          expect(tu.searchParams.get('LiveStreamId'), 'transcode bound to the opened live stream')
            .toBe(r2.ms.LiveStreamId);
        } else {
          expect(r2.ms.SupportsDirectStream, 'no TranscodingUrl implies DirectStream remux').toBeTruthy();
        }
        await JF.closeLiveStream(api, token, r2.liveStreamId);

        // 3. Browser-like client: must transcode; prove FFmpeg really runs by pulling the
        // master playlist AND the first media segment, then stop the encode.
        const r3 = await JF.requestPlaybackInfo(api, token, userId, nextChannel().id, matrixClients.browser);
        expect(r3.ms.TranscodingUrl, 'browser client gets a TranscodingUrl').toBeTruthy();
        try {
          const hls = await JF.fetchHlsArtifact(api, token, r3.ms.TranscodingUrl);
          console.log(`[A|${profile}] browser           -> ${r3.method} (master=${hls.masterStatus}, segment=${hls.segmentBytes}B, ts=${hls.segmentIsMpegTs})`);
          expect(hls.master, 'valid HLS master playlist').toContain('#EXTM3U');
          expect(hls.segmentBytes, 'a real transcoded segment arrived').toBeGreaterThan(10240);
          expect(hls.segmentIsMpegTs, 'segment starts with the TS sync byte').toBeTruthy();
        } finally {
          await JF.stopActiveEncoding(api, token, r3.ms.TranscodingUrl);
          await JF.closeLiveStream(api, token, r3.liveStreamId);
        }
        await new Promise((r) => setTimeout(r, 2000)); // let the subscription drain

        // 4. Audio-incompatible client (video ok, audio mp3 only) -> must not Direct Play;
        // a playable (audio-)transcode artifact must exist.
        const r4 = await JF.requestPlaybackInfo(api, token, userId, nextChannel().id, matrixClients.audioMp3Only);
        console.log(`[A|${profile}] audioMp3Only      -> ${r4.method} (tc=${!!r4.ms.TranscodingUrl})`);
        expect(r4.ms.SupportsDirectPlay, 'aac source must not direct-play on an mp3-only client').toBeFalsy();
        expect(!!r4.ms.TranscodingUrl || r4.ms.SupportsDirectStream, 'playable artifact exists').toBeTruthy();
        await JF.closeLiveStream(api, token, r4.liveStreamId);

        // 5. Bitrate-constrained client (300 kbps cap). Jellyfin core deliberately skips the
        // bitrate cap for REMOTE sources (StreamBuilder.IsBitrateLimitExceeded returns false
        // when MediaSourceInfo.IsRemote) and the plugin's relay/direct URLs are always remote,
        // so the cap does NOT force a transcode — DirectPlay is the correct core decision here.
        const r5 = await JF.requestPlaybackInfo(api, token, userId, nextChannel().id, matrixClients.bitrate300k);
        console.log(`[A|${profile}] bitrate300k       -> ${r5.method} (srcBitrate=${r5.ms.Bitrate ?? 'n/a'}; remote source exempt from cap)`);
        expect(r5.ms.SupportsDirectPlay, 'remote live source bypasses the bitrate cap (Jellyfin core behavior)').toBeTruthy();
        await JF.closeLiveStream(api, token, r5.liveStreamId);
        // …but when the client cannot direct-play at all, the cap must shape the transcode:
        const r5b = await JF.requestPlaybackInfo(api, token, userId, nextChannel().id, matrixClients.bitrate300k, { directPlay: false, directStream: false });
        expect(r5b.ms.TranscodingUrl, 'bitrate-capped transcode artifact').toBeTruthy();
        const vb = Number(new URL(r5b.ms.TranscodingUrl, JF.CONFIG.baseURL).searchParams.get('VideoBitrate'));
        console.log(`[A|${profile}] bitrate300k(f)    -> Transcode (VideoBitrate=${vb})`);
        expect(vb, 'video bitrate honors the 300k cap').toBeLessThanOrEqual(300000);
        await JF.closeLiveStream(api, token, r5b.liveStreamId);
      } finally {
        await JF.writePluginConfig(api, token, pluginId, restoreBody);
        await JF.refreshProfileCache(api, token);
        await JF.waitForDiagnoseChannels(api, token);
      }
    });
  }
});

test.describe('B) Delivery-mode matrix (Relay vs DirectToTvheadend)', () => {
  const TVH_URL = process.env.TVH_URL || 'http://localhost:19981';
  let api, token, userId, pluginId, channels;
  let rot = 1;
  const nextChannel = () => channels[rot++ % channels.length];

  test.beforeAll(async () => {
    api = await request.newContext({ baseURL: JF.CONFIG.baseURL, ignoreHTTPSErrors: true });
    ({ token, userId } = await JF.authenticate(api));
    pluginId = await JF.getPluginId(api, token);
    await JF.ensureManagedProfile(api, token, pluginId);
    await JF.pickChannel(api, token, userId);
    channels = await JF.getChannels(api, token, userId, 50);
  });
  test.afterAll(async () => {
    const cfg = await JF.readPluginConfig(api, token, pluginId);
    expect(JF.canonicalConfigDeviations(cfg), 'config back to canonical').toEqual([]);
    await api.dispose();
  });

  test('Relay mode: tokenized relay path, bytes arrive without Range', async () => {
    // Relay is the canonical default — no mutation needed.
    const cfg = await JF.readPluginConfig(api, token, pluginId);
    expect(cfg.StreamDeliveryMode).toBe('Relay');

    const { ms, method, liveStreamId } = await JF.requestPlaybackInfo(api, token, userId, nextChannel().id, matrixClients.directPlay);
    try {
      console.log(`[B|Relay] ${method} path=${(ms.Path || '').replace(/token=[^&]+/, 'token=…')}`);
      expect(ms.Path).toContain('/api/tvheadend/relay/stream/');
      expect(ms.Path).toContain('token=');

      // Fetch the stream exactly like a client would (no Range header) via the published port.
      const res = await JF.fetchStreamBytes(JF.rewriteHost(ms.Path, JF.CONFIG.baseURL), { minBytes: 65536, timeoutMs: 15000 });
      console.log(`[B|Relay] GET -> ${res.status} ${res.contentType}, ${res.bytes}B in ${res.elapsedMs}ms (first byte ${res.firstByteMs}ms)`);
      expect(res.status, 'relay stream reachable').toBe(200);
      expect(res.contentType, 'video content type').toMatch(/^video\//);
      expect(res.bytes, 'first 64KB arrived within 15s').toBeGreaterThanOrEqual(65536);
    } finally {
      await JF.closeLiveStream(api, token, liveStreamId);
    }
  });

  test('DirectToTvheadend mode: direct URL with profile + alphanumeric auth token', async () => {
    test.setTimeout(240000);
    const before = await JF.readPluginConfig(api, token, pluginId);
    const restoreBody = JSON.parse(JSON.stringify(before)); // exact restore incl. prior AuthToken
    try {
      // Direct mode needs a TVHeadend auth token; generate one when the stack has none yet
      // (GenerateAuthToken persists it — the finally-restore reverts to the prior value).
      if (!before.AuthToken) await JF.generateAuthToken(api, token);
      const mutated = await JF.readPluginConfig(api, token, pluginId); // re-read: may carry the new token
      mutated.StreamDeliveryMode = 'DirectToTvheadend';
      await JF.writePluginConfig(api, token, pluginId, mutated);
      await JF.refreshProfileCache(api, token);
      await JF.waitForDiagnoseChannels(api, token);

      // Poll past the plugin's 10s stream-build reuse window until a direct URL shows up.
      const r = await JF.pollUntil(async () => {
        const attempt = await JF.requestPlaybackInfo(api, token, userId, nextChannel().id, matrixClients.directPlay);
        if (/\/stream\/channel\//.test(attempt.ms.Path || '')) return attempt;
        await JF.closeLiveStream(api, token, attempt.liveStreamId);
        return null;
      }, { timeoutMs: 45000, intervalMs: 3000, label: 'direct-to-TVHeadend stream URL' });

      try {
        const u = new URL(r.ms.Path);
        console.log(`[B|Direct] ${r.method} path=${u.origin}${u.pathname}?profile=${u.searchParams.get('profile')}&auth=…`);
        expect(u.pathname, 'TVHeadend stream endpoint').toMatch(/\/stream\/channel\/[0-9a-f]{32}$/);
        expect(u.port, 'points at the TVHeadend port').toBe('9981');
        expect(r.ms.Path).toContain('?profile=');
        const authToken = u.searchParams.get('auth');
        expect(authToken, 'auth token present').toBeTruthy();
        expect(authToken, 'auth token is alphanumeric (FFmpeg-URL-safe)').toMatch(/^[A-Za-z0-9]+$/);

        // Verify the stream actually plays from TVHeadend (host-rewritten to the published port).
        const res = await JF.fetchStreamBytes(JF.rewriteHost(r.ms.Path, TVH_URL), { minBytes: 32768, timeoutMs: 20000 });
        console.log(`[B|Direct] GET -> ${res.status} ${res.contentType}, ${res.bytes}B in ${res.elapsedMs}ms`);
        expect(res.status, 'TVHeadend serves the direct URL').toBe(200);
        expect(res.contentType).toMatch(/^video\//);
        expect(res.bytes).toBeGreaterThan(0);
      } finally {
        await JF.closeLiveStream(api, token, r.liveStreamId);
      }
    } finally {
      await JF.writePluginConfig(api, token, pluginId, restoreBody);
      await JF.refreshProfileCache(api, token);
      await JF.waitForDiagnoseChannels(api, token);
    }
  });
});

test.describe('C) Token security × streaming', () => {
  let api, token, userId, pluginId, tvhChannels;

  test.beforeAll(async () => {
    api = await request.newContext({ baseURL: JF.CONFIG.baseURL, ignoreHTTPSErrors: true });
    ({ token, userId } = await JF.authenticate(api));
    pluginId = await JF.getPluginId(api, token);
    await JF.ensureManagedProfile(api, token, pluginId);
    tvhChannels = await JF.getTvhChannels(api, token);
    expect(tvhChannels.length).toBeGreaterThan(0);
  });
  test.afterAll(async () => {
    const cfg = await JF.readPluginConfig(api, token, pluginId);
    expect(JF.canonicalConfigDeviations(cfg), 'config back to canonical').toEqual([]);
    await api.dispose();
  });

  test('EnableRelayTokenSecurity=false: open stream endpoint serves without a token', async () => {
    const uuid = tvhChannels[tvhChannels.length - 1].Id;
    const before = await JF.readPluginConfig(api, token, pluginId);
    const restoreBody = JSON.parse(JSON.stringify(before));
    try {
      const mutated = JSON.parse(JSON.stringify(before));
      mutated.EnableRelayTokenSecurity = false;
      await JF.writePluginConfig(api, token, pluginId, mutated);

      // Config reads are live per-request, but poll to a 200 with a deadline anyway.
      const res = await JF.pollUntil(async () => {
        const r = await JF.fetchStreamBytes(`${JF.CONFIG.baseURL}/api/tvheadend/stream/${uuid}?profile=pass`, { minBytes: 16384, timeoutMs: 15000 });
        return r.status === 200 ? r : null;
      }, { timeoutMs: 30000, intervalMs: 2000, label: 'tokenless stream with security off' });
      console.log(`[C|off] no token -> ${res.status} ${res.contentType}, ${res.bytes}B`);
      expect(res.contentType).toMatch(/^video\//);
      expect(res.bytes).toBeGreaterThan(0);
    } finally {
      await JF.writePluginConfig(api, token, pluginId, restoreBody);
    }
  });

  test('EnableRelayTokenSecurity=true: 401 without token, 200 with the PlaybackInfo token', async () => {
    const cfg = await JF.readPluginConfig(api, token, pluginId);
    expect(cfg.EnableRelayTokenSecurity, 'canonical security is on').toBeTruthy();

    const uuid = tvhChannels[tvhChannels.length - 1].Id;
    // Poll: the previous test just restored security=true.
    const denied = await JF.pollUntil(async () => {
      const r = await JF.fetchStreamBytes(`${JF.CONFIG.baseURL}/api/tvheadend/stream/${uuid}?profile=pass`, { minBytes: 1024, timeoutMs: 8000 });
      return r.status === 401 ? r : null;
    }, { timeoutMs: 30000, intervalMs: 2000, label: '401 for tokenless stream with security on' });
    console.log(`[C|on] no token -> ${denied.status}`);

    // A real playback token (embedded in the PlaybackInfo relay URL) must open the same endpoint.
    const channels = await JF.getChannels(api, token, userId, 50);
    const { ms, liveStreamId } = await JF.requestPlaybackInfo(api, token, userId, channels[channels.length - 1].id, matrixClients.directPlay);
    try {
      const u = new URL(ms.Path);
      const relayToken = u.searchParams.get('token');
      const chanUuid = u.pathname.split('/').pop();
      expect(relayToken, 'PlaybackInfo issued a relay token').toBeTruthy();
      const ok = await JF.fetchStreamBytes(`${JF.CONFIG.baseURL}/api/tvheadend/stream/${chanUuid}?token=${relayToken}`, { minBytes: 32768, timeoutMs: 20000 });
      console.log(`[C|on] valid token -> ${ok.status} ${ok.contentType}, ${ok.bytes}B`);
      expect(ok.status).toBe(200);
      expect(ok.contentType).toMatch(/^video\//);
      expect(ok.bytes).toBeGreaterThan(0);
    } finally {
      await JF.closeLiveStream(api, token, liveStreamId);
    }
  });
});

test.describe('D) Real-browser spot-checks (managed profile) + zapping', () => {
  let api, token, userId, serverId, pluginId, channels;

  test.beforeAll(async () => {
    api = await request.newContext({ baseURL: JF.CONFIG.baseURL, ignoreHTTPSErrors: true });
    ({ token, userId } = await JF.authenticate(api));
    serverId = await JF.getServerId(api);
    pluginId = await JF.getPluginId(api, token);
    await JF.ensureManagedProfile(api, token, pluginId);
    await JF.pickChannel(api, token, userId);
    channels = await JF.getChannels(api, token, userId, 50);
    expect(channels.length, 'zapping needs at least two channels').toBeGreaterThan(1);
  });
  test.afterAll(async () => {
    const cfg = await JF.readPluginConfig(api, token, pluginId);
    expect(JF.canonicalConfigDeviations(cfg), 'config back to canonical').toEqual([]);
    await api.dispose();
  });

  test('browser plays a channel, then zaps to a second channel', async ({ page }) => {
    test.setTimeout(360000);
    const cfg = await JF.readPluginConfig(api, token, pluginId);
    expect(cfg.StreamingProfileSettings.DefaultTvHeadendProfile, 'managed profile active').toBe('jellyfin');

    // The previous browser test leaves a server-side FFmpeg session that keeps holding a
    // TVHeadend tuner slot for minutes after the page closes. The zap below briefly needs
    // TWO slots (old + new channel overlap), so actively stop leftover playback sessions
    // and wait until TVHeadend's subscriptions are gone.
    await JF.stopAllPlaybackSessions(api, token);
    await JF.pollUntil(async () => {
      const subs = await JF.tvhApi('/api/status/subscriptions');
      const active = (subs.entries || []).filter((e) => (e.title || '') !== 'epggrab');
      if (active.length === 0) return true;
      await JF.stopAllPlaybackSessions(api, token);
      return null;
    }, { timeoutMs: 90000, intervalMs: 3000, label: 'TVHeadend subscriptions drained before zap test' });

    const [ch1, ch2] = [channels[0], channels[1]];
    const fatal = JF.captureMediaErrors(page);
    const playbackInfoItems = [];
    page.on('response', (r) => {
      const m = r.url().match(/\/Items\/([^/?]+)\/PlaybackInfo/i);
      if (m && r.request().method() === 'POST') {
        playbackInfoItems.push(m[1].replace(/-/g, '').toLowerCase());
      }
    });

    await JF.uiLogin(page);

    // First channel: sustained playback (the 04 pattern).
    expect(await JF.playChannel(page, ch1.id, serverId), 'play triggered for channel 1').toBeTruthy();
    const start1 = await JF.waitForPlaybackStart(page, 90000);
    expect(start1.v, 'channel 1 video element').toBeTruthy();
    expect(start1.ok, `channel 1 started (${JSON.stringify(start1.v)})`).toBeTruthy();
    await page.waitForTimeout(8000); // sustain to prove stable decode
    const v1 = await JF.readVideo(page);
    console.log(`[D] ${ch1.name}: ${JSON.stringify(v1)}`);
    expect(v1.errorCode, `channel 1 media error ${v1 && v1.errorMsg}`).toBeNull();
    expect(v1.currentTime, 'channel 1 position advanced').toBeGreaterThan(2);
    expect(fatal, `fatal media errors on channel 1:\n${fatal.join('\n')}`).toHaveLength(0);

    // Channel ZAP: switch to a different channel in the same browser session. A tuner or
    // subscription leak on channel 1 would starve this second tune — regression guard.
    expect(await JF.playChannel(page, ch2.id, serverId), 'play triggered for channel 2').toBeTruthy();
    const ch2Seen = await JF.pollUntil(
      async () => playbackInfoItems.includes(ch2.id.replace(/-/g, '').toLowerCase()),
      { timeoutMs: 60000, intervalMs: 2000, label: `PlaybackInfo for ${ch2.name}` },
    );
    expect(ch2Seen, 'channel 2 PlaybackInfo requested').toBeTruthy();
    const adv = await JF.waitForPlaybackAdvance(page, 2, 90000);
    console.log(`[D] zap ${ch1.name} -> ${ch2.name}: ${JSON.stringify(adv.v)}`);
    expect(adv.v, 'channel 2 video element').toBeTruthy();
    expect(adv.v.errorCode, `channel 2 media error ${adv.v && adv.v.errorMsg}`).toBeNull();
    expect(adv.ok, 'channel 2 playback advanced > 2s after the zap').toBeTruthy();
    expect(fatal, `fatal media errors after zapping:\n${fatal.join('\n')}`).toHaveLength(0);
  });
});
