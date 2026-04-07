# AI Skills for This Plugin

Use these skill playbooks when implementing changes.

## Skill: LiveTV Pipeline Safety

Goal: keep Jellyfin LiveTV behavior stable while changing plugin internals.

- Start from `LiveTvService.cs` and trace request/response boundaries.
- Preserve `ILiveTvService` contract semantics.
- Prefer additive flags/config over breaking behavior changes.

## Skill: TVHeadend API Integration

Goal: keep integration API-only (no HTSP assumptions).

- Confirm requests target TVHeadend HTTP/JSON APIs.
- Handle auth and URL composition consistently with existing config.
- Keep model mapping robust against missing/optional fields.

## Skill: Configuration Compatibility

Goal: avoid silent config regressions.

- Check `PluginConfiguration.cs` and `ConfigPage.html` together.
- Preserve defaults unless explicitly changed and documented.
- Document any UI/behavioral impact in `README.md` or `CHANGELOG.md`.

## Skill: Validation Discipline

Goal: produce reproducible, review-ready changes.

- Build solution in Release mode.
- Parse-check changed PowerShell scripts.
- Run a minimal smoke test for the changed script behavior.

