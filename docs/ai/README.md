# AI Docs Index

This folder provides implementation context for AI coding agents.

## Reading Order

1. `AGENTS.md` (repository canonical context)
2. `.github/copilot-instructions.md` (Copilot behavior constraints)
3. `docs/ai/SKILLS.md` (domain skills and playbooks)
4. `docs/ai/INSTRUCTIONS.md` (execution flow and validation)

## Scope

These docs describe:

- plugin architecture and change boundaries
- expected testing workflow
- analyzer/report output expectations

When a plugin question depends on Jellyfin internals, also inspect the sibling core repository at `../jellyfin` (if available in the workspace).

Path style rule for all agent output and docs: use portable relative paths only (for example `Jellyfin.Plugin.TvHeadendApi/...` or `../jellyfin/...`), never local absolute paths.

Use this index when onboarding a new agent session.

## Naming Rules (Required)

Before creating or renaming files, follow `docs/guides/naming-conventions.md`.

Short version:

- Keep TVHeadend-specific helper adapters under `Jellyfin.Plugin.TvHeadendApi/Service/Helper/`.
- Do not reintroduce a generic `Utility/` folder.
- Match C# file names and primary type names exactly.
- Prefer descriptive service/helper names without source-system prefixes when the folder namespace already provides context; reserve `TvhApi*` for API DTO models.

## Commit Messages and Changelog (Required)

- **Do NOT edit `CHANGELOG.md` manually.** The changelog is generated exclusively by Release Please in the CI pipeline.
- All commit messages **must** follow [Conventional Commits](https://www.conventionalcommits.org/) format:
  - `feat: <message>` — new feature (appears under **Features**)
  - `fix: <message>` — bug fix (appears under **Fixes**)
  - `docs: <message>` — documentation change (appears under **Documentation**)
  - `refactor: <message>` — code refactoring (appears under **Refactoring**)
  - `test: <message>` — test additions or changes (appears under **Tests**)
  - `ci: <message>` — CI/CD pipeline changes (appears under **CI/CD**)
  - `build: <message>` — build system changes (appears under **Build System**)
  - `perf: <message>` — performance improvement (appears under **Performance Improvements**)
- Use a scope when helpful: `feat(guide): add EPG grid pagination`
- Breaking changes: add `!` after type or include `BREAKING CHANGE:` in the footer.
