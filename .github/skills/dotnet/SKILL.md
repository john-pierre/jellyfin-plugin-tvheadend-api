---
name: dotnet
description: "All C#/.NET work in jellyfin-plugin-tvheadend-api: coding, testing, building, reviewing, refactoring."
---

# .NET Skill — jellyfin-plugin-tvheadend-api

---

## 1 — Core Principles

- **Follow the plan**: When there is an implementation plan, follow it. Use the [Implementation Plan Skill](/.github/skills/implementation-plan/SKILL.md).
- **Enforce conventions**: Adhere to the coding conventions defined in this skill and in [dev-team-dotnet.instructions.md](/.github/instructions/code-conventions/dev-team-dotnet.instructions.md).
- **Domain language compliance**: Use domain terms from [domain-language.md](/.github/instructions/business/domain-language.md) for all identifiers. Alert the user before introducing new terms.

---

## 2 — Prerequisites & Build Commands

### Prerequisites

| Tool | Required Version | Notes |
|------|-----------------|-------|
| **.NET SDK** | **8.0** | TreatWarningsAsErrors is on — zero warnings allowed |
| **Docker** | latest | Optional, for dev environment |

### Build Commands

```bash
# Restore
dotnet restore Jellyfin.Plugin.TvHeadendApi.sln

# Build (Release)
dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release --no-restore

# Test (unit only, excludes live integration)
dotnet test Jellyfin.Plugin.TvHeadendApi.Tests/Jellyfin.Plugin.TvHeadendApi.Tests.csproj -c Release --no-build --filter "Category!=LiveIntegration"

# Test (live integration — requires docker-compose.test.yaml running)
dotnet test --filter "Category=LiveIntegration"

# Full CI-style
dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release && dotnet test Jellyfin.Plugin.TvHeadendApi.Tests/Jellyfin.Plugin.TvHeadendApi.Tests.csproj -c Release --no-build --filter "Category!=LiveIntegration"
```

### Key Build Rules

- Build must succeed with **zero warnings and zero errors**.
- StyleCop + Roslyn analyzers enforce style automatically via `Jellyfin.ruleset`.
- Coverage: 80% minimum (CI-enforced), 90% target.

---

## 3 — Coding Conventions

All code must comply with the following convention documents. Read and internalize them **before** writing or reviewing any code:

1. **Dev Team .NET Conventions**: [dev-team-dotnet.instructions.md](/.github/instructions/code-conventions/dev-team-dotnet.instructions.md)
2. **Naming Conventions**: `docs/guides/naming-conventions.md`
3. **Architecture Overview**: `docs/architecture/overview.md`
4. **Module Responsibilities**: `docs/architecture/module-responsibilities.md`

---

## 4 — Development Iteration Cycle

Follow the [Implementation Cycle Skill](/.github/skills/implementation-cycle/SKILL.md) for the mandatory process, strict rules, step sequence table, and iteration-end gate.

The following .NET-specific additions extend each step:

### Step 1 — .NET-specific additions
- Preferred granularity: one service method or one class per step.
- Always forward `CancellationToken` to async calls.
- Use `ILogger<T>` with static message templates (no interpolation).
- Register new services as singletons in `ServiceRegistrator.cs`.

### Step 2 — .NET-specific additions
- Test framework: **xUnit + FluentAssertions + Moq + AutoFixture**.
- Test naming: `{MethodName}_{Scenario}_{ExpectedResult}`
- Verify command:
  ```bash
  dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release && dotnet test Jellyfin.Plugin.TvHeadendApi.Tests/Jellyfin.Plugin.TvHeadendApi.Tests.csproj -c Release --no-build --filter "Category!=LiveIntegration"
  ```

### Step 3 — .NET-specific additions
- Review against the [Review Checklist (§5)](#5--review-checklist).
- Verify no nullable warnings suppressed without comment.
- Verify `CancellationToken` forwarded everywhere.
- Verify no string interpolation in logger calls.

### Step 4 — .NET-specific additions
- Scope: every service module (folder under `Service/`) that was modified.
- Instruct the documentation agent to re-analyze each module and update its `business-description.md`.

---

## 5 — Review Checklist

When reviewing .NET code:

- **Domain language violations**: identifiers that don't match the glossary.
- **Convention violations**: code that doesn't follow `dev-team-dotnet.instructions.md` or `naming-conventions.md`.
- **Nullable safety**: improper null handling, unnecessary `!` suppressions.
- **CancellationToken**: not forwarded to async calls.
- **Logging**: string interpolation in logger calls (must use static templates).
- **Documentation**: missing XML docs on public members of public types.
- **Test coverage**: production code covered by meaningful tests.
- **Error handling**: exceptions properly caught (specific types), clear error messages.
- **Code quality**: code smells, long methods (>30 lines), duplication, excessive complexity.
- **Performance**: unnecessary allocations, missing `ConfigureAwait(false)`, sync-over-async.
- **Security**: hardcoded secrets, sensitive data in logs.
- **Architecture**: dependency direction violations, business logic in wrong layer.

