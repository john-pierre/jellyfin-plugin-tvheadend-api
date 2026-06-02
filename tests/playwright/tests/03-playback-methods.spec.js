// Exercises the three play methods (Direct Play / Direct Stream / Transcode) at the PlaybackInfo
// decision layer using crafted device profiles, and verifies a playable artifact is produced.
const { test, expect, request } = require('@playwright/test');
const JF = require('../fixtures/jellyfin');

test.describe('Play methods (Direct Play / Direct Stream / Transcode)', () => {
  let api, token, userId, channel;

  test.beforeAll(async () => {
    api = await request.newContext({ baseURL: JF.CONFIG.baseURL, ignoreHTTPSErrors: true });
    ({ token, userId } = await JF.authenticate(api));
    channel = await JF.pickChannel(api, token, userId);
  });
  test.afterAll(async () => { await api.dispose(); });

  test('Direct-Play-capable client gets Direct Play with h264/aac', async () => {
    const { ms, method } = await JF.requestPlaybackInfo(api, token, userId, channel.id, JF.deviceProfiles.directPlay);
    const codecs = (ms.MediaStreams || []).map((s) => `${s.Type}:${s.Codec}`).join(',');
    console.log(`[${channel.name}] directPlay profile -> ${method} (${codecs})`);
    expect(ms.SupportsDirectPlay, 'Direct Play supported').toBeTruthy();
    expect(codecs).toMatch(/Video:h264/i);
    expect(codecs).toMatch(/Audio:aac/i);
  });

  test('codec-compatible / container-mismatch client gets a playable stream (Direct Stream or remux)', async () => {
    const { ms, method } = await JF.requestPlaybackInfo(api, token, userId, channel.id, JF.deviceProfiles.directStream);
    console.log(`[${channel.name}] directStream profile -> ${method}`);
    const playable = ms.SupportsDirectStream || ms.SupportsDirectPlay || !!ms.TranscodingUrl;
    expect(playable, 'a playable artifact is produced').toBeTruthy();
  });

  test('browser-like client gets a working HLS transcode', async () => {
    const { ms, method } = await JF.requestPlaybackInfo(api, token, userId, channel.id, JF.deviceProfiles.transcode);
    console.log(`[${channel.name}] transcode profile -> ${method}, url=${(ms.TranscodingUrl || '').slice(0, 80)}`);
    expect(ms.TranscodingUrl, 'transcoding url present').toBeTruthy();

    // The master playlist must actually load (this starts a real transcode on the backend).
    const res = await api.get(ms.TranscodingUrl, { headers: JF.authHeaders(token) });
    expect(res.ok(), `master.m3u8 status ${res.status()}`).toBeTruthy();
    const text = await res.text();
    expect(text, 'valid HLS playlist').toContain('#EXTM3U');

    // Clean up: stop the transcode so it does not hold a tuner.
    try {
      const u = new URL(ms.TranscodingUrl, JF.CONFIG.baseURL);
      const deviceId = u.searchParams.get('DeviceId');
      const psid = u.searchParams.get('PlaySessionId');
      if (deviceId) {
        await api.delete(`/Videos/ActiveEncodings?deviceId=${encodeURIComponent(deviceId)}${psid ? `&playSessionId=${psid}` : ''}`, { headers: JF.authHeaders(token) }).catch(() => {});
      }
    } catch (e) { /* best effort */ }
  });
});
