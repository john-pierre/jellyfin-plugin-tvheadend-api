// @soak — sustained playback for SOAK_SECONDS (default 5 minutes) to catch dropouts / stalls /
// late decode errors that a short test would miss. Also cross-checks the relay metrics mid-soak.
// Run with:  npm run test:soak   (or SOAK_SECONDS=300 npx playwright test --grep @soak --timeout=0)
const { test, expect, request } = require('@playwright/test');
const JF = require('../fixtures/jellyfin');

test.describe('@soak sustained playback', () => {
  let api, token, userId, serverId, channel;

  test.beforeAll(async () => {
    api = await request.newContext({ baseURL: JF.CONFIG.baseURL, ignoreHTTPSErrors: true });
    ({ token, userId } = await JF.authenticate(api));
    serverId = await JF.getServerId(api);
    channel = await JF.pickChannel(api, token, userId);
  });
  test.afterAll(async () => { await api.dispose(); });

  test('plays continuously without dropouts', async ({ page }) => {
    test.setTimeout(0); // no timeout — duration is controlled by SOAK_SECONDS
    const total = JF.CONFIG.soakSeconds;
    const fatal = JF.captureMediaErrors(page);

    await JF.uiLogin(page);
    const played = await JF.playChannel(page, channel.id, serverId);
    expect(played, 'play button was triggered').toBeTruthy();
    await page.waitForTimeout(8000); // let playback establish

    const start = Date.now();
    let samples = 0, stalls = 0, lastT = -1, maxT = 0, sawActiveStream = false;
    const stallLog = [];

    while ((Date.now() - start) / 1000 < total) {
      await page.waitForTimeout(5000);
      samples++;
      const v = await JF.readVideo(page);
      if (!v) { stalls++; stallLog.push(`${elapsed(start)}s: no video element`); continue; }
      if (v.errorCode) throw new Error(`media error at ${elapsed(start)}s: ${v.errorCode} ${v.errorMsg}`);
      if (v.currentTime <= lastT + 0.2) { stalls++; stallLog.push(`${elapsed(start)}s: position stalled at ${v.currentTime}`); }
      lastT = v.currentTime;
      maxT = Math.max(maxT, v.currentTime);

      // Mid-soak metric cross-check: the relay should report at least one active stream.
      if (samples % 6 === 0) {
        const m = await api.get('/TvHeadendApi/RelayMetrics?hours=1', { headers: JF.authHeaders(token) }).then((r) => r.json()).catch(() => ({}));
        if ((m.ActiveStreams || 0) >= 1) sawActiveStream = true;
        console.log(`[soak ${elapsed(start)}s] pos=${v.currentTime} activeStreams=${m.ActiveStreams ?? '?'} stalls=${stalls}/${samples}`);
      }
    }

    console.log(`[soak done] maxPos=${maxT}s stalls=${stalls}/${samples} fatal=${fatal.length} sawActiveStream=${sawActiveStream}`);
    if (stallLog.length) console.log('stalls:\n' + stallLog.join('\n'));

    expect(fatal, `fatal media errors:\n${fatal.join('\n')}`).toHaveLength(0);
    expect(maxT, 'playback advanced near the full duration').toBeGreaterThan(total * 0.7);
    expect(stalls, `too many stalls: ${stalls}/${samples}`).toBeLessThan(samples * 0.34);
    expect(sawActiveStream, 'relay reported the active stream').toBeTruthy();
  });
});

function elapsed(start) { return Math.round((Date.now() - start) / 1000); }
