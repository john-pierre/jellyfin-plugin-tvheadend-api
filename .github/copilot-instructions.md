# GitHub Copilot Instructions

Use this file together with `AGENTS.md`.

## Focus Areas

- Keep the plugin API-driven against TVHeadend HTTP/JSON endpoints.
- Preserve backward-compatible configuration defaults where possible.
- Prefer explicit null-safe handling in C# (`Nullable` is enabled).

## Important Paths

- `Jellyfin.Plugin.TvHeadendApi/Service/LiveTvService.cs`
- `Jellyfin.Plugin.TvHeadendApi/Configuration/PluginConfiguration.cs`
- `Jellyfin.Plugin.TvHeadendApi/Api/TvHeadendApiController.cs`
- `README.md`

## Code Change Expectations

- Keep changes small and scoped.
- Add comments only where logic is non-obvious.
- Update docs if user-facing behavior, script output, or workflow changes.

## Validation Expectations

For code and script changes, run at least:

1. `dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release`
2. PowerShell parser check for changed scripts.
3. Run the changed script once with minimal safe parameters when behavior changes.

