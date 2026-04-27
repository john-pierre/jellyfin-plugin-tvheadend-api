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
| Coding standards | `.docs/ai/coding-standards.md` |
| Testing standards | `.docs/ai/testing-standards.md` |
| Performance rules | `.docs/ai/performance-rules.md` |
| Security rules | `.docs/ai/security-rules.md` |
| Plugin domain knowledge | `.docs/ai/plugin-domain.md` |
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

Reusable skill docs and prompts are available in:

- `.ai/skills/` — Domain-specific knowledge (database, relay, tvheadend, dashboard, logging, token-security, streaming-profile)
- `.ai/prompts/` — Workflow prompts (refactor, bugfix, feature, test-generation, review, release)
- `.ai/agents/` — Agent role definitions (architect, reviewer, performance, security, test-engineer, release-manager)
