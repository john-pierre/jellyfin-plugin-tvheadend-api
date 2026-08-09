// Settings matrix (UI level): drives the REAL ConfigPage in the browser across all six tabs and
// proves the page's own save/load round-trip drops or coerces NO visible field — the historic bug
// class here is padding-minutes zeroing, select-value drops, and rule resets (see 01-settings.spec.js
// for the first, narrower regression guard this file generalizes to the whole page).
//
// Connectivity fields (Host/Port/credentials + UseSSL/IgnoreCertificateErrors/Webroot/
// AllowAnonymousAccess/AuthToken) are intentionally NOT mutated — only asserted to still render
// the current value — because touching them could break every other spec's ability to reach the
// live stack. Two checkboxes (EnableMediaInfoCacheWrite/EnableMediaInfoCacheValidation) are
// `display:none` in the current markup (deprecated — cache is now always active when Stream
// Probing is on) and are therefore excluded as NOT VISIBLE, per the task's own scope ("every
// VISIBLE input").
//
// Interaction notes learned from the live page (see 01-settings.spec.js precedent): the
// `emby-checkbox` custom element's label span intercepts plain pointer clicks on the native
// input, so checkboxes are toggled with `.click({ force: true })` rather than `.check()`/
// `.setChecked()`. Text/number/select inputs work fine with plain `.fill()`/`.selectOption()`
// once their tab is active (a `.tvh-tab-panel` that is not the active tab is `display:none`,
// which fill/click's actionability checks correctly refuse to act on).
const { test, expect, request } = require('@playwright/test');
const JF = require('../fixtures/jellyfin');

test.describe.configure({ mode: 'serial' });

const TABS = ['tab-connection', 'tab-relay', 'tab-streaming', 'tab-playback', 'tab-recording', 'tab-advanced'];

// Connectivity-critical: render-only, never mutated (see file header).
const CONNECTIVITY_FIELDS = [
  { id: 'Host', kind: 'text' },
  { id: 'Port', kind: 'number' },
  { id: 'UseSSL', kind: 'checkbox' },
  { id: 'IgnoreCertificateErrors', kind: 'checkbox' },
  { id: 'Webroot', kind: 'text' },
  { id: 'AllowAnonymousAccess', kind: 'checkbox' },
  { id: 'Username', kind: 'text' },
  { id: 'Password', kind: 'text' },
  { id: 'AuthToken', kind: 'text' }, // generated secret; alphanumeric-only invariant covered at API level in 07
];

// Hidden (display:none) — deprecated toggles still bound to real config fields but not a
// "visible input" per the task's scope. Documented and excluded, not silently skipped.
const HIDDEN_EXCLUDED_FIELDS = ['EnableMediaInfoCacheWrite', 'EnableMediaInfoCacheValidation'];

// Mutated fields: id -> config path resolver + kind. `configPath(cfg)` reads the CURRENT value
// from a freshly-read plugin configuration so the target value is always computed relative to
// whatever the live stack currently holds (no hard-coded assumptions about current values).
function cfgPath(id) {
  return id.startsWith('SPS_') ? (cfg) => cfg.StreamingProfileSettings[id.slice(4)] : (cfg) => cfg[id];
}

const MUTATED_FIELDS = [
  // -- tab-relay --
  { tab: 'tab-relay', id: 'RelayEnabled', kind: 'checkbox' },
  { tab: 'tab-relay', id: 'RelayHostOverride', kind: 'text' },
  { tab: 'tab-relay', id: 'EnableRelayTokenSecurity', kind: 'checkbox' },
  { tab: 'tab-relay', id: 'StreamTokenTtlSeconds', kind: 'number' },
  { tab: 'tab-relay', id: 'StreamTokenMaxUses', kind: 'number' },
  { tab: 'tab-relay', id: 'ImageTokenTtlMinutes', kind: 'number' },
  { tab: 'tab-relay', id: 'ImageTokenMaxUses', kind: 'number' },
  { tab: 'tab-relay', id: 'EnableTokenReuse', kind: 'checkbox' },
  { tab: 'tab-relay', id: 'StrictScopeValidation', kind: 'checkbox' },
  { tab: 'tab-relay', id: 'CleanupExpiredTokensIntervalMinutes', kind: 'number' },
  { tab: 'tab-relay', id: 'TokenValidationClockSkewSeconds', kind: 'number' },

  // -- tab-streaming --
  { tab: 'tab-streaming', id: 'StreamDeliveryMode', kind: 'select', alt: 'DirectToTvheadend' },
  { tab: 'tab-streaming', id: 'FallbackMaxStreamingBitrate', kind: 'number' },
  { tab: 'tab-streaming', id: 'IsInfiniteStream', kind: 'checkbox' },
  { tab: 'tab-streaming', id: 'SPS_PassThroughProfile', kind: 'text', collapsible: true },
  { tab: 'tab-streaming', id: 'SPS_JellyfinTranscodeProfile', kind: 'text', collapsible: true },
  { tab: 'tab-streaming', id: 'SPS_FallbackTvHeadendProfile', kind: 'text', collapsible: true },
  { tab: 'tab-streaming', id: 'SPS_EnableResolutionDiagnostics', kind: 'checkbox', collapsible: true },

  // -- tab-playback --
  { tab: 'tab-playback', id: 'SupportsDirectPlay', kind: 'checkbox' },
  { tab: 'tab-playback', id: 'SupportsDirectStream', kind: 'checkbox' },
  { tab: 'tab-playback', id: 'SupportsTranscoding', kind: 'checkbox' },
  { tab: 'tab-playback', id: 'SupportsProbing', kind: 'checkbox' },
  { tab: 'tab-playback', id: 'IgnoreDts', kind: 'checkbox' },
  { tab: 'tab-playback', id: 'EnableJellyfinMetadataEnrichment', kind: 'checkbox' },
  { tab: 'tab-playback', id: 'BufferMs', kind: 'number' },
  { tab: 'tab-playback', id: 'AnalyzeDurationMs', kind: 'number' },

  // -- tab-recording -- (PrePaddingSeconds/PostPaddingSeconds handled separately: the UI edits
  // MINUTES while the config stores SECONDS — see the `minutes` block below.)
  { tab: 'tab-recording', id: 'RecordingProfile', kind: 'select', alt: 'test-dvr' },
  { tab: 'tab-recording', id: 'Priority', kind: 'number' },
  { tab: 'tab-recording', id: 'SeriesRecordNewOnly', kind: 'checkbox' },
  { tab: 'tab-recording', id: 'SeriesRecordAnyTime', kind: 'checkbox' },
  { tab: 'tab-recording', id: 'SeriesRecordAnyChannel', kind: 'checkbox' },

  // -- tab-advanced --
  { tab: 'tab-advanced', id: 'AuthTokenMaxAttempts', kind: 'number' },
  { tab: 'tab-advanced', id: 'StatisticsRetentionPeriod', kind: 'select', alt: 'SixMonths' },
  { tab: 'tab-advanced', id: 'PluginLogLevelOverride', kind: 'select', alt: 'Debug' },
  { tab: 'tab-advanced', id: 'StorePluginLogsInSqlite', kind: 'checkbox' },
  { tab: 'tab-advanced', id: 'StoreTvHeadendLogsInSqlite', kind: 'checkbox' },
  { tab: 'tab-advanced', id: 'TvHeadendLogImportEnabled', kind: 'checkbox' },
  { tab: 'tab-advanced', id: 'EnableDebugLogSanitization', kind: 'checkbox' },
  { tab: 'tab-advanced', id: 'MaxDashboardLogEntries', kind: 'number' },
  { tab: 'tab-advanced', id: 'LogRetentionDays', kind: 'number' },
];

// PrePaddingSeconds/PostPaddingSeconds: the UI displays/edits whole MINUTES
// (setMinutesFromSeconds/minutesToSeconds in ConfigPage.html) while the config stores SECONDS.
// The canonical default (5s) rounds to "0" minutes, so this is the exact "padding zeroing" bug
// class the task calls out: a naive test that fills the field with a raw seconds-shaped number
// would misjudge what actually persists. Pick whole-minute targets and assert the PERSISTED
// value is minutes*60, not the raw number typed into the box.
const MINUTES_FIELDS = [
  { tab: 'tab-recording', id: 'PrePaddingSeconds', minutes: 3 },
  { tab: 'tab-recording', id: 'PostPaddingSeconds', minutes: 4 },
];

async function activateTab(page, tabId) {
  await page.locator(`.tvh-tab-btn[data-tab="${tabId}"]`).click();
  await page.waitForTimeout(150);
}

function computeTarget(field, currentValue) {
  if (field.kind === 'checkbox') return !currentValue;
  if (field.kind === 'number') return Number(currentValue) + 1;
  if (field.kind === 'text') return `${currentValue}-ui-e2e`;
  if (field.kind === 'select') return field.alt;
  throw new Error(`no target strategy for kind '${field.kind}'`);
}

async function applyField(page, field, target) {
  const loc = page.locator(`#${field.id}`);
  if (field.kind === 'checkbox') {
    const cur = await loc.isChecked();
    if (cur !== target) await loc.click({ force: true });
  } else if (field.kind === 'select') {
    await loc.selectOption(target);
  } else {
    await loc.fill(String(target));
  }
}

async function readField(page, field) {
  const loc = page.locator(`#${field.id}`);
  if (field.kind === 'checkbox') return loc.isChecked();
  return loc.inputValue();
}

// Normalizes a raw config value into the shape `readField` returns for that field kind, so both
// sides of every comparison in this file go through the same formatting rule.
function expectedRenderedValue(field, rawValue) {
  if (field.kind === 'checkbox') return !!rawValue;
  if (field.kind === 'number') return String(rawValue);
  return rawValue || '';
}

test.describe('UI settings matrix — ConfigPage', () => {
  let api, token, pluginId, restoreBody;

  test.beforeAll(async () => {
    api = await request.newContext({ baseURL: JF.CONFIG.baseURL, ignoreHTTPSErrors: true });
    ({ token } = await JF.authenticate(api));
    pluginId = await JF.getPluginId(api, token);
    expect(pluginId, 'plugin installed').toBeTruthy();
    restoreBody = await JF.readPluginConfig(api, token, pluginId);
  });
  test.afterAll(async () => {
    const cfg = await JF.readPluginConfig(api, token, pluginId);
    expect(JF.canonicalConfigDeviations(cfg), 'config back to canonical').toEqual([]);
    await api.dispose();
  });

  test('every visible non-connectivity input round-trips through a real UI save; connectivity fields render unchanged', async ({ page }) => {
    test.setTimeout(300000);
    const before = JSON.parse(JSON.stringify(restoreBody));
    const preSweepConnectivity = {};
    for (const f of CONNECTIVITY_FIELDS) preSweepConnectivity[f.id] = cfgPath(f.id)(before);

    // Expected values, computed relative to what the live config currently holds.
    const expected = {};
    for (const f of MUTATED_FIELDS) expected[f.id] = computeTarget(f, cfgPath(f.id)(before));
    const expectedDefaultProfile = 'test-pass';

    try {
      await JF.uiLogin(page);
      await JF.openPluginPage(page, 'TvHeadendApiConfig');
      await page.waitForFunction(() => {
        const el = document.querySelector('#Host');
        return el && el.value !== '';
      }, { timeout: 20000 });
      // Profile discovery (incl. the Default row select) is async — wait for it before touching
      // the streaming tab.
      await page.waitForFunction(() => {
        const row = document.querySelector('#ProfileAssignmentsBody tr.tvh-row-default [data-field="profile"]');
        return !!row && row.tagName === 'SELECT' && row.options.length > 1;
      }, { timeout: 20000 });

      // ── Mutate every field, tab by tab ──────────────────────────────
      const perTabCount = {};
      for (const tabId of TABS) {
        await activateTab(page, tabId);
        const fieldsHere = MUTATED_FIELDS.filter((f) => f.tab === tabId);
        for (const f of fieldsHere) {
          if (f.collapsible) {
            const detailsOpen = await page.locator('.tvh-collapsible').getAttribute('open');
            if (detailsOpen === null) await page.locator('.tvh-collapsible summary').click();
          }
          await applyField(page, f, expected[f.id]);
        }
        perTabCount[tabId] = fieldsHere.length;

        if (tabId === 'tab-streaming') {
          const defSel = page.locator('#ProfileAssignmentsBody tr.tvh-row-default select[data-field="profile"]');
          await defSel.selectOption(expectedDefaultProfile);
          perTabCount[tabId] += 1;
        }
        if (tabId === 'tab-recording') {
          for (const mf of MINUTES_FIELDS) {
            await page.locator(`#${mf.id}`).fill(String(mf.minutes));
          }
          perTabCount[tabId] += MINUTES_FIELDS.length;
        }
      }
      perTabCount['tab-connection'] = CONNECTIVITY_FIELDS.length; // render-only, counted for coverage reporting

      // ── Save once via the real submit button ────────────────────────
      const saveResponse = page.waitForResponse(
        (r) => /\/Configuration$/.test(r.url()) && r.request().method() === 'POST',
        { timeout: 20000 },
      );
      await page.locator('#TvHeadendApiConfigForm button[type="submit"]').click({ force: true });
      const saveRes = await saveResponse;
      expect(saveRes.ok(), 'config save POST succeeded').toBeTruthy();

      // Cross-check the minutes->seconds conversion directly against the persisted API value —
      // the concrete regression guard for the "padding zeroing" bug class.
      const savedCfg = await JF.readPluginConfig(api, token, pluginId);
      for (const mf of MINUTES_FIELDS) {
        expect(savedCfg[mf.id], `${mf.id} persisted as ${mf.minutes} minutes in seconds`).toBe(mf.minutes * 60);
      }
      expect(savedCfg.StreamingProfileSettings.DefaultTvHeadendProfile, 'default profile select persisted').toBe(expectedDefaultProfile);

      // ── Hard reload and re-assert every field from the freshly-rendered DOM ─────────────
      await page.reload({ waitUntil: 'domcontentloaded' });
      await page.waitForTimeout(6000);
      await page.waitForFunction(() => {
        const el = document.querySelector('#Host');
        return el && el.value !== '';
      }, { timeout: 20000 });

      let assertedCount = 0;
      for (const f of MUTATED_FIELDS) {
        const rendered = await readField(page, f);
        expect(rendered, `#${f.id} shows the changed value after hard reload`).toEqual(expectedRenderedValue(f, expected[f.id]));
        assertedCount++;
      }
      for (const mf of MINUTES_FIELDS) {
        const rendered = await page.locator(`#${mf.id}`).inputValue();
        expect(rendered, `#${mf.id} shows ${mf.minutes} minutes after hard reload`).toBe(String(mf.minutes));
        assertedCount++;
      }
      const defSelAfter = page.locator('#ProfileAssignmentsBody tr.tvh-row-default select[data-field="profile"]');
      expect(await defSelAfter.inputValue(), 'Default profile select shows test-pass after hard reload').toBe(expectedDefaultProfile);
      assertedCount++;

      for (const f of CONNECTIVITY_FIELDS) {
        const rendered = await readField(page, f);
        expect(rendered, `#${f.id} (connectivity) still renders the current value, untouched`).toEqual(expectedRenderedValue(f, preSweepConnectivity[f.id]));
        assertedCount++;
      }

      const totalCoverage = Object.entries(perTabCount).map(([t, n]) => `${t}=${n}`).join(', ');
      console.log(`[09] UI coverage by tab: ${totalCoverage} (total examined=${assertedCount})`);
    } finally {
      // ── Restore the exact pre-test config via API and reload to confirm the UI reflects it ──
      await JF.writePluginConfig(api, token, pluginId, restoreBody);
      const restored = await JF.readPluginConfig(api, token, pluginId);
      expect(restored, 'config restored exactly via API').toEqual(restoreBody);

      await page.reload({ waitUntil: 'domcontentloaded' });
      await page.waitForTimeout(6000);
      await page.waitForFunction(() => {
        const el = document.querySelector('#Host');
        return el && el.value !== '';
      }, { timeout: 20000 });

      for (const f of MUTATED_FIELDS) {
        const rendered = await readField(page, f);
        expect(rendered, `#${f.id} shows the restored value after reload`).toEqual(expectedRenderedValue(f, cfgPath(f.id)(restoreBody)));
      }
      for (const mf of MINUTES_FIELDS) {
        const rendered = await page.locator(`#${mf.id}`).inputValue();
        const restoredSeconds = restoreBody[mf.id];
        const wantMinutes = Number.isFinite(restoredSeconds) && restoredSeconds > 0 ? Math.round(restoredSeconds / 60) : 0;
        expect(rendered, `#${mf.id} shows the restored minutes value after reload`).toBe(String(wantMinutes));
      }
      const defSelRestored = page.locator('#ProfileAssignmentsBody tr.tvh-row-default select[data-field="profile"]');
      await page.locator('.tvh-tab-btn[data-tab="tab-streaming"]').click();
      await page.waitForTimeout(150);
      expect(await defSelRestored.inputValue(), 'Default profile select shows restored profile after reload').toBe(restoreBody.StreamingProfileSettings.DefaultTvHeadendProfile);

      await JF.refreshProfileCache(api, token);
      await JF.waitForDiagnoseChannels(api, token);
    }
  });

  test('excluded fields are documented: hidden deprecated toggles are not user-visible', async ({ page }) => {
    await JF.uiLogin(page);
    await JF.openPluginPage(page, 'TvHeadendApiConfig');
    await page.locator('.tvh-tab-btn[data-tab="tab-playback"]').click();
    await page.waitForTimeout(150);
    for (const id of HIDDEN_EXCLUDED_FIELDS) {
      const visible = await page.locator(`#${id}`).isVisible();
      console.log(`[09] hidden-excluded field #${id}: visible=${visible}`);
      expect(visible, `#${id} is display:none (deprecated toggle, correctly excluded from the visible-input matrix)`).toBeFalsy();
    }
  });
});
