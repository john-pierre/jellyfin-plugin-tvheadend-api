// Shared helpers for the TVHeadend plugin Playwright suite: REST auth, plugin API calls,
// crafted device profiles for each play method, and browser login / live-TV playback helpers.
const { expect } = require('@playwright/test');

const CONFIG = {
  baseURL: process.env.JELLYFIN_URL || 'http://localhost:8096',
  user: process.env.JF_USER || 'admin',
  pass: process.env.JF_PASS || 'admin123',
  // Channel to use for playback tests. Empty = first channel returned by the server.
  channelName: process.env.JF_CHANNEL || '',
  // Soak duration in seconds (default 5 minutes, as requested).
  soakSeconds: parseInt(process.env.SOAK_SECONDS || '300', 10),
};

const CLIENT = 'MediaBrowser Client="tvh-e2e", Device="playwright", DeviceId="tvh-e2e-playwright", Version="1.0.0"';
const authHeaders = (token) => ({
  'X-Emby-Authorization': token ? `${CLIENT}, Token="${token}"` : CLIENT,
  'Content-Type': 'application/json',
});

async function authenticate(request) {
  const res = await request.post(`${CONFIG.baseURL}/Users/AuthenticateByName`, {
    headers: authHeaders(null),
    data: { Username: CONFIG.user, Pw: CONFIG.pass },
  });
  expect(res.ok(), `auth status ${res.status()}`).toBeTruthy();
  const j = await res.json();
  return { token: j.AccessToken, userId: j.User.Id };
}

async function getServerId(request) {
  const res = await request.get(`${CONFIG.baseURL}/System/Info/Public`);
  return (await res.json()).Id;
}

async function getPluginId(request, token) {
  const res = await request.get(`${CONFIG.baseURL}/Plugins`, { headers: authHeaders(token) });
  const plugins = await res.json();
  const p = plugins.find((x) => /tvheadend/i.test(x.Name || ''));
  return p ? p.Id : null;
}

async function getChannels(request, token, userId, limit = 5) {
  const res = await request.get(`${CONFIG.baseURL}/LiveTv/Channels?userId=${userId}&limit=${limit}`, { headers: authHeaders(token) });
  const j = await res.json();
  return (j.Items || []).map((i) => ({ id: i.Id, name: i.Name }));
}

async function pickChannel(request, token, userId) {
  const channels = await getChannels(request, token, userId, 50);
  expect(channels.length, 'at least one Live TV channel').toBeGreaterThan(0);
  if (CONFIG.channelName) {
    const m = channels.find((c) => c.name === CONFIG.channelName);
    if (m) return m;
  }
  return channels[0];
}

// ---- Device profiles to exercise each play method against a mpegts/h264/aac source ----
const HLS_TRANSCODE = { Container: 'ts', Type: 'Video', VideoCodec: 'h264', AudioCodec: 'aac', Protocol: 'hls', Context: 'Streaming' };

const deviceProfiles = {
  // Source container + codecs are directly supported -> Direct Play.
  directPlay: {
    MaxStreamingBitrate: 120000000,
    MaxStaticBitrate: 120000000,
    DirectPlayProfiles: [{ Container: 'ts,mpegts', Type: 'Video', VideoCodec: 'h264', AudioCodec: 'aac,mp2,ac3,eac3,mp3' }],
    TranscodingProfiles: [HLS_TRANSCODE],
    CodecProfiles: [], ContainerProfiles: [], SubtitleProfiles: [],
  },
  // Codecs supported, but the container is not directly playable -> Direct Stream (remux).
  directStream: {
    MaxStreamingBitrate: 120000000,
    MaxStaticBitrate: 120000000,
    DirectPlayProfiles: [{ Container: 'mp4,fmp4', Type: 'Video', VideoCodec: 'h264', AudioCodec: 'aac' }],
    TranscodingProfiles: [HLS_TRANSCODE],
    CodecProfiles: [], ContainerProfiles: [], SubtitleProfiles: [],
  },
  // Browser-like: only fmp4/HLS, low bitrate cap -> full Transcode.
  transcode: {
    MaxStreamingBitrate: 3000000,
    MaxStaticBitrate: 3000000,
    DirectPlayProfiles: [{ Container: 'mp4', Type: 'Video', VideoCodec: 'h264', AudioCodec: 'aac' }],
    TranscodingProfiles: [HLS_TRANSCODE],
    CodecProfiles: [], ContainerProfiles: [], SubtitleProfiles: [],
  },
};

async function requestPlaybackInfo(request, token, userId, itemId, deviceProfile, flags = {}) {
  const q = new URLSearchParams({
    userId,
    autoOpenLiveStream: 'true',
    maxStreamingBitrate: String(deviceProfile.MaxStreamingBitrate || 120000000),
    startTimeTicks: '0',
    enableDirectPlay: String(flags.directPlay ?? true),
    enableDirectStream: String(flags.directStream ?? true),
    enableTranscoding: String(flags.transcoding ?? true),
  });
  const res = await request.post(`${CONFIG.baseURL}/Items/${itemId}/PlaybackInfo?${q}`, {
    headers: authHeaders(token),
    data: { DeviceProfile: deviceProfile },
  });
  expect(res.ok(), `PlaybackInfo status ${res.status()}`).toBeTruthy();
  const j = await res.json();
  const ms = (j.MediaSources || [])[0] || {};
  let method = 'Transcode';
  if (ms.SupportsDirectPlay) method = 'DirectPlay';
  else if (ms.SupportsDirectStream) method = 'DirectStream';
  return { ms, method, playSessionId: j.PlaySessionId, liveStreamId: ms.LiveStreamId };
}

// ---- Browser helpers ----
async function uiLogin(page) {
  await page.goto('/web/', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(4000);
  if (await page.locator('#txtManualName').count() === 0) {
    const ml = page.locator('text=/manual login|manuelle anmeldung/i').first();
    if (await ml.count()) { await ml.click().catch(() => {}); await page.waitForTimeout(1200); }
  }
  if (await page.locator('#txtManualName').count()) {
    await page.fill('#txtManualName', CONFIG.user);
    await page.fill('#txtManualPassword', CONFIG.pass);
  } else {
    await page.fill('input[type=text]', CONFIG.user).catch(() => {});
    await page.fill('input[type=password]', CONFIG.pass).catch(() => {});
  }
  await page.keyboard.press('Enter');
  await page.waitForTimeout(6000);
  // We should now be on the home page (not still on login).
  expect(page.url()).not.toMatch(/login\.html/);
}

async function openPluginPage(page, name) {
  await page.goto(`/web/#/configurationpage?name=${name}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(6000);
}

async function playChannel(page, channelId, serverId) {
  await page.goto(`/web/#/details?id=${channelId}&serverId=${serverId}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(5000);
  const selectors = ['.btnPlay', '.mainDetailButtons .btnPlay', 'button[title="Play"]', 'button[title*="bspielen"]', 'button[title*="ieder"]', '.detailFloatingButton', '[data-action="play"]', '[data-action="resume"]'];
  for (const sel of selectors) {
    const loc = page.locator(sel).first();
    if (await loc.count()) { try { await loc.click({ timeout: 3000 }); return true; } catch (e) { /* try next */ } }
  }
  const txtBtn = page.locator('button:has-text("Wiedergabe"), button:has-text("Play"), button:has-text("Abspielen")').first();
  if (await txtBtn.count()) { try { await txtBtn.click(); return true; } catch (e) { /* ignore */ } }
  return false;
}

async function readVideo(page) {
  return page.evaluate(() => {
    // Jellyfin web can have several <video> elements (backdrop/trickplay + the real player).
    // Pick the one that has progressed the most — i.e. the actually-playing element.
    const vids = Array.from(document.querySelectorAll('video'));
    if (!vids.length) return null;
    const v = vids.slice().sort((a, b) => (b.currentTime || 0) - (a.currentTime || 0))[0];
    return {
      count: vids.length,
      readyState: v.readyState,
      paused: v.paused,
      currentTime: Math.round(v.currentTime * 100) / 100,
      errorCode: v.error ? v.error.code : null,
      errorMsg: v.error ? v.error.message : null,
      networkState: v.networkState,
    };
  });
}

// Waits until playback actually starts (currentTime advances) or the timeout elapses.
async function waitForPlaybackStart(page, timeoutMs = 40000) {
  const deadline = Date.now() + timeoutMs;
  let last = null;
  while (Date.now() < deadline) {
    const v = await readVideo(page);
    last = v;
    if (v && v.errorCode) return { ok: false, v };
    if (v && v.currentTime > 0.5) return { ok: true, v };
    await page.waitForTimeout(3000);
  }
  return { ok: false, v: last };
}

// Attaches console/error capture; returns an array that collects fatal media errors.
function captureMediaErrors(page) {
  const fatal = [];
  page.on('console', (m) => {
    const t = m.text();
    if (/PIPELINE_ERROR_DECODE|Failed to send (audio|video) packet|bufferAppendError.*Fatal: true|FATAL_HLS_ERROR|cannot recover/i.test(t)) {
      fatal.push(t.slice(0, 200));
    }
  });
  page.on('pageerror', (e) => { if (/decode|media/i.test(e.message || '')) fatal.push('pageerror: ' + e.message.slice(0, 150)); });
  return fatal;
}

module.exports = {
  CONFIG, authHeaders, authenticate, getServerId, getPluginId, getChannels, pickChannel,
  deviceProfiles, requestPlaybackInfo, uiLogin, openPluginPage, playChannel, readVideo, captureMediaErrors,
};
