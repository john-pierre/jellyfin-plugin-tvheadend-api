// DVR end-to-end: single timers, series timers (autorec), and a REAL recording cycle —
// the first coverage of Service/Dvr/ (SingleTimerService + SeriesTimerService) in this suite.
//
// Flow per test: drive Jellyfin's public LiveTV API (POST /LiveTv/Timers from
// GET /LiveTv/Timers/Defaults?programId=… of a real EPG program), then cross-check the
// plugin's TVHeadend side effects directly against the TVHeadend API (digest auth via the
// tvhApi fixture helper) — created timers must materialize as DVR entries / autorec rules,
// deletes must remove them again.
//
// Platform note (verified against the Jellyfin 10.10 source, MediaBrowser.Controller/LiveTv/
// ILiveTvService.cs): the ILiveTvService interface has NO recording-listing surface — finished
// recordings of third-party providers can NOT appear in GET /LiveTv/Recordings (that endpoint
// only serves Jellyfin's embedded recorder). The real-recording test therefore proves the full
// chain that IS observable: Jellyfin timer lifecycle (New -> InProgress -> Completed) plus the
// recorded file on the TVHeadend side (filesize > 0, playable TS bytes via /dvrfile/<uuid>),
// and asserts /LiveTv/Recordings stays at its baseline as documentation of that platform limit.
//
// Cleanup discipline: every created timer/autorec/DVR entry is removed again (completed DVR
// entries via the TVHeadend API — Jellyfin cannot delete those), and afterAll asserts the
// timer list and TVHeadend DVR grid are back to their pre-spec baseline.
const { test, expect, request } = require('@playwright/test');
const JF = require('../fixtures/jellyfin');

test.describe.configure({ mode: 'serial' });

async function getTimers(api, token) {
  const res = await api.get(`${JF.CONFIG.baseURL}/LiveTv/Timers`, { headers: JF.authHeaders(token) });
  expect(res.ok(), `GET Timers status ${res.status()}`).toBeTruthy();
  return (await res.json()).Items || [];
}

async function getSeriesTimers(api, token) {
  const res = await api.get(`${JF.CONFIG.baseURL}/LiveTv/SeriesTimers`, { headers: JF.authHeaders(token) });
  expect(res.ok(), `GET SeriesTimers status ${res.status()}`).toBeTruthy();
  return (await res.json()).Items || [];
}

async function getTimerDefaults(api, token, programId) {
  const res = await api.get(`${JF.CONFIG.baseURL}/LiveTv/Timers/Defaults?programId=${encodeURIComponent(programId)}`, { headers: JF.authHeaders(token) });
  expect(res.ok(), `Timers/Defaults status ${res.status()}`).toBeTruthy();
  return res.json();
}

// Real EPG programs (bootstrap guarantees XMLTV coverage for all channels). minStartOffsetMs
// keeps CRUD-only timers safely in the future so they never actually start recording.
async function findPrograms(api, token, userId, { minStartOffsetMs = 0, airing = false, limit = 20 } = {}) {
  const q = new URLSearchParams({ userId, limit: String(limit), sortBy: 'StartDate' });
  if (airing) q.set('isAiring', 'true');
  else q.set('minStartDate', new Date(Date.now() + minStartOffsetMs).toISOString());
  const res = await api.get(`${JF.CONFIG.baseURL}/LiveTv/Programs?${q}`, { headers: JF.authHeaders(token) });
  expect(res.ok(), `GET Programs status ${res.status()}`).toBeTruthy();
  const items = (await res.json()).Items || [];
  expect(items.length, 'EPG programs available for DVR tests').toBeGreaterThan(0);
  return items;
}

async function tvhDvrEntries() {
  const grid = await JF.tvhApi('/api/dvr/entry/grid?limit=500');
  return grid.entries || [];
}

// All 100 simulator channels share the same hourly EPG grid, so during the last minutes of
// an hour NO airing program has enough runtime left for a recording test — a timer created
// then completes (or misses) before it ever reports InProgress. Poll across the hour
// boundary until a freshly started program with enough remaining runtime exists.
async function findAiringWithRuntime(api, token, userId, minRemainingMs) {
  return JF.pollUntil(async () => {
    const q = new URLSearchParams({ userId, limit: '20', sortBy: 'StartDate', isAiring: 'true' });
    const res = await api.get(`${JF.CONFIG.baseURL}/LiveTv/Programs?${q}`, { headers: JF.authHeaders(token) });
    if (!res.ok()) return null;
    const items = (await res.json()).Items || [];
    return items.find((p) => new Date(p.EndDate).getTime() - Date.now() > minRemainingMs) || null;
  }, { timeoutMs: 420000, intervalMs: 15000, label: `an airing program with >${Math.round(minRemainingMs / 60000)}min remaining` });
}

test.describe('DVR: timers, series timers, real recording', () => {
  let api, token, userId;
  // Pre-spec baselines — afterAll proves the spec leaves the stack exactly as it found it.
  let baselineTimerIds, baselineDvrUuids, baselineRecordingCount;

  test.beforeAll(async () => {
    test.setTimeout(720000);
    api = await request.newContext({ baseURL: JF.CONFIG.baseURL, ignoreHTTPSErrors: true });
    ({ token, userId } = await JF.authenticate(api));
    expect(await JF.ensureLiveTvChannels(api, token, userId), 'Live TV channels available').toBeTruthy();

    // TVHeadend renumbers EPG event ids on every grab; Jellyfin's guide cache then maps
    // program ids to STALE TVHeadend events and a created timer would record the wrong show
    // (observed live: a "Kids Show" program id resolved to a "Music Show" event). Refresh the
    // guide so ExternalProgramId mappings are current before any timer is created. (The
    // plugin now ALSO re-validates event ids server-side and falls back to time-based
    // entries — see the custom-time-window test below — but program-bound assertions in the
    // tests here still need fresh mappings.) Normally ~2min; the FIRST refresh after a
    // Jellyfin restart can queue behind startup scans, hence the generous deadline.
    await JF.runScheduledTask(api, token, 'RefreshGuide', { timeoutMs: 600000 });

    baselineTimerIds = (await getTimers(api, token)).map((t) => t.Id).sort();
    baselineDvrUuids = (await tvhDvrEntries()).map((e) => e.uuid).sort();
    const recRes = await api.get(`${JF.CONFIG.baseURL}/LiveTv/Recordings`, { headers: JF.authHeaders(token) });
    baselineRecordingCount = (await recRes.json()).TotalRecordCount || 0;
    console.log(`[12] baseline: ${baselineTimerIds.length} timers, ${baselineDvrUuids.length} TVH DVR entries, ${baselineRecordingCount} Jellyfin recordings`);
  });

  test.afterAll(async () => {
    // The stack must be back at its pre-spec DVR baseline — no leaked timers/entries.
    const timerIds = (await getTimers(api, token)).map((t) => t.Id).sort();
    expect(timerIds, 'Jellyfin timers back to baseline').toEqual(baselineTimerIds);
    const dvrUuids = (await tvhDvrEntries()).map((e) => e.uuid).sort();
    expect(dvrUuids, 'TVHeadend DVR entries back to baseline').toEqual(baselineDvrUuids);
    const autorec = await JF.tvhApi('/api/dvr/autorec/grid?limit=50');
    expect(autorec.total, 'no leftover autorec rules').toBe(0);
    await JF.waitForDiagnoseChannels(api, token);
    await api.dispose();
  });

  test('single timer: create from EPG program defaults, list, delete', async () => {
    test.setTimeout(120000);
    // A program comfortably in the future — the timer must never start recording.
    const program = (await findPrograms(api, token, userId, { minStartOffsetMs: 45 * 60000 }))[0];
    console.log(`[12|timer] program "${program.Name}" on ${program.ChannelId}, ${program.StartDate} -> ${program.EndDate}`);
    expect(program.Name, 'program has a title').toBeTruthy();

    const defaults = await getTimerDefaults(api, token, program.Id);
    expect(defaults.ProgramId, 'defaults bound to the program').toBe(program.Id);
    expect(defaults.ChannelId, 'defaults carry the program channel').toBe(program.ChannelId);

    let createdId = null;
    try {
      const createRes = await api.post(`${JF.CONFIG.baseURL}/LiveTv/Timers`, { headers: JF.authHeaders(token), data: defaults });
      expect(createRes.ok(), `POST Timers status ${createRes.status()}`).toBeTruthy();

      // Jellyfin side: the timer lists with correct channel/time mapping.
      const timer = await JF.pollUntil(async () => {
        const timers = await getTimers(api, token);
        return timers.find((t) => t.ProgramId === program.Id) || null;
      }, { timeoutMs: 30000, intervalMs: 2000, label: 'created timer appears in GET /LiveTv/Timers' });
      createdId = timer.Id;
      console.log(`[12|timer] created ${timer.Id}, status=${timer.Status}`);
      expect(timer.ChannelId, 'timer mapped to the program channel').toBe(program.ChannelId);
      expect(new Date(timer.StartDate).getTime(), 'timer start matches program start').toBe(new Date(program.StartDate).getTime());
      expect(new Date(timer.EndDate).getTime(), 'timer end matches program end').toBe(new Date(program.EndDate).getTime());
      expect(timer.Status, 'future timer is scheduled, not recording').toBe('New');

      // TVHeadend side: a scheduled DVR entry materialized.
      const tvhEntry = (await tvhDvrEntries()).find((e) => !baselineDvrUuids.includes(e.uuid) && e.disp_title === program.Name);
      expect(tvhEntry, `TVHeadend DVR entry for "${program.Name}" exists`).toBeTruthy();
      expect(tvhEntry.sched_status, 'TVHeadend entry is scheduled').toBe('scheduled');

      // Delete and prove it is gone on both sides.
      const delRes = await api.delete(`${JF.CONFIG.baseURL}/LiveTv/Timers/${timer.Id}`, { headers: JF.authHeaders(token) });
      expect(delRes.ok(), `DELETE Timers status ${delRes.status()}`).toBeTruthy();
      await JF.pollUntil(async () => {
        const timers = await getTimers(api, token);
        return timers.some((t) => t.Id === timer.Id) ? null : true;
      }, { timeoutMs: 30000, intervalMs: 2000, label: 'deleted timer disappears from GET /LiveTv/Timers' });
      createdId = null;
      const stillThere = (await tvhDvrEntries()).some((e) => e.uuid === tvhEntry.uuid && e.sched_status === 'scheduled');
      expect(stillThere, 'TVHeadend scheduled entry cancelled').toBeFalsy();
      // A cancel can leave a non-scheduled tombstone entry behind — remove any residue
      // (best effort here; afterAll asserts the grid is REALLY back at baseline).
      const residue = (await tvhDvrEntries()).filter((e) => !baselineDvrUuids.includes(e.uuid));
      for (const e of residue) await JF.tvhApi(`/api/dvr/entry/remove?uuid=${e.uuid}`).catch(() => {});
    } finally {
      if (createdId) await api.delete(`${JF.CONFIG.baseURL}/LiveTv/Timers/${createdId}`, { headers: JF.authHeaders(token) }).catch(() => {});
    }
  });

  test('series timer: create from EPG program defaults, list, delete', async () => {
    test.setTimeout(120000);
    // A different future program than the single-timer test (rotate to the last match).
    const programs = await findPrograms(api, token, userId, { minStartOffsetMs: 45 * 60000 });
    const program = programs[programs.length - 1];
    console.log(`[12|series] program "${program.Name}" on ${program.ChannelId}`);

    const defaults = await getTimerDefaults(api, token, program.Id);
    expect(defaults.Type, 'defaults are a series timer body').toBe('SeriesTimer');

    let createdId = null;
    try {
      const createRes = await api.post(`${JF.CONFIG.baseURL}/LiveTv/SeriesTimers`, { headers: JF.authHeaders(token), data: defaults });
      expect(createRes.ok(), `POST SeriesTimers status ${createRes.status()}`).toBeTruthy();

      // Jellyfin side: series timer lists with the program name.
      const seriesTimer = await JF.pollUntil(async () => {
        const timers = await getSeriesTimers(api, token);
        return timers.find((t) => t.Name === program.Name) || null;
      }, { timeoutMs: 30000, intervalMs: 2000, label: 'series timer appears in GET /LiveTv/SeriesTimers' });
      createdId = seriesTimer.Id;
      console.log(`[12|series] created ${seriesTimer.Id} "${seriesTimer.Name}"`);
      expect(seriesTimer.Days.length, 'series timer has recording days').toBeGreaterThan(0);

      // TVHeadend side: an autorec rule materialized.
      const autorec = await JF.tvhApi('/api/dvr/autorec/grid?limit=50');
      console.log(`[12|series] TVH autorec rules: ${autorec.total}`);
      expect(autorec.total, 'autorec rule created in TVHeadend').toBeGreaterThan(0);

      // Delete and prove both sides are clean again (autorec children included).
      const delRes = await api.delete(`${JF.CONFIG.baseURL}/LiveTv/SeriesTimers/${seriesTimer.Id}`, { headers: JF.authHeaders(token) });
      expect(delRes.ok(), `DELETE SeriesTimers status ${delRes.status()}`).toBeTruthy();
      await JF.pollUntil(async () => {
        const timers = await getSeriesTimers(api, token);
        return timers.some((t) => t.Id === seriesTimer.Id) ? null : true;
      }, { timeoutMs: 30000, intervalMs: 2000, label: 'series timer disappears from GET /LiveTv/SeriesTimers' });
      createdId = null;
      const autorecAfter = await JF.tvhApi('/api/dvr/autorec/grid?limit=50');
      expect(autorecAfter.total, 'autorec rule removed from TVHeadend').toBe(0);
      // Scheduled child entries spawned by the rule must be gone too (remove stragglers;
      // afterAll asserts the grid is REALLY back at baseline).
      const residue = (await tvhDvrEntries()).filter((e) => !baselineDvrUuids.includes(e.uuid));
      console.log(`[12|series] autorec child residue after delete: ${residue.length}`);
      for (const e of residue) await JF.tvhApi(`/api/dvr/entry/remove?uuid=${e.uuid}`).catch(() => {});
    } finally {
      if (createdId) await api.delete(`${JF.CONFIG.baseURL}/LiveTv/SeriesTimers/${createdId}`, { headers: JF.authHeaders(token) }).catch(() => {});
    }
  });

  test('real recording: record a live program, verify the file, play it, clean up', async () => {
    test.setTimeout(720000);
    // A program airing RIGHT NOW with enough runtime left — the timer starts recording
    // immediately and must be observable as InProgress before the program ends.
    const program = await findAiringWithRuntime(api, token, userId, 5 * 60000);
    console.log(`[12|rec] recording "${program.Name}" on ${program.ChannelId} (airing now, ends ${program.EndDate})`);

    // Program-bound timers go through TVHeadend's create_by_event, which takes start/stop
    // from the EVENT — so the recording is ended early via Jellyfin's update-timer API below
    // (which also exercises the plugin's UpdateTimerAsync/idnode-save path).
    const defaults = await getTimerDefaults(api, token, program.Id);

    let tvhUuid = null;
    let jfTimerId = null;
    try {
      const createRes = await api.post(`${JF.CONFIG.baseURL}/LiveTv/Timers`, { headers: JF.authHeaders(token), data: defaults });
      expect(createRes.ok(), `POST Timers status ${createRes.status()}`).toBeTruthy();

      // Recording goes active: Jellyfin timer InProgress + TVHeadend entry "recording".
      const active = await JF.pollUntil(async () => {
        const timers = await getTimers(api, token);
        const t = timers.find((x) => x.ProgramId === program.Id);
        if (t) jfTimerId = t.Id;
        return t && t.Status === 'InProgress' ? t : null;
      }, { timeoutMs: 90000, intervalMs: 3000, label: 'timer reports Status=InProgress' });
      console.log(`[12|rec] Jellyfin timer ${active.Id} InProgress`);

      // The filename is assigned a moment after the state flips to "recording" (TVHeadend
      // creates the file once the first stream data arrives) — poll for both together.
      const tvhEntry = await JF.pollUntil(async () => {
        const e = (await tvhDvrEntries()).find((x) => !baselineDvrUuids.includes(x.uuid) && x.disp_title === program.Name);
        if (e) tvhUuid = e.uuid; // track as early as possible for the fail-safe cleanup
        return e && e.sched_status === 'recording' && e.filename ? e : null;
      }, { timeoutMs: 45000, intervalMs: 2000, label: 'TVHeadend DVR entry state=recording with a file' });
      expect(tvhEntry.filename, 'recording writes to a real file').toBeTruthy();
      console.log(`[12|rec] TVH recording ${tvhUuid} -> ${tvhEntry.filename}`);

      // Record for ~50s of real content.
      await new Promise((r) => setTimeout(r, 50000));

      // End the recording via Jellyfin's update-timer API: pull EndDate forward to now+15s.
      // The plugin maps this to a TVHeadend idnode/save with the new stop time.
      const current = (await getTimers(api, token)).find((t) => t.Id === jfTimerId);
      expect(current, 'timer still listed while recording').toBeTruthy();
      expect(current.Status, 'timer still recording before the stop update').toBe('InProgress');
      current.EndDate = new Date(Date.now() + 15000).toISOString();
      const updRes = await api.post(`${JF.CONFIG.baseURL}/LiveTv/Timers/${jfTimerId}`, { headers: JF.authHeaders(token), data: current });
      expect(updRes.ok(), `POST Timers/{id} (update) status ${updRes.status()}`).toBeTruthy();

      // The pulled-forward stop time completes the recording autonomously.
      const completed = await JF.pollUntil(async () => {
        const e = (await tvhDvrEntries()).find((x) => x.uuid === tvhUuid);
        return e && e.sched_status === 'completed' ? e : null;
      }, { timeoutMs: 120000, intervalMs: 5000, label: 'TVHeadend recording completes' });
      console.log(`[12|rec] completed: status="${completed.status}", filesize=${completed.filesize}, errors=${completed.errors}, data_errors=${completed.data_errors}`);
      expect(completed.filesize, 'recorded file has substantial content (~60s of TS)').toBeGreaterThan(262144);
      expect(completed.errors, 'recording finished without stream errors').toBe(0);

      // Jellyfin reflects the final state.
      const finalTimer = await JF.pollUntil(async () => {
        const t = (await getTimers(api, token)).find((x) => x.Id === jfTimerId);
        return t && t.Status === 'Completed' ? t : null;
      }, { timeoutMs: 60000, intervalMs: 3000, label: 'Jellyfin timer reports Status=Completed' });
      expect(finalTimer.Status).toBe('Completed');

      // "Play" the recording: stream the first 64KB of the recorded file. Jellyfin cannot
      // surface it (see file header — ILiveTvService has no recordings API in 10.10), so the
      // playable-bytes proof runs against TVHeadend's own file endpoint.
      const play = await JF.tvhStreamBytes(`/dvrfile/${tvhUuid}`, { minBytes: 65536, timeoutMs: 20000 });
      console.log(`[12|rec] dvrfile playback -> ${play.status}, ${play.bytes}B`);
      expect([200, 206], `dvrfile served (got ${play.status})`).toContain(play.status);
      expect(play.bytes, '64KB of recorded video read').toBeGreaterThanOrEqual(65536);

      // Documented platform limit: the recording does NOT appear in /LiveTv/Recordings.
      const recRes = await api.get(`${JF.CONFIG.baseURL}/LiveTv/Recordings`, { headers: JF.authHeaders(token) });
      const recCount = (await recRes.json()).TotalRecordCount || 0;
      console.log(`[12|rec] /LiveTv/Recordings count: ${recCount} (baseline ${baselineRecordingCount} — ILiveTvService cannot surface recordings)`);
      expect(recCount, 'recordings endpoint stays at baseline (platform limitation, see header)').toBe(baselineRecordingCount);

      // Clean up: completed entries are invisible to Jellyfin deletes — remove via TVHeadend.
      await JF.tvhApi(`/api/dvr/entry/remove?uuid=${tvhUuid}`);
      await JF.pollUntil(async () => {
        const still = (await tvhDvrEntries()).some((e) => e.uuid === tvhUuid);
        return still ? null : true;
      }, { timeoutMs: 30000, intervalMs: 2000, label: 'recording removed from TVHeadend' });
      await JF.pollUntil(async () => {
        const timers = await getTimers(api, token);
        return timers.some((t) => t.Id === jfTimerId) ? null : true;
      }, { timeoutMs: 30000, intervalMs: 2000, label: 'completed timer disappears from Jellyfin' });
      tvhUuid = null;
    } finally {
      // Fail-safe cleanup — cancel/remove whatever the failure path left behind, polling
      // until the TVHeadend grid is actually clean (a cancel on an active recording needs a
      // moment to close the file before remove succeeds).
      if (tvhUuid) {
        await JF.pollUntil(async () => {
          const entry = (await tvhDvrEntries()).find((e) => e.uuid === tvhUuid);
          if (!entry) return true;
          await JF.tvhApi(`/api/dvr/entry/cancel?uuid=${tvhUuid}`).catch(() => {});
          await JF.tvhApi(`/api/dvr/entry/remove?uuid=${tvhUuid}`).catch(() => {});
          return null;
        }, { timeoutMs: 45000, intervalMs: 3000, label: 'fail-safe removal of the test recording' }).catch(() => {});
      }
      if (jfTimerId) {
        await api.delete(`${JF.CONFIG.baseURL}/LiveTv/Timers/${jfTimerId}`, { headers: JF.authHeaders(token) }).catch(() => {});
      }
    }
  });

  // Regression guard for the stale-event/custom-window fix in SingleTimerService: a timer
  // whose requested window deviates from the EPG event's own times (here: EndDate pulled to
  // now+45s) must NOT go through create_by_event (which ignores the request's times — the
  // event's start/stop would win and, worse, a stale renumbered event id would record a
  // different show entirely). The plugin re-validates the event against TVHeadend's current
  // EPG and falls back to a TIME-BASED entry. Observable proof on the TVHeadend side: the
  // created entry has broadcast=0 (no event link — create_by_event entries carry the event
  // id) and its stop equals the REQUESTED EndDate, and the recording really stops there.
  test('custom time window: falls back to a time-based entry honoring the requested stop', async () => {
    test.setTimeout(720000);
    // An airing program with comfortably more than the validation tolerance (120s) left.
    const program = await findAiringWithRuntime(api, token, userId, 5 * 60000);
    console.log(`[12|window] program "${program.Name}" (ends ${program.EndDate})`);

    const defaults = await getTimerDefaults(api, token, program.Id);
    const requestedStop = Math.floor((Date.now() + 45000) / 1000);
    defaults.EndDate = new Date(requestedStop * 1000).toISOString();

    let tvhUuid = null;
    let jfTimerId = null;
    try {
      const createRes = await api.post(`${JF.CONFIG.baseURL}/LiveTv/Timers`, { headers: JF.authHeaders(token), data: defaults });
      expect(createRes.ok(), `POST Timers status ${createRes.status()}`).toBeTruthy();

      const tvhEntry = await JF.pollUntil(async () => {
        const e = (await tvhDvrEntries()).find((x) => !baselineDvrUuids.includes(x.uuid) && x.disp_title === program.Name);
        if (e) tvhUuid = e.uuid;
        return e || null;
      }, { timeoutMs: 30000, intervalMs: 2000, label: 'time-based DVR entry appears in TVHeadend' });
      console.log(`[12|window] entry ${tvhUuid}: broadcast=${tvhEntry.broadcast}, stop=${tvhEntry.stop} (requested ${requestedStop})`);
      expect(tvhEntry.broadcast || 0, 'entry is TIME-based (no event link) — the stale-event fallback fired').toBe(0);
      expect(Math.abs(tvhEntry.stop - requestedStop), 'TVHeadend stop equals the REQUESTED EndDate').toBeLessThanOrEqual(5);

      const jf = (await getTimers(api, token)).find((t) => t.Name === program.Name && !baselineTimerIds.includes(t.Id));
      jfTimerId = jf ? jf.Id : null;

      // The recording must actually END at the requested stop (completes on its own).
      const completed = await JF.pollUntil(async () => {
        const e = (await tvhDvrEntries()).find((x) => x.uuid === tvhUuid);
        return e && e.sched_status === 'completed' ? e : null;
      }, { timeoutMs: 120000, intervalMs: 5000, label: 'short custom-window recording completes at the requested stop' });
      console.log(`[12|window] completed: status="${completed.status}", filesize=${completed.filesize}`);
      expect(completed.filesize, 'the short recording produced real content').toBeGreaterThan(65536);

      await JF.tvhApi(`/api/dvr/entry/remove?uuid=${tvhUuid}`);
      await JF.pollUntil(async () => {
        const still = (await tvhDvrEntries()).some((e) => e.uuid === tvhUuid);
        return still ? null : true;
      }, { timeoutMs: 30000, intervalMs: 2000, label: 'custom-window recording removed from TVHeadend' });
      tvhUuid = null;
    } finally {
      if (tvhUuid) {
        await JF.pollUntil(async () => {
          const entry = (await tvhDvrEntries()).find((e) => e.uuid === tvhUuid);
          if (!entry) return true;
          await JF.tvhApi(`/api/dvr/entry/cancel?uuid=${tvhUuid}`).catch(() => {});
          await JF.tvhApi(`/api/dvr/entry/remove?uuid=${tvhUuid}`).catch(() => {});
          return null;
        }, { timeoutMs: 45000, intervalMs: 3000, label: 'fail-safe removal of the custom-window recording' }).catch(() => {});
      }
      if (jfTimerId) {
        await api.delete(`${JF.CONFIG.baseURL}/LiveTv/Timers/${jfTimerId}`, { headers: JF.authHeaders(token) }).catch(() => {});
      }
    }
  });
});
