// The key regression test: a live channel must actually play in a real Chromium browser.
// This catches the AAC-Main bug (Chrome MSE cannot decode AAC Main -> PIPELINE_ERROR_DECODE).
const { test, expect, request } = require('@playwright/test');
const JF = require('../fixtures/jellyfin');

test.describe('Real-browser live playback', () => {
  let api, token, userId, serverId, channel;

  test.beforeAll(async () => {
    api = await request.newContext({ baseURL: JF.CONFIG.baseURL, ignoreHTTPSErrors: true });
    ({ token, userId } = await JF.authenticate(api));
    serverId = await JF.getServerId(api);
    channel = await JF.pickChannel(api, token, userId);
  });
  test.afterAll(async () => { await api.dispose(); });

  test('channel plays without fatal media/decode error', async ({ page }) => {
    const fatal = JF.captureMediaErrors(page);
    const decisions = [];
    page.on('response', async (r) => {
      if (/PlaybackInfo/i.test(r.url()) && r.request().method() === 'POST') {
        try { const j = await r.json(); const ms = (j.MediaSources || [])[0] || {}; decisions.push(ms.TranscodingUrl ? 'Transcode' : (ms.SupportsDirectPlay ? 'DirectPlay' : 'DirectStream')); } catch (e) { /* ignore */ }
      }
    });

    await JF.uiLogin(page);
    const played = await JF.playChannel(page, channel.id, serverId);
    expect(played, 'play button was triggered').toBeTruthy();

    // Give the player time to start and run for a bit.
    await page.waitForTimeout(25000);

    const v = await JF.readVideo(page);
    console.log(`[${channel.name}] decision(s): ${decisions.join(',') || 'n/a'} | video: ${JSON.stringify(v)}`);

    expect(v, 'a <video> element exists').toBeTruthy();
    expect(v.errorCode, `media error ${v && v.errorCode}: ${v && v.errorMsg}`).toBeNull();
    expect(fatal, `fatal media errors:\n${fatal.join('\n')}`).toHaveLength(0);
    expect(v.currentTime, 'playback position advanced').toBeGreaterThan(2);
    expect(v.readyState, 'has current data').toBeGreaterThanOrEqual(2);
  });
});
