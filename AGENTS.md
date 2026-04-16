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
- `Jellyfin.Plugin.TvHeadendApi/Service/OrchestratorService.cs`
  - Core `ILiveTvService` implementation and stream/channel integration logic.
- `Jellyfin.Plugin.TvHeadendApi/Configuration/PluginConfiguration.cs`
  - Runtime configuration model used by API and UI.
- `Jellyfin.Plugin.TvHeadendApi/Configuration/ConfigPage.html`
  - Plugin settings page rendered in Jellyfin admin.
- `Jellyfin.Plugin.TvHeadendApi/Api/PluginController.cs`
  - Diagnostic and helper endpoints used by tooling.

## Ground Rules For Changes

- Keep docs, code comments, and commit messages in English.
- Do not alter plugin behavior silently; document user-visible effects.
- Prefer minimal, focused diffs over broad refactors.
- Keep generated artifacts out of git (`reports/` is ignored except marker files).
- Keep GitHub Actions `uses:` references tag-based by default; do not auto-convert tags to commit SHAs unless explicitly requested.
- When plugin behavior depends on Jellyfin internals, also validate against the sibling core repository `../jellyfin` when available.
- Use portable relative paths in docs, issues, reviews, and agent output (`Jellyfin.Plugin.TvHeadendApi/...`, `docs/...`, `../jellyfin/...`); avoid local absolute paths.

## Architecture Documentation

- `docs/architecture-overview.md` — layers, dependency direction, module map.
- `docs/architecture-decisions.md` — ADR-style records for significant design decisions.
- `docs/module-responsibilities.md` — per-module ownership and boundary rules.
- `docs/test-strategy.md` — test types, naming, coverage expectations.
- `docs/assessment.md` — repository assessment and technical debt inventory.

## Naming And Layout Rules

- Follow `docs/NAMING_CONVENTIONS.md` as the **authoritative** source of truth for all naming and structure conventions.
- Keep TVHeadend helper adapters in `Jellyfin.Plugin.TvHeadendApi/Service/Helper/`.
- Do not add new files back into a generic `Utility/` folder.
- Keep C# file name == primary type name (e.g., `TokenValidator.cs` contains `TokenValidator` class).
- Prefer descriptive service names without source-system prefixes when the folder namespace already provides context.

## Testing Workflow

1. Build plugin:
   - `dotnet restore Jellyfin.Plugin.TvHeadendApi.sln`
   - `dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release --no-restore`
2. (Optional) Start dev Jellyfin:
   - `docker compose up -d --build`
3. (Optional) Run a quick live playback smoke test from a client.

## When Editing `scripts/*.ps1`

- Keep script output stable and deterministic where possible.
- Prefer explicit parameters over hidden defaults for reproducible runs.

## PR Checklist For Agents

- Build succeeds locally.
- Any changed behavior is documented in `README.md` or `CHANGELOG.md`.
- Script behavior and output expectations are still coherent.
- No secrets or local machine data added.

