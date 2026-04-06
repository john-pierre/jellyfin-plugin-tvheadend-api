# AI Agent Guide

This file is the canonical AI-agent context for this repository.

## Mission

Maintain and improve the Jellyfin TVHeadend API plugin while preserving:

- Jellyfin LiveTV behavior and compatibility.
- TVHeadend API-only integration (no HTSP).
- Stable plugin configuration semantics.
- Safe, reproducible local testing.

## Architecture Quick Map

- `Jellyfin.Plugin.TvHeadendApi/Plugin.cs`
  - Plugin entrypoint and metadata.
- `Jellyfin.Plugin.TvHeadendApi/Service/LiveTvService.cs`
  - Core `ILiveTvService` implementation and stream/channel integration logic.
- `Jellyfin.Plugin.TvHeadendApi/Configuration/PluginConfiguration.cs`
  - Runtime configuration model used by API and UI.
- `Jellyfin.Plugin.TvHeadendApi/Configuration/ConfigPage.html`
  - Plugin settings page rendered in Jellyfin admin.
- `Jellyfin.Plugin.TvHeadendApi/Api/TvHeadendApiController.cs`
  - Diagnostic and helper endpoints used by tooling.
- `scripts/analyze-tvh.ps1`
  - End-to-end analyzer for PlaybackInfo/stream open metrics and report generation.

## Ground Rules For Changes

- Keep docs, code comments, and commit messages in English.
- Do not alter plugin behavior silently; document user-visible effects.
- Prefer minimal, focused diffs over broad refactors.
- Keep generated artifacts out of git (`reports/` is ignored except marker files).

## Testing Workflow

1. Build plugin:
   - `dotnet restore Jellyfin.Plugin.TvHeadendApi.sln`
   - `dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release --no-restore`
2. (Optional) Start dev Jellyfin:
   - `docker compose up -d --build`
3. Run analyzer smoke test:
   - `powershell -ExecutionPolicy Bypass -File .\scripts\analyze-tvh.ps1 -MaxChannels 1 -Scenarios current -StreamingProfiles pass -SkipBuild`
4. Validate outputs:
   - Report in `reports/report_<timestamp>.md`
   - Artifacts in `reports/artifacts_<timestamp>/...`

## When Editing `scripts/analyze-tvh.ps1`

- Keep pre-run plan output consistent with report `## Test Plan` section.
- Keep artifact naming deterministic and media-friendly (`stream_capture.<ext>`).
- Ensure report paths default under `reports/`.

## PR Checklist For Agents

- Build succeeds locally.
- Any changed behavior is documented in `README.md` or `CHANGELOG.md`.
- Script/report outputs and paths are still coherent.
- No secrets or local machine data added.

