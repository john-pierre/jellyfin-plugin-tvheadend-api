# Jellyfin Plugin: TVHeadend API Integration

[![Release Version](https://img.shields.io/github/v/release/john-pierre/jellyfin-plugin-tvheadend-api)](https://github.com/john-pierre/jellyfin-plugin-tvheadend-api/releases)
[![Minimum Jellyfin Version](https://img.shields.io/badge/Jellyfin-10.10.3%2B-blue)](https://jellyfin.org)
[![Minimum TVHeadend Version](https://img.shields.io/badge/TVHeadend-4.3%2B-green)](https://tvheadend.org)
[![License](https://img.shields.io/github/license/john-pierre/jellyfin-plugin-tvheadend-api)](LICENSE)

A Jellyfin Live TV plugin for TVHeadend that works entirely through HTTP/JSON APIs.

This README is written for normal Jellyfin users first (setup, playback behavior, troubleshooting), and then includes a short section for developers and AI agents.

## What Makes This Plugin Different?

Compared to the official TVHeadend plugin path many users know, this plugin is optimized for:

- API-only integration (no HTSP dependency).
- Very fast channel switching when configured correctly.
- Better direct play behavior by combining profile strategy and probe-cache workflow.
- Built-in diagnostics to explain why playback is fast or slow.
- Guided setup helpers from the plugin UI (profile creation, token generation, diagnostics).
- Active-channel focused guide loading (disabled TVHeadend channels are skipped).
- Better channel grouping/tag metadata mapping for Jellyfin Live TV views.
- Richer EPG mapping (for example repeat/premiere/live hints and original air date).
- New timer creation now returns stable backend IDs where available.

### Comparison at a Glance

The official Jellyfin TVHeadend plugin and this plugin are both valid choices. They differ mainly in
integration style, setup workflow, and operational focus.

| Topic | This plugin | Official Jellyfin TVHeadend plugin |
|---|---|---|
| Integration model | HTTP/JSON API-only integration | HTSP-oriented integration with HTTP support where needed |
| Setup workflow | Guided setup with diagnostics, profile creation, token generation, and profile dropdowns | Simpler configuration focused on core connection settings |
| Playback tuning | Focus on direct play, profile strategy, and probe-cache workflow for predictable startup behavior | More traditional live TV flow with fewer plugin-side playback tuning controls |
| Diagnostics and visibility | Built-in compatibility checks, recommendations, and admin-facing feedback | Minimal built-in diagnostic tooling in the plugin UI |
| Metadata handling | Focus on active channels, channel tags/groups, and richer EPG mapping hints | Core Live TV integration with a more minimal configuration surface |

Choose the plugin that best fits your deployment goals, client mix, and preferred TVHeadend integration style.

### Typical Real-World Result

With the recommended setup (`jellyfin` profile, direct play allowed, probing enabled, cache pre-creation enabled), many users can reach channel switching times around 2 seconds or below after warm-up, often even on the first tune.

Fast channel switching depends primarily on one condition: **Direct Play must be used**.

When Direct Play is active, the generated TVHeadend stream URL is passed through to the client and the client opens a direct connection to TVHeadend. This bypasses Jellyfin's stream processing path for the actual media flow.

For non-Direct-Play paths (Direct Stream or Transcoding), Jellyfin core introduces hard-coded live TV analysis/wait behavior. In practice this means channel switches are typically **not below about 6 seconds**, and can be higher depending on FFmpeg startup overhead.

## Table of Contents

- [Comparison at a Glance](#comparison-at-a-glance)
- [Quick Start (10 Minutes)](#quick-start-10-minutes)
- [Requirements](#requirements)
- [Compatibility Matrix](#compatibility-matrix)
- [How Playback Modes Work](#how-playback-modes-work)
- [Recommended TVHeadend Configuration](#recommended-tvheadend-configuration)
- [Recommended Plugin Configuration](#recommended-plugin-configuration)
- [Performance Tuning for Fast Channel Switching](#performance-tuning-for-fast-channel-switching)
- [Benchmarking and Performance Baselines](#benchmarking-and-performance-baselines)
- [Current Limitations and Improvement Areas](#current-limitations-and-improvement-areas)
- [Troubleshooting](#troubleshooting)
- [FAQ](#faq)
- [For Copilot and AI Agents](#for-copilot-and-ai-agents)
- [Developer Notes](#developer-notes)
- [License](#license)

## Quick Start (10 Minutes)

1. Install the plugin in Jellyfin from this repo manifest:

   ```text
   https://raw.githubusercontent.com/john-pierre/jellyfin-plugin-tvheadend-api/main/manifest.json
   ```

2. Open plugin settings: `Dashboard -> Plugins -> My Plugins -> TVHeadend API`.

3. Fill connection/auth fields:
   - `Host`, `Port`, optional `Webroot`.
   - `Username` and `Password`.
   - Generate and save an `Auth Token` (button in auth section).

4. Click `Create "jellyfin" Profile` in the plugin page.

5. Set `Streaming Profile` to `jellyfin`.

6. Enable:
   - `Supports Direct Play`
   - `Supports Direct Stream`
   - `Supports Probing`
   - `Enable MediaInfo Cache Write`

7. Save config, run `Diagnose`, then open Live TV channels.

8. Verify playback mode in Jellyfin's playback info overlay:
   - The mode should be `Direct Play`.
   - If mode is `Direct Stream` or `Transcoding`, fast switching is not expected and channel changes will usually be significantly slower.

## Requirements

- Jellyfin `10.10.3+`
- TVHeadend `4.3+`
- A TVHeadend user with permissions for:
  - API access
  - Channel/EPG read
  - Stream access
  - DVR access (optional)

## Compatibility Matrix

The matrix below summarizes the deployment patterns currently targeted by this plugin.

| Scenario | Status | Notes |
|---|---|---|
| Jellyfin `10.10.x` + TVHeadend `4.3+` + `jellyfin` streaming profile | **Recommended** | Primary optimization path for direct play, cache pre-creation, and predictable startup behavior |
| Jellyfin `10.10.x` + TVHeadend `4.3+` + `pass` streaming profile | **Supported** | Works, but stream/container behavior can vary more by channel and source |
| Clients can reach TVHeadend directly | **Recommended** | Best path for direct play and lowest channel-switch time |
| Clients reach Jellyfin but not TVHeadend directly | **Conditional** | Playback can still work, but remux/transcode paths are more likely and startup is usually slower |
| Reverse proxy or TVHeadend sub-path (`Webroot`) | **Supported** | Requires correct `Webroot`, reachable stream/image URLs, and working auth token propagation |
| HTTPS with trusted certificates | **Supported** | Recommended for production deployments |
| HTTPS with self-signed certificates | **Conditional** | Supported when `Ignore Certificate Errors` is enabled, but not recommended for production |
| Missing or non-alphanumeric auth token | **Not recommended** | Common cause of stream/image auth failures and inconsistent direct-access behavior |
| TVHeadend admin credentials for UI helpers | **Optional** | Only required for profile creation and auth-token generation from the plugin UI |

This matrix is intended as operational guidance. Actual playback results still depend on client capabilities, Jellyfin playback policy, network reachability, and TVHeadend profile design.

## How Playback Modes Work

Jellyfin can use three playback paths.

| Mode | What it means | Server load | Startup speed | Best use case |
|---|---|---:|---:|---|
| `Direct Play` | Client plays stream as-is from the provided TVHeadend URL (direct client <-> TVHeadend path) | Lowest | Fastest | **Default target mode for live TV** |
| `Direct Stream` (Remux) | Jellyfin stays in the media path and remuxes container without full re-encode | Low-Medium | Slow for channel switching compared to Direct Play | Compatibility fallback |
| `Transcoding` | Jellyfin stays in the media path and fully converts audio/video | Highest | Slowest | Last-resort compatibility fallback |

### Why This Plugin Can Be Fast

The plugin improves startup by combining:

1. A predictable TVHeadend stream profile (`jellyfin` transcode profile).
2. Probe-friendly metadata behavior in Jellyfin.
3. Optional pre-created MediaInfo cache entries.

This combination helps Jellyfin stay on direct play/direct stream paths more often.

### Direct Play Is The Critical Requirement

For fast zapping, this plugin should be configured so that Direct Play is used whenever technically possible.

- Direct Play: TVHeadend URL is forwarded to the client, and the client connects directly to TVHeadend.
- Non-Direct-Play: Jellyfin processing path is used, and hard-coded live TV wait/probe timings in Jellyfin core apply.

As a practical rule: **anything that is not Direct Play usually implies at least about 6 seconds channel-switch time** in Jellyfin live TV flows.

## Recommended TVHeadend Configuration

Use these settings as baseline.

### 1) User and Access

Create or use a dedicated TVHeadend user for Jellyfin with:

- Channel and EPG read access.
- Streaming permission.
- DVR permission (if recordings are needed).
- Admin permission only if you want token/profile generation from plugin UI.

### 2) Stream Profile Strategy

Recommended: use plugin-created `jellyfin` profile.

- Click `Create "jellyfin" Profile` in plugin UI.
- This creates codec and stream profiles designed for compatibility and fast startup.

Alternative: `pass` profile.

- Use if you need untouched source transport.
- Expect more variability in codec/container behavior per channel.

### 3) Auth Token

Generate token in plugin UI or TVHeadend admin UI.

Important token format in this plugin:

- Allowed: letters and numbers only (`A-Z`, `a-z`, `0-9`).
- The token is appended as `auth=...` to stream/image URLs.

## Recommended Plugin Configuration

### Connection

| Setting | Recommended value |
|---|---|
| `Host` | TVHeadend host/IP |
| `Port` | Usually `9981` |
| `Use SSL` | On only if TVHeadend serves HTTPS |
| `Ignore Certificate Errors` | Off in production |
| `Webroot` | `/` unless reverse proxy subpath is used |

### Authentication

| Setting | Recommended value |
|---|---|
| `Allow Anonymous Access` | Off |
| `Username` | TVHeadend user |
| `Password` | TVHeadend password |
| `Auth Token` | Required alphanumeric token |

### Streaming

| Setting | Recommended value | Why |
|---|---|---|
| `Streaming Profile` | `jellyfin` | Predictable output |
| `Supports Direct Play` | On | Fastest path |
| `Supports Direct Stream` | On | Useful fallback |
| `Supports Transcoding` | On or Off by your policy | Compatibility fallback |
| `Supports Probing` | On | Better stream decision quality |
| `Enable MediaInfo Cache Write` | On (with `jellyfin` profile) | Faster startup |
| `FallbackMaxStreamingBitrate` | Keep default first | Tune only if needed |
| `AnalyzeDurationMs` | Keep default first | Tune later based on diagnostics |
| `BufferMs` | `0` initially | Add only if unstable network |

### Recording (Optional)

Enable DVR fields only if you use TVHeadend-managed recording profiles.

## Performance Tuning for Fast Channel Switching

If your goal is sub-2s switching, use this checklist:

- Use `jellyfin` TVHeadend stream profile.
- Keep `Direct Play` and `Direct Stream` enabled.
- Keep probing enabled for reliable stream decisions.
- Keep media info cache writing enabled.
- Run channels once to warm cache.
- Avoid unnecessary transcoding policies on clients.
- Check diagnostics after every major config change.

### Practical Expectation

- Direct Play is the only mode that consistently enables very fast channel switching.
- For non-Direct-Play (Direct Stream/Transcoding), expect a minimum around 6s in practice due to Jellyfin core live TV wait/analysis behavior.
- Once cached, repeated Direct Play channel opens are usually much faster.
- If every channel still takes long, check profile mismatches and auth issues first.

## Benchmarking and Performance Baselines

If you want reproducible performance data for your setup, measure channel-open time by scenario instead of relying on a single average number.

### Recommended Benchmark Method

1. Use a stable test channel with known good reception.
2. Benchmark each scenario at least 5 times.
3. Record the elapsed time from channel open request to first visible playback.
4. Note the actual playback mode reported by Jellyfin (`Direct Play`, `Direct Stream`, or `Transcode`).
5. Separate first-tune results from warm-cache results.
6. Repeat after any change to TVHeadend profile, auth token, reverse proxy, or client device.

### Suggested Benchmark Scenarios

| Scenario | Recommended setup | What to record |
|---|---|---|
| First tune, recommended fast path | `jellyfin` profile + `Direct Play` enabled + probing enabled + cache pre-creation enabled | Time to first frame, playback mode, whether cache file was created beforehand |
| Warm tune, recommended fast path | Same as above, after at least one previous successful tune | Time to first frame, playback mode |
| Variable source profile | `pass` profile with same client/device | Time to first frame, playback mode, detected fallback behavior |
| No direct TVHeadend reachability | Client forced through Jellyfin path | Time to first frame, playback mode, whether remux/transcode was selected |
| Compatibility fallback | Client/profile combination that cannot direct play | Time to first frame, playback mode, any transcoding trigger observed |

### Interpreting Results

| Result pattern | Interpretation |
|---|---|
| Fast first tune and fast warm tune with `Direct Play` | The recommended profile/cache strategy is working as intended |
| Warm tune is fast but first tune is slow | Cache pre-creation may be disabled, mismatched, or not aligned with the selected TVHeadend profile |
| All scenarios are slow and playback is not `Direct Play` | Investigate client codec support, playback policy, and network topology before tuning FFmpeg-related settings |
| Streams are fast in browser tests but slow in Jellyfin clients | The client is likely forcing remux/transcode or cannot reach TVHeadend directly |
| Results changed after profile or auth changes | Re-run diagnostics and confirm the cache file matches the active TVHeadend streaming profile |

These benchmark baselines are intended to make performance comparisons reproducible across channels, clients, and configuration changes.

## Current Limitations and Improvement Areas

This plugin is production-oriented, but some behaviors still depend on Jellyfin core, client capabilities, network topology, and TVHeadend configuration.

- Very fast channel switching generally depends on `Direct Play`. Non-direct-play paths remain slower because Jellyfin core applies additional live TV analysis and startup behavior.
- Playback results still vary by client/device profile, codec support, and Jellyfin user playback policy. A configuration that direct plays on one client may still remux or transcode on another.
- Best startup behavior usually requires clients to reach TVHeadend directly. Reverse proxies, SSL termination, DNS issues, or restrictive network layouts can increase startup time or break direct access.
- Authentication remains sensitive to correct TVHeadend permissions and a valid alphanumeric auth token for stable stream and image access.
- Stream behavior can still vary across TVHeadend profiles, channel types, and source formats, especially outside the recommended `jellyfin` profile.
- Diagnostics improve visibility into slow or incompatible setups, but they cannot override Jellyfin core stream-selection logic or client-side playback decisions.
- Ongoing improvement areas include broader compatibility coverage, clearer client-specific guidance, stronger validation of cache/probe assumptions, and deeper diagnostics for slow-path causes.

## Troubleshooting

### Symptom: Channel switch is slow

Check:

1. Is `Streaming Profile` set to `jellyfin`?
2. Is direct play allowed by client policy?
3. Is probing enabled and cache write enabled?
4. Is the selected client forcing transcoding?
5. Does diagnostics show profile mismatch/warnings?
6. If playback mode is not Direct Play, treat >=6s switching as expected behavior in Jellyfin core live TV path.

### Symptom: Streams fail or 401

Check:

1. TVHeadend username/password correctness.
2. Auth token exists and is alphanumeric.
3. Token has not been rotated without updating plugin config.
4. TVHeadend user rights include stream access.

### Symptom: Missing channel logos or EPG images

Check:

1. TVHeadend image endpoints are reachable from Jellyfin host.
2. Auth token is present and valid.
3. Browser network inspector shows image URL contains `auth=`.

### Symptom: Works in browser but not in client

Check:

1. Client playback capabilities (codec/container support).
2. Jellyfin user playback policy.
3. Whether client is forcing transcode due to profile mismatch.

## FAQ

### Do I need HTSP?

No. This plugin is API-only via HTTP/JSON.

### Should I use `pass` or `jellyfin` profile?

- Use `jellyfin` for best compatibility and startup speed.
- Use `pass` only if you need untouched source stream behavior.

### Is auth token optional?

In practical setups, treat it as required for stable stream/image auth behavior.

When using the plugin's token generation endpoint, the plugin now retries token create/refresh automatically until it gets an alphanumeric token (A-Z, a-z, 0-9) that is safe for FFmpeg URL usage.

## For Copilot and AI Agents

If you are an AI coding assistant working in this repository:

- Read `AGENTS.md` first.
- Then read `.github/copilot-instructions.md`.
- When validating Jellyfin core behavior, also reference the sibling core repository `../jellyfin` if it is present in your workspace.
- Keep docs and comments in English.
- Prefer minimal, focused diffs.
- Preserve plugin behavior unless change is requested and documented.
- Use portable paths only (`Jellyfin.Plugin.TvHeadendApi/...`, `docs/...`, `../jellyfin/...`); do not use machine-specific absolute paths.

Primary architecture files:

- `Jellyfin.Plugin.TvHeadendApi/Service/OrchestratorService.cs`
- `Jellyfin.Plugin.TvHeadendApi/Configuration/PluginConfiguration.cs`
- `Jellyfin.Plugin.TvHeadendApi/Api/TvHeadendApiController.cs`
- `Jellyfin.Plugin.TvHeadendApi/Service/Guide/GuideService.cs`

## Developer Notes

If you want to build from source:

```bash
dotnet restore Jellyfin.Plugin.TvHeadendApi.sln
dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release --no-restore
```

For cross-repo debugging and behavior checks, keep the Jellyfin core repository as a sibling folder (for example `../jellyfin`) and add it to your IDE workspace.

In issues, docs, and review notes, prefer repository-relative paths so instructions remain portable for all contributors.

For full contributor flow, see `CONTRIBUTING.md` and docs under `docs/ai/`.

## License

This project is licensed under the GNU General Public License v3.0 (`GPL-3.0`). See `LICENSE`.
