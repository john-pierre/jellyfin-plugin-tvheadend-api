# jellyfin-plugin-tvheadend-api — OpenCode Instructions

**jellyfin-plugin-tvheadend-api** is a Jellyfin Live TV plugin that integrates TVHeadend via HTTP/JSON API only (no HTSP).
Single-repo: **C# / .NET 8.0** plugin with xUnit tests, Docker dev environment, and GitHub Actions CI.

All agents follow the behavioral principles in `.github/AGENTS.md`.

---

## Key Documentation

| Document | Location |
|----------|----------|
| Agent behavior principles | `.github/AGENTS.md` |
| Architecture overview | `docs/architecture/overview.md` |
| Architecture decisions | `docs/architecture/decisions.md` |
| Module responsibilities | `docs/architecture/module-responsibilities.md` |
| Naming conventions | `docs/guides/naming-conventions.md` |
| Test strategy | `docs/guides/test-strategy.md` |
| Observability | `docs/guides/observability.md` |
| Domain language | `.github/instructions/business/domain-language.md` |
| Coding standards | `.github/instructions/code-conventions/dev-team-dotnet.instructions.md` |
| Testing standards | `docs/guides/test-strategy.md` |
| Performance rules | `docs/guides/performance.md` |
| Security rules | `docs/guides/security.md` |
| Plugin domain knowledge | `.github/instructions/business/domain-language.md` |
| Roadmap | `docs/ROADMAP.md` |

---

## Build and Test

```bash
dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release
dotnet test Jellyfin.Plugin.TvHeadendApi.Tests/Jellyfin.Plugin.TvHeadendApi.Tests.csproj -c Release --no-build --filter "Category!=LiveIntegration"
```

---

## GIT Conventions

- **Branches:** `feature/<description>` or `fix/<description>`
- **PR title:** `feat(<scope>): <description>` or `fix(<scope>): <description>` (Conventional Commits)
- Never commit without explicit user approval
- Never push without asking first

---

## Key Rules

- `agent-results/` is git-ignored — local-only artifacts, never commit
- All code, comments, variables, documentation: **English**
- Domain language glossary: `.github/instructions/business/domain-language.md`
- Every service module needs a `business-description.md`
- Use portable relative paths only; no absolute paths
- Keep GitHub Actions `uses:` tag-based; no commit SHAs

---

## Skills and Prompts

Reusable skills and prompts live under `.github/`:

- `.github/skills/` — ci-cd, debugging, documentation, dotnet, implementation-cycle,
  implementation-plan, pr-review, refactoring
- `.github/agents/` — agent role definitions
- `.github/prompts/` — task prompts

Domain knowledge lives with the code: `docs/architecture/**`, `docs/guides/**`, and the per-module
`business-description.md` next to each service.
