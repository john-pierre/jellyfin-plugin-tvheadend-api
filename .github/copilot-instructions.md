# jellyfin-plugin-tvheadend-api — Copilot Instructions

**jellyfin-plugin-tvheadend-api** is a Jellyfin Live TV plugin that integrates TVHeadend via HTTP/JSON API only (no HTSP).
Single-repo: **C# / .NET 8.0** plugin with xUnit tests, Docker dev environment, and GitHub Actions CI.

All agents follow the behavioral principles in [AGENTS.md](AGENTS.md).

---

## GIT Conventions

- **Branches:** `feature/<description>` or `fix/<description>`
- **PR title:** `feat(<scope>): <description>` or `fix(<scope>): <description>` (max 50 chars, Conventional Commits)
- **PR description:** implementation plan as raw Markdown
- **PRs:** create as Draft
- ⛔ **Never commit** without explicit user approval — present staged changes + proposed message, then wait
- ⛔ **Never push** without asking: *"Shall I push the committed changes to the remote branch?"*

---

## Key Rules

- `agent-results/` is git-ignored — local-only artifacts, never commit
- All code, comments, variables, documentation: **English**
- Commit messages, PR descriptions, PR titles: **English**, Conventional Commits format
- Domain language glossary: `.github/instructions/business/domain-language.md` — use these terms everywhere
- Every module needs a `business-description.md` — use `documentation-tvh` agent to create/update
- Business workflow docs: `.github/instructions/business/` (indexed by `README.md`)
- Keep GitHub Actions `uses:` references tag-based; do not auto-convert to commit SHAs
- Use portable relative paths only (`Jellyfin.Plugin.TvHeadendApi/...`, `docs/...`); no absolute paths
- When plugin behavior depends on Jellyfin internals, also validate against `../jellyfin` if available

---

## Validation Commands

```bash
dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release
dotnet test Jellyfin.Plugin.TvHeadendApi.Tests/Jellyfin.Plugin.TvHeadendApi.Tests.csproj -c Release --no-build --filter "Category!=LiveIntegration"
```

---

## Architecture References

- `docs/architecture/architecture-overview.md` — layers, dependency direction
- `docs/architecture/architecture-decisions.md` — ADRs
- `docs/architecture/module-responsibilities.md` — per-module ownership
- `docs/guides/naming-conventions.md` — naming rules
- `docs/guides/test-strategy.md` — test types, coverage
- `docs/guides/observability.md` — logging, metrics
- `docs/ROADMAP.md` — milestones and technical debt

---

## External Systems

- **Issues:** GitHub Issues — `https://github.com/john-pierre/jellyfin-plugin-tvheadend-api/issues`
- **Releases:** Release Please (simple type, squash merge)
