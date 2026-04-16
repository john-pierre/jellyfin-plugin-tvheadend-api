# GitHub Copilot Instructions

Use this file together with `AGENTS.md`.

## Routing: Where To Start

| Task type | Start here |
|---|---|
| Architecture questions | `docs/architecture-overview.md`, `docs/architecture-decisions.md` |
| Module boundary questions | `docs/module-responsibilities.md` |
| Naming/structure questions | `docs/NAMING_CONVENTIONS.md` |
| Testing questions | `docs/test-strategy.md` |
| Technical debt / assessment | `docs/assessment.md` |
| Agent workflow | `AGENTS.md`, `docs/ai/INSTRUCTIONS.md` |

## Focus Areas

- Keep the plugin API-driven against TVHeadend HTTP/JSON endpoints.
- Preserve backward-compatible configuration defaults where possible.
- Prefer explicit null-safe handling in C# (`Nullable` is enabled).

## Important Paths

- `Jellyfin.Plugin.TvHeadendApi/Service/OrchestratorService.cs`
- `Jellyfin.Plugin.TvHeadendApi/Configuration/PluginConfiguration.cs`
- `Jellyfin.Plugin.TvHeadendApi/Api/PluginController.cs`
- `README.md`

## Cross-Repo and Path Rules

- When plugin questions depend on Jellyfin core behavior, also reference `../jellyfin` if available in the workspace.
- Use portable relative paths only (`Jellyfin.Plugin.TvHeadendApi/...`, `docs/...`, `../jellyfin/...`); do not use machine-specific absolute paths.

## Code Change Expectations

- Keep changes small and scoped.
- Add comments only where logic is non-obvious.
- Update docs if user-facing behavior, script output, or workflow changes.
- Every meaningful change must include tests (see `docs/test-strategy.md`).
- Architecture changes require updating `docs/architecture-overview.md` or `docs/architecture-decisions.md`.

## When To Add a New Service vs Extend an Existing One

- Add a new service when the concern is a distinct domain (e.g., a new TVHeadend API area).
- Extend an existing service when adding a method within the same domain boundary.
- Check `docs/module-responsibilities.md` before deciding.

## Validation Expectations

For code and script changes, run at least:

1. `dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release`
2. `dotnet test Jellyfin.Plugin.TvHeadendApi.Tests/Jellyfin.Plugin.TvHeadendApi.Tests.csproj -c Release --no-build`
3. PowerShell parser check for changed scripts.
