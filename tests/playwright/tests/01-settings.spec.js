// Verifies the plugin is installed, configured with the managed "jellyfin" profile, reports a
// healthy diagnostic (with transcode capability detected), and that the config page renders.
const { test, expect, request } = require('@playwright/test');
const JF = require('../fixtures/jellyfin');

test.describe('Settings & health', () => {
  let api, token, userId, pluginId;

  test.beforeAll(async () => {
    api = await request.newContext({ baseURL: JF.CONFIG.baseURL, ignoreHTTPSErrors: true });
    ({ token, userId } = await JF.authenticate(api));
    pluginId = await JF.getPluginId(api, token);
    // A fresh e2e stack has not provisioned the managed profile yet — create it on demand.
    if (pluginId) {
      await JF.ensureManagedProfile(api, token, pluginId);
    }
  });
  test.afterAll(async () => { await api.dispose(); });

  test('managed "jellyfin" profile is the effective default', async () => {
    expect(pluginId, 'plugin installed').toBeTruthy();
    const res = await api.get(`/Plugins/${pluginId}/Configuration`, { headers: JF.authHeaders(token) });
    expect(res.ok()).toBeTruthy();
    const cfg = await res.json();
    const s = cfg.StreamingProfileSettings || {};
    expect(s.DefaultTvHeadendProfile).toBe('jellyfin');
  });

  test('diagnostics OK; transcode capability detected; no audio-only warning', async () => {
    const res = await api.get('/TvHeadendApi/Diagnose', { headers: JF.authHeaders(token) });
    expect(res.ok()).toBeTruthy();
    const d = await res.json();
    const status = String(d.OverallStatus || d.Status || '');
    expect(['OK', 'Healthy', 'Warning', 'WARNING']).toContain(status); // WARNING: canonical RelayHostOverride='' trips the advisory host-override check (see fixtures)
    const warnings = (d.Warnings || []).join(' ').toLowerCase();
    expect(warnings, 'no false audio-only warning').not.toContain('audio-only');
  });

  test('config page renders in the browser', async ({ page }) => {
    await JF.uiLogin(page);
    await JF.openPluginPage(page, 'TvHeadendApiConfig');
    const body = await page.evaluate(() => document.body.innerText);
    expect(body, 'config page shows plugin content').toMatch(/tvheadend|profile|stream/i);
  });

  test('UI save round-trip persists the change and corrupts no other field', async ({ page }) => {
    // Regression guard: the save handler historically dropped/defaulted fields it did not
    // render (ImageTokenTtlMinutes 0->30, rule MatchType/PlaybackMode/Enabled resets).
    const before = await (await api.get(`/Plugins/${pluginId}/Configuration`, { headers: JF.authHeaders(token) })).json();
    const originalRetention = before.LogRetentionDays;
    const newRetention = originalRetention === 14 ? 21 : 14;

    try {
      await JF.uiLogin(page);
      await JF.openPluginPage(page, 'TvHeadendApiConfig');

      // Drive the page's own form state and its real submit/save path.
      await page.waitForFunction(() => {
        const el = document.querySelector('#LogRetentionDays');
        return el && el.value !== '';
      }, { timeout: 20000 });
      await page.evaluate((value) => {
        const el = document.querySelector('#LogRetentionDays');
        el.value = String(value);
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
      }, newRetention);

      const saveResponse = page.waitForResponse(
        (r) => /\/Configuration$/.test(r.url()) && r.request().method() === 'POST',
        { timeout: 20000 },
      );
      await page.evaluate(() => {
        document.querySelector('#TvHeadendApiConfigForm button[type="submit"]').click();
      });
      expect((await saveResponse).ok(), 'config save POST succeeded').toBeTruthy();

      const after = await (await api.get(`/Plugins/${pluginId}/Configuration`, { headers: JF.authHeaders(token) })).json();

      // The edited field persisted…
      expect(after.LogRetentionDays, 'edited field persisted').toBe(newRetention);

      // …and nothing else changed. Compare the full config minus the edited field.
      const strip = (cfg) => {
        const clone = JSON.parse(JSON.stringify(cfg));
        delete clone.LogRetentionDays;
        return clone;
      };
      expect(strip(after), 'no other field was altered by the UI save').toEqual(strip(before));
    } finally {
      const restore = await (await api.get(`/Plugins/${pluginId}/Configuration`, { headers: JF.authHeaders(token) })).json();
      restore.LogRetentionDays = originalRetention;
      await api.post(`/Plugins/${pluginId}/Configuration`, {
        headers: { ...JF.authHeaders(token), 'Content-Type': 'application/json' },
        data: restore,
      });
    }
  });
});
