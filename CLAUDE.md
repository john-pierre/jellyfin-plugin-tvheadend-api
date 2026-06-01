# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

A Jellyfin Live TV plugin (`C# / .NET 8.0`) that integrates TVHeadend **exclusively over its HTTP/JSON API — no HTSP**. The plugin implements Jellyfin's `ILiveTvService` and exposes admin-only REST endpoints plus two embedded admin pages. Targets Jellyfin `10.10.3+` and TVHeadend `4.3+`.

## Commands

```bash
# Build (must succeed with ZERO warnings — TreatWarningsAsErrors is on)
dotnet restore Jellyfin.Plugin.TvHeadendApi.sln
dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release --no-restore

# Run tests, excluding tests that need a live TVHeadend/Jellyfin backend
dotnet test Jellyfin.Plugin.TvHeadendApi.Tests/Jellyfin.Plugin.TvHeadendApi.Tests.csproj \
  -c Release --no-build --filter "Category!=LiveIntegration"

# Run a single test class or method
dotnet test Jellyfin.Plugin.TvHeadendApi.Tests/Jellyfin.Plugin.TvHeadendApi.Tests.csproj \
  -c Release --filter "FullyQualifiedName~GuideServiceTests"
dotnet test ... --filter "FullyQualifiedName~GuideServiceTests.GetChannels_ReturnsActiveOnly"
```

Tests carry a `[Trait("Category", ...)]` of `Unit`, `JellyfinIntegration`, or `LiveIntegration`. `LiveIntegration` tests require a running backend (provisioned via `docker/docker-compose.test.yaml`) and are excluded from the normal build verification filter — always run with `Category!=LiveIntegration` unless explicitly testing against live backends.

### Local Jellyfin dev environment

```powershell
.\docker\build.ps1            # runs unit tests, bumps dev version, builds image, recreates container
.\docker\build.ps1 -DryRun    # preview without executing
```
Loads the plugin into a local Jellyfin at `http://localhost:8096`. This stack does **not** include a TVHeadend backend.

## Architecture

The dependency flow is strictly one-directional: **Controller → Service → Backend → HTTP**. Never reverse it.

- **`Plugin.cs`** — entry point. Registers the two embedded pages (`Configuration/ConfigPage.html` config page, `Page/DashboardPage.html` Live-TV dashboard) and holds the `Plugin.Instance` singleton. Only path/config provider lambdas in `ServiceRegistrator` may touch `Plugin.Instance` — domain services must depend on injected interfaces instead.
- **`ServiceRegistrator.cs`** — all DI wiring. Registers two named `HttpClient`s: `"TvHeadend"` (validates certs) and `"TvHeadendUnsafe"` (skips cert validation for self-signed setups). Both run through `ResilienceHandler`.
- **`OrchestratorService`** (`Service/`) — the `ILiveTvService` implementation. It is a **thin facade: delegation only**. No business logic, HTTP, or mapping belongs here; it dispatches to domain services.
- **`Service/{domain}/`** — domain services own their business logic AND TVHeadend→Jellyfin mapping (mapping is inline in service methods, not a separate layer). Key domains: `Guide` (channels, EPG, tags), `Dvr` (timers, recordings), `Stream` (URL building, media sources, lifecycle, MediaInfo cache), `Auth` (token generation/validation), `Profile` + `StreamingProfile` (profile resolution + hierarchical selection), `Diagnostic`, `Relay` (token-secured stream/image proxy), `Statistic`/`Metrics` (telemetry).
- **`Service/Backend/`** — low-level integration only: `ApiClient` (HTTP), `UrlBuilder`, `GridFetcher` (TVHeadend grid pagination). No domain logic here. Services read live config via `IApiClient.GetCurrentConfiguration()` — config is read-only at runtime.
- **`Service/Resilience/`** — `ResilienceHandler` is a custom `DelegatingHandler` with exponential back-off (3 attempts) + circuit breaker (5 failures → 30s open).
- **`Service/Database/`** — central SQLite infrastructure (EF Core + raw connections). `DatabaseProvider`/`DatabaseConnectionFactory` own the file and connection PRAGMAs; `DatabaseWriteCoordinator` serializes all writes; `DatabaseMigrationService` owns schema/versioning. **DB initialization is deferred** (not done at service registration) because `Plugin.Instance`/`DataFolderPath` is not yet available then — it happens the first time a service resolves `DatabaseHealthService`. SQLite persists viewing statistics, plugin logs, relay tokens, and streaming telemetry.
- **`Model/{domain}/`** — passive DTOs only. No logic, no service dependencies.
- **`Api/`** — REST controllers under `/TvHeadendApi/`, all delegate to services. All endpoints require Jellyfin admin elevation **except `RelayController`**, which is anonymous + token-secured (it proxies streams/images for clients).

Background work runs via `IHostedService` registrations (statistics, comet log buffering, DB cleanup, relay metrics, relay token cleanup).

### Performance model (why this plugin exists)

The whole design optimizes for **Direct Play**: when active, the TVHeadend stream URL is forwarded to the client and Jellyfin's processing path is bypassed, enabling ~2s channel switching. Any non-Direct-Play path (Direct Stream/Transcode) hits hard-coded Jellyfin-core live-TV analysis waits (~6s+ minimum). The `jellyfin` TVHeadend profile + probing + MediaInfo cache pre-creation (`MediaSourceService` writes `cache/mediainfo/*.json`) are the levers that keep playback on the Direct Play path. Don't make changes that push clients off Direct Play without flagging it.

## Conventions

- All code, comments, XML docs, log messages, commits, and docs are **English**.
- Nullable reference types are enabled; handle nulls explicitly and avoid `!` suppressions (comment if unavoidable). StyleCop + analyzers run with `AllEnabledByDefault`; the build fails on any violation.
- **Preserve backward compatibility**: configuration defaults must stay stable across versions.
- Auth tokens passed to TVHeadend stream/image URLs must be **alphanumeric only** (`A-Z a-z 0-9`) — they are appended as `auth=...` and must be FFmpeg-URL-safe.
- Use repo-relative paths everywhere (`Jellyfin.Plugin.TvHeadendApi/...`, `docs/...`); never machine-specific absolute paths.
- `agent-results/` is git-ignored — never commit it.

### Git / PRs

- Branches: `feature/<description>` or `fix/<description>`. PRs target `main`, created as Draft.
- Commit messages and PR titles follow **Conventional Commits** (`feat|fix|perf|revert|docs|style|refactor|test|build|ci`). PR titles are CI-validated (`.github/workflows/pr-title-check.yaml`) and max 50 chars. Maintainers squash-merge; Release Please derives the version from the PR title.
- **Never commit without explicit user approval; never push without asking first.** Keep GitHub Actions `uses:` refs tag-based — do not rewrite to commit SHAs.

## Further Reading

- `docs/architecture/overview.md` — layers, dependency rules, supporting-service table, module do/don't lists
- `docs/architecture/decisions.md` — ADRs; `docs/architecture/module-responsibilities.md` — per-module ownership
- `docs/guides/test-strategy.md` — test types and coverage; `docs/ROADMAP.md` — milestones/tech debt
- `.github/copilot-instructions.md` + `.github/AGENTS.md` — additional agent guidance
- When plugin behavior depends on Jellyfin internals, validate against a sibling `../jellyfin` checkout if available.
