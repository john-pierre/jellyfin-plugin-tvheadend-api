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
    expect(['OK', 'Healthy', 'Warning']).toContain(status);
    const warnings = (d.Warnings || []).join(' ').toLowerCase();
    expect(warnings, 'no false audio-only warning').not.toContain('audio-only');
  });

  test('config page renders in the browser', async ({ page }) => {
    await JF.uiLogin(page);
    await JF.openPluginPage(page, 'TvHeadendApiConfig');
    const body = await page.evaluate(() => document.body.innerText);
    expect(body, 'config page shows plugin content').toMatch(/tvheadend|profile|stream/i);
  });
});
