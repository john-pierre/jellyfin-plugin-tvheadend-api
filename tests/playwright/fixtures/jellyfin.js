// Shared helpers for the TVHeadend plugin Playwright suite: REST auth, plugin API calls,
// crafted device profiles for each play method, and browser login / live-TV playback helpers.
const crypto = require('crypto');
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

// Direct TVHeadend backend access (DVR cross-checks and cleanup that Jellyfin's
// ILiveTvService surface cannot express, e.g. removing completed DVR entries).
const TVH = {
  baseURL: process.env.TVHEADEND_URL || 'http://localhost:19981',
  user: process.env.TVH_USER || 'testuser',
  pass: process.env.TVH_PASS || 'testpass',
};

// The DeviceId is unique PER PROCESS: Jellyfin revokes the previous access token when the
// same user re-authenticates with the same DeviceId ("Logging out access token …"), so a
// fixed id would let any concurrently running suite/tool invalidate this run's session
// mid-test (observed live as sudden 401s during a 50s recording wait).
const DEVICE_ID = `tvh-e2e-playwright-${process.pid}-${Math.random().toString(36).slice(2, 8)}`;
const CLIENT = `MediaBrowser Client="tvh-e2e", Device="playwright", DeviceId="${DEVICE_ID}", Version="1.0.0"`;
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

// Jellyfin's Live TV channel cache can lag behind the plugin (e.g. right after the stack
// booted or the plugin recovered from a misconfiguration). Trigger the RefreshGuide
// scheduled task and poll until channel items appear.
async function ensureLiveTvChannels(request, token, userId) {
  let channels = await getChannels(request, token, userId, 1);
  if (channels.length > 0) return true;

  const tasksRes = await request.get(`${CONFIG.baseURL}/ScheduledTasks`, { headers: authHeaders(token) });
  if (tasksRes.ok()) {
    const tasks = await tasksRes.json();
    const guide = (tasks || []).find((t) => t.Key === 'RefreshGuide');
    if (guide) {
      await request.post(`${CONFIG.baseURL}/ScheduledTasks/Running/${guide.Id}`, { headers: authHeaders(token) });
    }
  }

  for (let attempt = 0; attempt < 30; attempt++) {
    await new Promise((r) => setTimeout(r, 3000));
    channels = await getChannels(request, token, userId, 1);
    if (channels.length > 0) return true;
  }
  return false;
}

// The managed "jellyfin" TVHeadend profile is created by the plugin on demand — a fresh
// e2e stack has not provisioned it yet. Create it (capability-driven, sets it as the
// effective default) when it is not the default already. Fail-closed: every caller relies
// on the managed profile actually existing afterwards, so a failed provisioning throws
// instead of returning a (historically ignored) false.
async function ensureManagedProfile(request, token, pluginId) {
  const cfgRes = await request.get(`${CONFIG.baseURL}/Plugins/${pluginId}/Configuration`, { headers: authHeaders(token) });
  const cfg = await cfgRes.json();
  if (((cfg.StreamingProfileSettings || {}).DefaultTvHeadendProfile || '') === 'jellyfin') return true;

  const createRes = await request.post(`${CONFIG.baseURL}/TvHeadendApi/CreateProfile`, {
    headers: authHeaders(token),
    timeout: 120000,
  });
  if (!createRes.ok()) {
    throw new Error(`ensureManagedProfile: CreateProfile returned HTTP ${createRes.status()}`);
  }
  const result = await createRes.json().catch(() => ({}));
  if (result && result.Success === false) {
    throw new Error(`ensureManagedProfile: CreateProfile failed: ${result.Message || 'no message'}`);
  }
  return true;
}

async function pickChannel(request, token, userId) {
  expect(
    await ensureLiveTvChannels(request, token, userId),
    'Live TV channels available (guide refresh did not surface any)',
  ).toBeTruthy();

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
    // flags.headers lets callers impersonate a specific client (see authenticateAs).
    headers: flags.headers || authHeaders(token),
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

// ---- Plugin configuration helpers (read-modify-write with exact restore) ----

async function readPluginConfig(request, token, pluginId) {
  const res = await request.get(`${CONFIG.baseURL}/Plugins/${pluginId}/Configuration`, { headers: authHeaders(token) });
  expect(res.ok(), `config GET status ${res.status()}`).toBeTruthy();
  return res.json();
}

async function writePluginConfig(request, token, pluginId, config) {
  const res = await request.post(`${CONFIG.baseURL}/Plugins/${pluginId}/Configuration`, {
    headers: authHeaders(token),
    data: config,
  });
  expect(res.ok(), `config POST status ${res.status()}`).toBeTruthy();
}

async function refreshProfileCache(request, token) {
  const res = await request.post(`${CONFIG.baseURL}/TvHeadendApi/StreamingProfiles/RefreshCache`, { headers: authHeaders(token) });
  expect(res.ok(), `RefreshCache status ${res.status()}`).toBeTruthy();
}

// The canonical plugin configuration of the e2e stack. Every spec that mutates the config
// must leave it in this state; specs assert it in afterAll via canonicalConfigDeviations.
const CANONICAL_CONFIG = {
  Host: 'tvheadend',
  Port: 9981,
  Username: 'testuser',
  Password: 'testpass',
  RelayEnabled: true,
  EnableRelayTokenSecurity: true,
  StreamDeliveryMode: 'Relay',
  // Jellyfin's FFmpeg uses ms.Path verbatim — a leaked override breaks every playback spec.
  RelayHostOverride: '',
};

function canonicalConfigDeviations(cfg) {
  const dev = [];
  for (const [key, want] of Object.entries(CANONICAL_CONFIG)) {
    if (cfg[key] !== want) dev.push(`${key}=${JSON.stringify(cfg[key])} (expected ${JSON.stringify(want)})`);
  }
  const sps = cfg.StreamingProfileSettings || {};
  if (sps.DefaultTvHeadendProfile !== 'jellyfin') {
    dev.push(`StreamingProfileSettings.DefaultTvHeadendProfile=${JSON.stringify(sps.DefaultTvHeadendProfile)} (expected "jellyfin")`);
  }
  if ((sps.ClientRules || []).length !== 0) dev.push(`ClientRules not empty (${sps.ClientRules.length} left behind)`);
  if ((sps.UserRules || []).length !== 0) dev.push(`UserRules not empty (${sps.UserRules.length} left behind)`);
  return dev;
}

// ---- Generic polling (all waits must poll a condition with a deadline) ----

async function pollUntil(fn, { timeoutMs = 30000, intervalMs = 2000, label = 'condition' } = {}) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const value = await fn();
    if (value) return value;
    if (Date.now() >= deadline) throw new Error(`Timed out after ${timeoutMs}ms waiting for: ${label}`);
    await new Promise((r) => setTimeout(r, intervalMs));
  }
}

// Diagnose statuses the suite treats as "healthy". The plugin intentionally reports WARNING
// for this suite's CANONICAL configuration: RelayHostOverride='' (a suite invariant — a
// leaked override breaks every playback spec) trips the advisory "Jellyfin Host Override"
// check added 2026-07-17. Only ERROR is a failure state for the e2e stack.
const HEALTHY_DIAGNOSE_STATUSES = ['OK', 'WARNING'];

// Polls GET /TvHeadendApi/Diagnose until the plugin reports channels again — used after
// config mutations to prove the plugin still talks to TVHeadend before moving on.
async function waitForDiagnoseChannels(request, token, timeoutMs = 60000) {
  return pollUntil(async () => {
    const res = await request.get(`${CONFIG.baseURL}/TvHeadendApi/Diagnose`, { headers: authHeaders(token) });
    if (!res.ok()) return null;
    const d = await res.json();
    return (d.ChannelCount || 0) > 0 ? d : null;
  }, { timeoutMs, intervalMs: 3000, label: 'Diagnose ChannelCount > 0' });
}

// ---- Raw stream verification (native fetch — Playwright's request context buffers whole
// bodies, which never terminates on an infinite live TS stream) ----

// Fetches url and reads the body until minBytes arrived or timeoutMs elapsed, then aborts the
// request so the TVHeadend subscription drains. Returns status/content-type/bytes/timing.
async function fetchStreamBytes(url, { method = 'GET', minBytes = 65536, timeoutMs = 20000, headers } = {}) {
  const controller = new AbortController();
  const started = Date.now();
  const safetyTimer = setTimeout(() => controller.abort(), timeoutMs);
  let response;
  try {
    response = await fetch(url, { method, headers, signal: controller.signal });
  } catch (e) {
    clearTimeout(safetyTimer);
    return { status: -1, contentType: '', bytes: 0, elapsedMs: Date.now() - started, error: String(e && e.message) };
  }
  let bytes = 0;
  let firstByteMs = null;
  if (method !== 'HEAD' && response.body) {
    const reader = response.body.getReader();
    try {
      while (bytes < minBytes) {
        const { done, value } = await reader.read();
        if (done) break;
        if (value && value.length) {
          if (firstByteMs === null) firstByteMs = Date.now() - started;
          bytes += value.length;
        }
      }
    } catch (e) {
      if (e.name !== 'AbortError') throw e; // AbortError = our own deadline fired mid-read
    } finally {
      try { await reader.cancel(); } catch (e) { /* upstream already gone */ }
      controller.abort();
    }
  } else {
    controller.abort(); // releases the (empty) HEAD body
  }
  clearTimeout(safetyTimer);
  return {
    status: response.status,
    contentType: response.headers.get('content-type') || '',
    acceptRanges: response.headers.get('accept-ranges') || '',
    bytes,
    firstByteMs,
    elapsedMs: Date.now() - started,
  };
}

// Rewrites the host/port of an absolute URL (MediaSource paths carry docker-internal hosts).
function rewriteHost(urlString, baseUrl) {
  const u = new URL(urlString);
  const b = new URL(baseUrl);
  u.protocol = b.protocol;
  u.host = b.host;
  return u.toString();
}

// ---- Playback artifact cleanup (tuners are scarce — always drain what you open) ----

async function closeLiveStream(request, token, liveStreamId) {
  if (!liveStreamId) return;
  await request.post(`${CONFIG.baseURL}/LiveStreams/Close`, {
    headers: authHeaders(token),
    data: { LiveStreamId: liveStreamId },
  }).catch(() => {});
}

async function stopActiveEncoding(request, token, transcodingUrl) {
  if (!transcodingUrl) return;
  try {
    const u = new URL(transcodingUrl, CONFIG.baseURL);
    const deviceId = u.searchParams.get('DeviceId');
    const psid = u.searchParams.get('PlaySessionId');
    if (deviceId) {
      await request.delete(
        `${CONFIG.baseURL}/Videos/ActiveEncodings?deviceId=${encodeURIComponent(deviceId)}${psid ? `&playSessionId=${encodeURIComponent(psid)}` : ''}`,
        { headers: authHeaders(token) },
      );
    }
  } catch (e) { /* best effort */ }
}

// Follows TranscodingUrl -> master playlist -> variant playlist -> media segments.
// Fetching the playlists starts a real FFmpeg transcode; the caller must stopActiveEncoding
// (and closeLiveStream) afterwards. The FIRST segment can legitimately be tiny (FFmpeg
// force-splits at the first keyframe after joining a live stream), so newly listed segments
// are consumed until one exceeds minSegmentBytes or the deadline passes. Returns the
// playlists plus the largest fetched segment's size.
async function fetchHlsArtifact(request, token, transcodingUrl, headers, { minSegmentBytes = 10240, timeoutMs = 45000, maxSegments = 8 } = {}) {
  const h = headers || authHeaders(token);
  const masterUrl = new URL(transcodingUrl, CONFIG.baseURL);
  const masterRes = await request.get(masterUrl.toString(), { headers: h, timeout: 60000 });
  const out = { masterStatus: masterRes.status(), master: '', variant: '', segmentBytes: 0, segmentIsMpegTs: false, segmentsFetched: 0 };
  if (!masterRes.ok()) return out;
  out.master = await masterRes.text();
  const variantLine = out.master.split('\n').find((l) => l.trim() && !l.startsWith('#'));
  if (!variantLine) return out;
  const variantUrl = new URL(variantLine.trim(), masterUrl);

  const fetched = new Set();
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    // The live variant playlist blocks until FFmpeg wrote the first segment, then grows.
    const variantRes = await request.get(variantUrl.toString(), { headers: h, timeout: 90000 });
    if (!variantRes.ok()) return out;
    out.variant = await variantRes.text();
    const segLines = out.variant.split('\n').filter((l) => l.trim() && !l.startsWith('#'));
    for (const segLine of segLines) {
      if (out.segmentBytes >= minSegmentBytes || out.segmentsFetched >= maxSegments || fetched.has(segLine)) continue;
      fetched.add(segLine);
      const segRes = await request.get(new URL(segLine.trim(), variantUrl).toString(), { headers: h, timeout: 90000 });
      if (segRes.ok()) {
        const buf = await segRes.body();
        out.segmentsFetched++;
        if (buf.length > out.segmentBytes) {
          out.segmentBytes = buf.length;
          out.segmentIsMpegTs = buf.length > 0 && buf[0] === 0x47; // TS sync byte
        }
      }
    }
    if (out.segmentBytes >= minSegmentBytes || out.segmentsFetched >= maxSegments || Date.now() >= deadline) return out;
    await new Promise((r) => setTimeout(r, 2000));
  }
}

// ---- Per-client identity (client-matrix tests impersonate real Jellyfin apps) ----

function clientAuthHeaders(clientName, deviceId, token, deviceName) {
  const c = `MediaBrowser Client="${clientName}", Device="${deviceName || `e2e-${deviceId}`}", DeviceId="${deviceId}", Version="1.0.0"`;
  return { 'X-Emby-Authorization': token ? `${c}, Token="${token}"` : c, 'Content-Type': 'application/json' };
}

// Authenticates a fresh session under the given client name (Jellyfin binds sessions to the
// Client/DeviceId of the auth request — client rules resolve against this name). The optional
// deviceName drives DeviceName-based streaming-profile rules (e.g. "Fire TV Stick").
async function authenticateAs(request, clientName, deviceId, deviceName) {
  const res = await request.post(`${CONFIG.baseURL}/Users/AuthenticateByName`, {
    headers: clientAuthHeaders(clientName, deviceId, null, deviceName),
    data: { Username: CONFIG.user, Pw: CONFIG.pass },
  });
  expect(res.ok(), `auth as ${clientName} status ${res.status()}`).toBeTruthy();
  const j = await res.json();
  return { token: j.AccessToken, userId: j.User.Id, headers: clientAuthHeaders(clientName, deviceId, j.AccessToken, deviceName) };
}

// Triggers a Jellyfin scheduled task by key and waits until it is Idle again. DVR tests use
// this with 'RefreshGuide': TVHeadend renumbers EPG event ids on every grab, so Jellyfin's
// cached ExternalProgramId mappings go stale — a timer created from a stale program id would
// target the WRONG TVHeadend event.
async function runScheduledTask(request, token, key, { timeoutMs = 300000 } = {}) {
  const tasksRes = await request.get(`${CONFIG.baseURL}/ScheduledTasks`, { headers: authHeaders(token) });
  expect(tasksRes.ok(), `ScheduledTasks status ${tasksRes.status()}`).toBeTruthy();
  const task = ((await tasksRes.json()) || []).find((t) => t.Key === key);
  if (!task) throw new Error(`Scheduled task '${key}' not found`);
  await request.post(`${CONFIG.baseURL}/ScheduledTasks/Running/${task.Id}`, { headers: authHeaders(token) });
  await pollUntil(async () => {
    const res = await request.get(`${CONFIG.baseURL}/ScheduledTasks`, { headers: authHeaders(token) });
    if (!res.ok()) return null;
    const t = ((await res.json()) || []).find((x) => x.Key === key);
    return t && t.State === 'Idle' ? t : null;
  }, { timeoutMs, intervalMs: 3000, label: `scheduled task '${key}' back to Idle` });
}

// ---- Direct TVHeadend API access (HTTP digest auth) ----
// TVHeadend only accepts digest authentication (basic is rejected with 401), which neither
// Playwright's request context nor plain fetch support natively — so the challenge/response
// round-trip (RFC 7616, MD5 + qop=auth) is implemented here.

function buildDigestAuthorization(challenge, method, uriPath) {
  const param = (name) => {
    const m = challenge.match(new RegExp(`${name}="([^"]+)"`)) || challenge.match(new RegExp(`${name}=([^",\\s]+)`));
    return m ? m[1] : '';
  };
  const realm = param('realm');
  const nonce = param('nonce');
  const opaque = param('opaque');
  const md5 = (s) => crypto.createHash('md5').update(s).digest('hex');
  const ha1 = md5(`${TVH.user}:${realm}:${TVH.pass}`);
  const ha2 = md5(`${method}:${uriPath}`);
  const nc = '00000001';
  const cnonce = crypto.randomBytes(8).toString('hex');
  const response = md5(`${ha1}:${nonce}:${nc}:${cnonce}:auth:${ha2}`);
  let auth = `Digest username="${TVH.user}", realm="${realm}", nonce="${nonce}", uri="${uriPath}", qop=auth, nc=${nc}, cnonce="${cnonce}", response="${response}"`;
  if (opaque) auth += `, opaque="${opaque}"`;
  return auth;
}

// Performs a digest-authenticated fetch against the TVHeadend backend. `path` must include
// the query string (the digest `uri` field has to match the request target exactly).
// Stops every active Jellyfin playback session and its server-side encoding. Browser tests
// leave web-client transcodes running for minutes after the page closes, and each one holds
// a TVHeadend tuner slot — later tuning tests starve without this.
async function stopAllPlaybackSessions(request, token) {
  try {
    const res = await request.get(`${CONFIG.baseURL}/Sessions`, { headers: authHeaders(token) });
    if (!res.ok()) return;
    for (const s of await res.json()) {
      if (s.NowPlayingItem || s.TranscodingInfo) {
        await request.post(`${CONFIG.baseURL}/Sessions/${s.Id}/Playing/Stop`, { headers: authHeaders(token) }).catch(() => {});
      }
      if (s.DeviceId) {
        await request.delete(`${CONFIG.baseURL}/Videos/ActiveEncodings?deviceId=${encodeURIComponent(s.DeviceId)}`, { headers: authHeaders(token) }).catch(() => {});
      }
    }
  } catch {
    // Best-effort cleanup only.
  }
}

async function tvhFetch(path, { method = 'GET' } = {}) {
  const url = `${TVH.baseURL}${path}`;
  const first = await fetch(url, { method });
  if (first.status !== 401) return first;
  const challenge = first.headers.get('www-authenticate') || '';
  await first.arrayBuffer().catch(() => {}); // drain the 401 body so the socket is reusable
  if (!/digest/i.test(challenge)) {
    throw new Error(`TVHeadend ${path}: expected a Digest challenge, got: ${challenge || '(none)'}`);
  }
  return fetch(url, { method, headers: { Authorization: buildDigestAuthorization(challenge, method, path) } });
}

// Digest-authenticated JSON call against the TVHeadend API — throws on any non-2xx status.
async function tvhApi(path, opts = {}) {
  const res = await tvhFetch(path, opts);
  if (!res.ok) {
    const body = await res.text().catch(() => '');
    throw new Error(`TVHeadend API ${path} -> HTTP ${res.status} ${body.slice(0, 200)}`);
  }
  return res.json();
}

// Digest-authenticated variant of fetchStreamBytes for TVHeadend-served content (e.g.
// /dvrfile/<uuid> recording playback). Resolves the challenge first, then streams bytes.
async function tvhStreamBytes(path, opts = {}) {
  const url = `${TVH.baseURL}${path}`;
  const probe = await fetch(url, { method: 'HEAD' }).catch(() => null);
  const challenge = probe ? (probe.headers.get('www-authenticate') || '') : '';
  if (probe) await probe.arrayBuffer().catch(() => {});
  const headers = /digest/i.test(challenge)
    ? { Authorization: buildDigestAuthorization(challenge, 'GET', path) }
    : undefined;
  return fetchStreamBytes(url, { ...opts, headers });
}

// ---- Misc plugin endpoints ----

// TVHeadend channel UUIDs (relay/stream endpoints are keyed by these, not Jellyfin item ids).
async function getTvhChannels(request, token) {
  const res = await request.get(`${CONFIG.baseURL}/TvHeadendApi/StreamingProfiles/Channels`, { headers: authHeaders(token) });
  expect(res.ok(), `TVH channels status ${res.status()}`).toBeTruthy();
  return res.json();
}

// Generates (and stores in the plugin config) a TVHeadend auth token — required for
// DirectToTvheadend delivery. The token is alphanumeric and FFmpeg-URL-safe.
async function generateAuthToken(request, token) {
  const res = await request.post(`${CONFIG.baseURL}/TvHeadendApi/GenerateAuthToken`, { headers: authHeaders(token), timeout: 60000 });
  expect(res.ok(), `GenerateAuthToken status ${res.status()}`).toBeTruthy();
  const j = await res.json();
  expect(j.Success, `GenerateAuthToken failed: ${j.Message}`).toBeTruthy();
  return j.AuthToken;
}

// Waits until video playback advances by deltaSeconds relative to the first sample.
// Handles channel zaps: when currentTime falls back (player restarted), the baseline resets.
async function waitForPlaybackAdvance(page, deltaSeconds = 2, timeoutMs = 60000) {
  const deadline = Date.now() + timeoutMs;
  let base = null;
  let last = null;
  while (Date.now() < deadline) {
    const v = await readVideo(page);
    if (v) {
      last = v;
      if (v.errorCode) return { ok: false, v };
      if (base === null || v.currentTime < base - 0.5) base = v.currentTime; // (re-)baseline
      if (v.currentTime >= base + deltaSeconds) return { ok: true, v };
    }
    await page.waitForTimeout(2000);
  }
  return { ok: false, v: last };
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
  CONFIG, TVH, tvhFetch, tvhApi, tvhStreamBytes,
  authHeaders, authenticate, getServerId, getPluginId, getChannels, pickChannel,
  ensureLiveTvChannels, ensureManagedProfile,
  deviceProfiles, requestPlaybackInfo, uiLogin, openPluginPage, playChannel, readVideo,
  waitForPlaybackStart, captureMediaErrors,
  readPluginConfig, writePluginConfig, refreshProfileCache, canonicalConfigDeviations,
  runScheduledTask,
  pollUntil, waitForDiagnoseChannels, fetchStreamBytes, rewriteHost, HEALTHY_DIAGNOSE_STATUSES,
  closeLiveStream, stopActiveEncoding, stopAllPlaybackSessions, fetchHlsArtifact,
  clientAuthHeaders, authenticateAs, getTvhChannels, generateAuthToken, waitForPlaybackAdvance,
};
