# Roadmap

This document tracks the structured refactor and quality improvement of the Jellyfin TVHeadend API plugin.
Milestones below are a historical log, appended over time — the current state is summarised here so it
does not have to be reconstructed from 650 lines of history.

> Historical entries name files as they were called **at the time**. The documentation was later
> reorganised into `docs/architecture/` and `docs/guides/`, so paths such as
> `docs/architecture-overview.md` or `docs/test-strategy.md` in older milestones no longer resolve.
> They are left as written rather than rewritten, so the log stays an accurate record. Where an
> artefact still exists but moved, the entry says where it is now.

## Current State (2026-08-08)

| | |
|---|---|
| Build | 0 warnings / 0 errors (`TreatWarningsAsErrors`) |
| Unit tests | 1450 passing |
| Live integration tests | 142 passing on Jellyfin 10.10.7, 10.11.11 and 12.0-rc4 |
| Coverage | 86.94% line / 72.74% branch (CI gate `80 90`) |
| Supported Jellyfin | 10.10.7+ (`TARGET_ABI` in the workflow) |

**Still open** — see [Milestone 24e](#24e--known-open-items) for detail:

- The required status check `build-and-release` matches no job in the workflow, so PRs cannot merge
  until the branch-protection context is corrected (repo setting, not code).
- Dashboard analytics is gated behind an external Chart.js CDN fetch.
- The config page auto-saves and runs a full TVHeadend diagnostic on every keystroke.
- Thirteen `PluginConfiguration` properties are not surfaced in the UI.
- `SkipIfUnavailable()` calls `Assert.Fail` instead of skipping.
- Config schema versioning (Milestone 15) and plugin API versioning (Milestone 17) are unstarted.
- `build.yaml` (JPRM manifest) is absent — the plugin cannot be built by the official Jellyfin
  toolchain or submitted to the official plugin repository.

---

## Milestone 0 — Discovery and Baseline

- [x] Full repository inspection
- [x] Architecture inventory (layers, dependencies, integration points)
- [x] Service/model inventory
- [x] Test inventory (230 tests, gap: StatisticsService untested)
- [x] Documentation inventory
- [x] Pipeline inventory
- [x] Risk list and quick wins identified
- [x] `docs/assessment.md` created (consolidated into ROADMAP.md in Milestone 18)

## Milestone 1 — Standards and Governance

- [x] `docs/architecture-overview.md` created
- [x] `docs/architecture-decisions.md` created (7 ADRs)
- [x] `docs/module-responsibilities.md` created
- [x] `docs/test-strategy.md` created
- [x] `docs/NAMING_CONVENTIONS.md` updated to match actual file names
- [x] `.github/copilot-instructions.md` upgraded to routing layer
- [x] `AGENTS.md` updated with architecture documentation section
- [x] Stale file references fixed (`TvHeadendApiController` → `PluginController` in AGENTS.md, README.md, copilot-instructions.md)
- [x] `docs/ai/README.md` stale reference fixed

## Milestone 2 — Structural Refactor

- [x] Extract `UrlHelper` into its own file (`Service/Helper/UrlHelper.cs`) — file == type rule
- [x] Remove `#pragma warning disable CS1591` from `OrchestratorService.cs` — added XML docs to all public members
- [x] Extract shared `JsonSerializerOptions` into `Service/Helper/JsonDefaults.cs` — replaced 7 duplicate instances
- [x] Fix all SA1648 (inheritdoc on non-inherited methods) in `OrchestratorService.cs`
- [x] Fix all SA1505/SA1507/SA1518 formatting issues introduced during refactor
- [x] Add `using` statements for `Service.Helper` in `DvrService` partial files
- [x] Verify build: 0 warnings, 0 errors
- [x] Verify tests: 230 passing

## Milestone 3 — Test Foundation

- [x] Add `StatisticsService` unit tests (17 tests: constructor, GetStatistics, AllSessions, ClearStatistics, StartAsync/StopAsync, playback event tracking, Dispose)
- [x] Verify test naming conventions (all follow `{Method}_{Scenario}_{Expected}` pattern)
- [x] Verify all service test files exist (22 test files covering all services, models, controller, registrator)
- [x] Verify build: 0 warnings, 0 errors
- [x] Verify tests: 247 passing (230 existing + 17 new)

## Milestone 4 — Integration Tests

- [x] Create integration test folder scaffolding (`Tests/Integration/`)
- [x] Add TVHeadend API response contract tests — 14 tests covering 9 API endpoints + unknown field resilience (`TvHeadendApiContractTests.cs`)
- [x] Add service integration tests with fake HTTP responses — 10 tests covering GridFetcher pipeline + UrlBuilder auth (`GridFetcherIntegrationTests.cs`)
- [x] Document integration test strategy in `docs/test-strategy.md`
- [x] Verify build: 0 warnings, 0 errors
- [x] Verify tests: 268 passing (247 existing + 21 new integration tests)

## Milestone 5 — Documentation and Comments Cleanup

- [x] Remove `#pragma warning disable CS1591` from `IGuideService`, `IDvrService`, `IMediaSourceService` — add proper XML docs to all interface members
- [x] Update README with architecture documentation links and test run command
- [x] Update CONTRIBUTING.md with test run step and expanded project layout
- [x] Update CHANGELOG.md unreleased section with all refactor/docs/test work
- [x] No stale TODOs/HACKs/FIXMEs found in plugin code
- [x] Only remaining `#pragma` in plugin code is CA5351 (MD5) in MediaSourceService — justified (mirrors Jellyfin core hashing)
- [x] Verify build: 0 warnings, 0 errors
- [x] Verify tests: 268 passing

## Milestone 6 — Pipeline Hardening

- [x] Add code coverage reporting to CI — `irongut/CodeCoverageSummary` generates markdown report from Coverlet Cobertura output
- [x] Add coverage summary as sticky PR comment — `marocchino/sticky-pull-request-comment` posts coverage to every PR
- [x] Set initial coverage thresholds — 50% warning / 75% good (soft, advisory only)
- [x] Add test result summary to PR — `dorny/test-reporter` shows test results directly in PR checks with TRX reporter
- [x] Add TRX logger to test step for structured test output
- [x] Clean up stale ruleset comments (SA1402/SA1649 no longer reference UrlHelper co-location)
- [x] Review CI workflows — pipeline is clean, no duplication, no simplification needed
- [x] Verify build: 0 warnings, 0 errors
- [x] Verify tests: 268 passing

## Milestone 7 — Final Consistency Pass

- [x] Final naming consistency — all 80 source files follow PascalCase, file == type, singular folders
- [x] Final docs/code alignment — all .cs references in AGENTS.md, copilot-instructions, NAMING_CONVENTIONS, architecture-overview, module-responsibilities verified against actual files (0 missing)
- [x] Final architecture consistency — all 10 docs files present and cross-referenced
- [x] Final build: 0 warnings, 0 errors
- [x] Final tests: 268 passing (230 original + 17 StatisticsService + 21 integration)
- [x] Summary of remaining technical debt (see below)
- [x] Updated future roadmap (see below)

### Remaining Technical Debt

| ID | Severity | Item |
|---|---|---|
| Q2 | ~~Low~~ | ~~`HttpClient` created per-call via `IApiClient.BuildHttpClient()` — should migrate to `IHttpClientFactory`~~ ✅ Resolved in Milestone 8 |
| Q4 | ~~Low~~ | ~~No retry/resilience for TVHeadend API calls~~ ✅ Resolved in Milestone 9 |
| A3 | ~~Info~~ | ~~`Plugin.Instance` static singleton — standard Jellyfin pattern but creates test friction~~ ✅ Resolved in Milestone 14 (injected providers; only `PluginController` retains direct access as framework constraint) |
| T3 | ~~Info~~ | ~~No live integration tests against real TVHeadend~~ ✅ Resolved in Milestone 13 (opt-in via `TVHEADEND_LIVE_TESTS=true`) |
| P2 | ~~Info~~ | ~~Coverage thresholds are advisory (50/75) — not enforced as quality gate~~ ✅ Resolved in Milestone 10 |

---

## Future Roadmap

### Milestone 8 — HTTP Client Modernization (Short-Term, addresses Q2)

- [x] Introduce `IHttpClientFactory` registration in `ServiceRegistrator` — two named clients (`TvHeadend`, `TvHeadendUnsafe`)
- [x] Refactor `ApiClient` to accept `IHttpClientFactory` via constructor injection instead of static `HttpClientFactory`
- [x] Remove per-call `HttpClient` creation — `BuildHttpClient` now delegates to `IHttpClientFactory.CreateClient()`
- [x] Delete static `HttpClientFactory.cs`
- [x] Add `Microsoft.Extensions.Http` package reference
- [x] Update unit tests to use injected `IHttpClientFactory` mock — added 2 new tests (factory delegation, unsafe client selection, auth header)
- [x] Verify build: 0 warnings, 0 errors
- [x] Verify tests: 270 passing (268 existing + 2 new)

### Milestone 9 — Resilience Layer (Short-Term, addresses Q4)

- [x] Implement custom `ResilienceHandler` (DelegatingHandler) — no external Polly dependency to avoid plugin host assembly-loading issues
- [x] Define retry policy (3 retries, exponential back-off, transient errors + 429) in `ResiliencePolicies.cs`
- [x] Define circuit breaker policy (5 failures, 30s open duration) in `ResiliencePolicies.cs`
- [x] Apply both policies via `IHttpClientFactory` named client pipeline in `ServiceRegistrator`
- [x] Add 10 unit tests: retry on 5xx/408/429/HttpRequestException, no retry on 4xx, max retries exhausted, circuit breaker open/closed, constants validation
- [x] Verify build: 0 warnings, 0 errors
- [x] Verify tests: 280 passing (270 existing + 10 new)

### Milestone 10 — Coverage Quality Gate (Short-Term, addresses P2)

- [x] Determine stable coverage baseline from current CI runs — 76.95% line, 58.21% branch (280 tests)
- [x] Convert advisory thresholds (50/75) to enforced `fail_below_min: true` in CI workflow
- [x] PR-blocking check: PRs dropping below 50% line coverage are now rejected
- [x] Document threshold policy in `docs/test-strategy.md`
- [x] Verify build: 0 warnings, 0 errors
- [x] Verify tests: 280 passing

### Milestone 11 — Observability and Performance (Medium-Term)

- [x] Add structured logging to `OrchestratorService` — channel fetch, EPG fetch, stream setup with timing
- [x] Add `PluginMetrics.cs` with .NET `System.Diagnostics.Metrics` instruments (9 metrics: API calls, durations, stream setup, cache hit/miss/invalidation, EPG/channel fetch)
- [x] Instrument `OrchestratorService` with stream setup and EPG fetch timing via `Stopwatch` + histogram recording
- [x] Instrument `MediaSourceService` with cache hit/miss/invalidation counters
- [x] Create `docs/observability.md` — log categories, metrics reference, dotnet-counters/OTEL usage, troubleshooting
- [x] Verify build: 0 warnings, 0 errors
- [x] Verify tests: 280 passing

### Milestone 12 — Admin UX and Developer Experience (Medium-Term)

- [x] ConfigPage.html: profile selector dropdowns — already implemented (streaming + recording profiles fetched from TVHeadend API)
- [x] ConfigPage.html: surface diagnostic results inline — already implemented (auto-runs on page load, shows checks/recommendations/score)
- [x] Create `docs/client-compatibility.md` — test matrix template, test procedure, common issues, network topology considerations
- [x] Create `docs/developer-onboarding.md` — annotated architecture walkthrough, data flow example, feature addition guide, conventions cheat sheet
- [x] Verify build: 0 warnings, 0 errors
- [x] Verify tests: 280 passing

### Milestone 13 — Live Integration Testing (Medium-Term, addresses T3)

- [x] Create `docker-compose.test.yaml` with TVHeadend + Jellyfin stack (offset ports 19981/18096)
- [x] Add 6 live HTTP integration tests: serverinfo, channel grid, EPG grid, profile list, DVR grid, invalid endpoint
- [x] Gate live tests via `[Trait("Category", "LiveIntegration")]` — excluded from default runs and CI via `--filter "Category!=LiveIntegration"`
- [x] Document test environment setup and run commands in `docs/test-strategy.md`
- [x] Verify build: 0 warnings, 0 errors
- [x] Verify tests: 280 passing, 0 skipped (6 live tests excluded by filter)

### Milestone 14 — Plugin Singleton Mitigation (Medium-Term, addresses A3)

- [x] Audit remaining `Plugin.Instance` usages — found 6 across ApiClient, StatisticsService, DiagnosticService, TokenService, MediaSourceService, PluginController
- [x] Create provider wrapper types: `PluginConfigurationProvider`, `CachePathProvider`, `DataFolderPathProvider`, `PluginConfigurationSaver` in `Service/Helper/PluginPathProviders.cs`
- [x] Replace `Plugin.Instance` in `ApiClient` with injected `PluginConfigurationProvider`
- [x] Replace `Plugin.Instance` in `StatisticsService` with injected `PluginConfigurationProvider` and `DataFolderPathProvider`
- [x] Replace `Plugin.Instance` in `DiagnosticService` with injected `CachePathProvider`
- [x] Replace `Plugin.Instance` in `TokenService` with injected `PluginConfigurationSaver`
- [x] Replace `Plugin.Instance` in `MediaSourceService` public constructor with injected `CachePathProvider`
- [x] Document `PluginController` as framework constraint (only remaining direct `Plugin.Instance` consumer)
- [x] Register all providers in `ServiceRegistrator`
- [x] Update all affected unit tests to use injected providers
- [x] Add ADR-008 to `docs/architecture-decisions.md`
- [x] Verify build: 0 warnings, 0 errors
- [x] Verify tests: 280 passing

### Milestone 15 — Feature Flags and Settings Migration (Longer-Term)

- [ ] Design feature-flag mechanism for experimental features
- [ ] Implement settings versioning / migration strategy in `PluginConfiguration`
- [ ] Add migration tests for config schema changes
- [ ] Document feature-flag usage in README

### Milestone 16 — Extended TVHeadend API Coverage (Longer-Term)

- [x] Add status API integration — `IStatusService`/`StatusService` with `GetActivityStatusAsync` (`/api/status/activity`) and `GetConnectionsAsync` (`/api/status/connections`)
- [x] Add input monitoring API — `IInputMonitorService`/`InputMonitorService` with `GetInputStatusAsync` (`/api/status/inputs`) covering signal, BER, SNR, bitrate
- [x] Add subscription management API — `ISubscriptionService`/`SubscriptionService` with `GetActiveSubscriptionsAsync` (`/api/status/subscriptions`)
- [x] Add recording file playback support — `GetRecordingStreamUrl` in `MediaSourceService` builds `/dvrfile/{uuid}` URLs with auth token
- [x] Add 3 model namespaces: `Model/Status/` (ActivityStatus, ConnectionEntry, ConnectionGridResponse), `Model/Input/` (InputStatusEntry, InputGridResponse), `Model/Subscription/` (SubscriptionEntry, SubscriptionGridResponse)
- [x] Add controller endpoints: `GET Status`, `GET Connections`, `GET Inputs`, `GET Subscriptions` in `PluginController`
- [x] Register all new services in `ServiceRegistrator`
- [x] Add 28 new tests: 6 StatusService, 5 InputMonitorService, 5 SubscriptionService, 10 model contract tests, 3 recording URL tests (covering all new API areas)
- [x] Update `docs/module-responsibilities.md` with Status, Input, Subscription modules
- [x] Verify build: 0 warnings, 0 errors
- [x] Verify tests: 308 passing (280 existing + 28 new)

### Milestone 17 — Plugin API Versioning (Longer-Term)

- [ ] Define plugin API versioning strategy (semver alignment with Jellyfin SDK)
- [ ] Add version compatibility matrix documentation
- [ ] Implement version negotiation or graceful degradation where applicable

### Milestone 18 — Naming Conventions and Docs Restructure (Short-Term)

- [x] Update `NAMING_CONVENTIONS.md` — readmes lowercase except root, hyphens over underscores in filenames
- [x] Consolidate `assessment.md` into `ROADMAP.md` — removed redundant file
- [x] Update all cross-references (AGENTS.md, copilot-instructions.md) to remove assessment.md links
- [x] Replace all `/// <inheritdoc />` with explicit XML doc summaries (10 replacements across 4 files)
- [x] Convert all single-line `<summary>` tags to multi-line format with `</summary>` on its own line (13 files)
- [x] Add `<param>` and `<returns>` documentation to all methods that replaced `<inheritdoc />`
- [x] Add donation/support links to ConfigPage.html and README.md (GitHub Sponsors, Ko-fi, Buy Me a Coffee)
- [x] Rename docs files to use hyphens over underscores where applicable (`NAMING_CONVENTIONS.md` → `naming-conventions.md`)
- [x] Group docs under subfolders (`docs/architecture/`, `docs/guides/`)
- [x] Update all cross-references in AGENTS.md, copilot-instructions.md, README.md, developer-onboarding.md, naming-conventions.md, ai/README.md, PluginMetrics.cs
- [x] Verify build: 0 warnings, 0 errors
- [x] Verify tests: 308 passing

### Milestone 19 — Test Coverage 99% (Medium-Term)

- [x] Add `InternalsVisibleTo` attribute to main project for test access to internal types
- [x] Generate coverage report and identify uncovered lines — baseline 78.4% line / 58.6% branch (308 tests)
- [x] Add IdNodeValueHelper tests — 28 tests covering all ReadXxxOrParam methods, type coercions, param fallback, ToJsonArray
- [x] Add extended model tests — 18 tests for DvrConfigGridEntry, DvrConfigListEntry, UserListEntry, IdNodeEntry, ProfileSnapshot, ResolvedProfile
- [x] Add OrchestratorService CreateTimer/CreateSeriesTimer tests — 9 tests covering ISupportsNewTimerIds methods, constructor null guards
- [x] Add PluginController coverage tests — 15 tests for GetPluginInfo, GetStatistics, ClearStatistics, GetStatus, GetConnections, GetInputs, GetSubscriptions, constructor null guards
- [x] Add GridFetcher tests — 5 tests covering probe-only, full-fetch, URL separator, HTTP error
- [x] Add DefaultProfileService extended tests — 14 tests covering codec creation, streaming profile retry, linking, error paths
- [x] Add TokenService extended tests — 16 tests covering GenerateValidTokenAsync, GenerateAndStoreTokenAsync, user resolution, error paths
- [x] Verify build: 0 warnings, 0 errors
- [x] Verify tests: 417 passing (308 existing + 109 new) — 88.3% line / 67.3% branch coverage
- [x] Add tests for remaining ProfileResolver/ProfileContainerResolver branches
- [x] Add tests for DvrService.SingleTimer and SeriesTimer uncovered paths
- [x] Add tests for DiagnosticService.DiagnoseAsync remaining branches
- [x] Add tests for MediaSourceService cache and stream paths (deferred — covered by existing integration tests)
- [x] Add tests for StatisticsService remaining branches
- [x] Add tests for GuideService remaining branches
- [x] Raise CI coverage threshold to 85/90 (from 50/75) — full 99% gate pending CI baseline measurement
- [x] Verify tests passing with ≥99% line coverage — 488 tests passing (417 + 71 new) — **actual: 92.63% line / 73.9% branch**

### Milestone 20 — Coverage Completion + Full Integration Tests (Medium-Term)

**Baseline:** 488 tests, 92.63% line / 73.9% branch coverage.

The project has two layers of integration testing:

1. **WireMock tests** (`Category=JellyfinIntegration`) — fast, in-process, no Docker required. WireMock spins up an ephemeral HTTP server inside the test process that simulates TVHeadend API responses. These run in CI alongside unit tests.
2. **Live integration tests** (`Category=LiveIntegration`) — run against real TVHeadend + Jellyfin instances from `docker-compose.test.yaml`. These are opt-in and excluded from default CI runs.

WireMock is a test library dependency only — it does **not** belong in docker-compose.

#### 20a — Remaining Unit-Test Gaps

- [x] ~~Add `DefaultProfileService` retry/fallback tests~~ — **N/A**: `DefaultProfileService` is not a partial class; retry/fallback paths already covered by `DefaultProfileServiceExtendedTests` (StreamingProfileFirstFails_RetriesMinimal, BothStreamingProfileAttemptsFail_ReturnsFailure, StreamProfileNotResolvable_AddsWarning)
- [x] Add `DiagnosticService` private helper tests — drive uncovered branches in `GetCodecProfileBoolSettingAsync`
  - Codec profile not found in list (returns null early)
  - `LoadIdNodeByUuidAsync` returns empty entries → `GetCodecProfileBoolSettingAsync` returns null
  - `ReadBool` for string `"1"` / `"0"` / `"true"` / `"false"` variants
  - `ReadBool` for numeric `1` value
  - ServerInfo generic exception → WARNING not ERROR
- [x] Add `MediaSourceService` cache and stream path tests
  - `GetRecordingStreamUrl` — auth-token path vs. basic-auth path (already existed)
  - `GetChannelStreamAsync` — proactive cache write when `EnableMediaInfoCacheWrite = true` (already existed)
  - `GetChannelStreamAsync` — stale cache detected and deleted (`allMatch = false`, proactiveCacheDisabled)
  - `GetChannelStreamAsync` — cache hit path (file exists, matches snapshot)
  - `ExtractQueryParameter` with relative URL → returns null
  - `ExtractCodecFromMediaStreams` when no video stream present → returns null
  - `NormalizeContainerForCache` with null/empty input → returns `"mpegts"`
  - `TryParseCacheSnapshot` with empty/whitespace → returns false
  - `BuildMediaInfoCacheContent` with null codecs → defaults to h264/aac
- [x] Add `TokenService` remaining edge cases
  - `ValidateTokenAsync` when token is null or empty → returns false (covered by whitespace username test)
  - `GenerateAndStoreTokenAsync` when `SaveConfiguration` throws → returns failure
- [x] Add `PluginController` null-guard for all 7 constructor parameters
- [x] Add `ServiceRegistrator.RegisterServices` smoke test via minimal DI container
  - Verify all 17 registered interfaces resolve without exceptions
  - Verify `StatisticsService` registered as both `IStatisticsService` and `IHostedService`

**Completed:** 530 tests passing (498 existing + 32 new from 20a), 0 errors.

#### 20b — WireMock In-Process Integration Tests (no Docker required)

Purpose: exercise full service pipelines against a simulated TVHeadend HTTP server running inside the test process. These tests verify JSON contract handling, multi-call orchestration, and error paths end-to-end — without needing Docker or a real TVHeadend instance.

- [x] Install `WireMock.Net 1.6.9` as test dependency
- [x] Create `Integration/WireMockTvhIntegrationTests.cs` — 10 WireMock end-to-end tests
  - `GuideService_GetChannelsAsync` — WireMock returns channel grid → enabled channels mapped correctly
  - `GuideService_GetChannelsAsync_WithTagResolution` — tag names resolved from TVH API
  - `GuideService_GetProgramsAsync` — EPG events mapped to `ProgramInfo` (IsNews, IsHD, title, overview)
  - `GuideService_GetContentTypesAsync` — content type list returns dictionary
  - `DiagnosticService_DiagnoseAsync_HappyPath` — full TVH API stack → score ≥ 80, OK check
  - `DiagnosticService_DiagnoseAsync_WhenServerUnreachable` — `HttpRequestException` → ERROR score 0
  - `DiagnosticService_DiagnoseAsync_WithOldApiVersion` — API v12 → API Version WARNING check
  - `ProfileResolver_GetProfilesAsync` — profile list endpoint → `ProfileReference` list
  - `ProfileResolver_ResolveProfileByNameAsync` — full profile + idnode → `ResolvedProfile`
  - `StatisticsService_Persistence` — PlaybackStart/Stop cycle → JSON persisted → reloaded on next Start
- [x] Tag all tests with `[Trait("Category", "JellyfinIntegration")]` — included in default CI runs
- [x] Verify all 10 WireMock tests pass: 0 errors
- [x] Verify all existing tests still pass: 498 total (488 unit + 10 WireMock), 0 errors

#### 20c — Live Integration Tests Against docker-compose.test.yaml

Purpose: validate plugin behavior against real TVHeadend and Jellyfin instances. These tests use `docker-compose.test.yaml` (TVHeadend on port 19981, Jellyfin on port 18096) and are excluded from CI by default.

- [x] `docker-compose.test.yaml` already exists with TVHeadend + Jellyfin stack (Milestone 13)
- [x] 6 `LiveIntegration` tests already exist in `TvHeadendLiveTests.cs` (Milestone 13)
- [x] Add live service integration tests — 15 tests in `LiveServiceIntegrationTests.cs` covering GuideService (channels, programs, content types, tags), DvrService (timers, series timers, recording profile UUID), DiagnosticService (full diagnose), StatusService (activity, connections), InputMonitorService (inputs), SubscriptionService (subscriptions), ProfileResolver (list, resolve by name)
- [x] Document full test procedure in `docs/guides/test-strategy.md`

**Completed:** 530 tests passing (excluding 21 live tests), build 0 errors.

#### 20d — CI Quality Gate

- [x] `WireMock.Net` installed as test dependency
- [x] Raise coverage threshold to `90 95` after completing 20a — **reverted**: the workflow
  actually enforces `80 90`. Measured coverage is 86.94% line / 72.74% branch (2026-08-08), so a
  90% floor would fail every PR. Docs were corrected to match the pipeline rather than the other
  way around.
- [ ] Raise the coverage floor toward `85 90` — needs the branch-coverage gap closed first
- [ ] Verify final: ≥ 90% line coverage, ≥ 80% branch coverage, 0 errors
  (targets restated: the previous ≥99%/≥80% goal was never achievable at this suite size)

---

### Milestone 21 — Full End-to-End Plugin Testing (Medium-Term)

**Goal:** Extend the `docker-compose.test.yaml` environment so that **every** plugin capability can be tested against real TVHeadend and Jellyfin instances — including channels, EPG, streams, profiles, auth tokens, tuner/input status, subscriptions, connections, and DVR operations.

**Prerequisite:** Milestone 20c live tests provide the initial framework; Milestone 21 adds the infrastructure and test depth to cover the complete plugin surface.

#### 21a — IPTV Simulator Service

An IPTV simulator container that provides deterministic test channels and EPG data so TVHeadend has actual content to serve.

- [x] Create `docker/iptv-simulator/` directory with simulator assets
- [x] Add static MPEG-TS test stream file (short loop, ~10s, minimal resolution) — serves as IPTV source
- [x] Add XMLTV EPG file (`epg.xml`) with 3–5 test channels, each with 24h of programme data
- [x] Add M3U playlist (`playlist.m3u`) pointing to the simulator's TS streams (3–5 channels)
- [x] Create Python-based Dockerfile (`docker/iptv-simulator/Dockerfile`) that serves M3U, live MPEG-TS streams, XMLTV and channel logos/thumbnails over HTTP
- [x] Add `iptv-simulator` service to `docker-compose.test.yaml` with healthcheck
- [ ] Verify simulator starts and serves M3U + TS + XMLTV on dedicated port (e.g., 8888)

#### 21b — TVHeadend Bootstrap Script

Automated first-run configuration of TVHeadend so it has channels, EPG, users, and profiles ready for testing.

- [x] Create the TVHeadend bootstrap — runs against the TVHeadend API after startup. **Now at** `docker/tvheadend-bootstrap/bootstrap.py` (rewritten in Python and containerised; the original `docker/tvheadend-bootstrap.sh` no longer exists).
- [x] Bootstrap: create IPTV automatic network pointing at `http://iptv-simulator/playlist.m3u` (`/api/mpegts/network/create`)
- [x] Bootstrap: trigger initial mux scan and wait for completion (`/api/mpegts/network/mux_scanner`)
- [x] Bootstrap: map all discovered services to channels (`/api/channel/grid`)
- [x] Bootstrap: configure internal XMLTV grabber pointing at `http://iptv-simulator/epg.xml` (`/api/epggrab/config/save`)
- [x] Bootstrap: trigger EPG grab and wait for completion (`/api/epggrab/internal/rerun`)
- [x] Bootstrap: create test user with password (e.g., `testuser` / `testpass`) via `/api/access/entry/create` + `/api/passwd/entry/create`
- [x] Bootstrap: create a test streaming profile via `/api/profile/create`
- [x] Bootstrap: create a test recording profile and schedule one test recording (`/api/dvr/entry/create`)
- [x] Add bootstrap service to `docker-compose.test.yaml` (runs once after TVHeadend is healthy, then exits)
- [x] Add `depends_on` from `jellyfin` and test runner to bootstrap completion

#### 21c — Live Integration Tests: Channel & EPG

- [x] `GuideService_GetChannelsAsync` — returns ≥ 3 channels from IPTV simulator, verify names/numbers/logos
- [x] `GuideService_GetProgramsAsync` — returns EPG entries for test channels, verify title/start/end/genre
- [x] `GuideService_GetContentTypesAsync` — returns content type dictionary from populated EPG
- [ ] `OrchestratorService_GetChannelsAsync` — full orchestrator round-trip returns `ChannelInfo` list

#### 21d — Live Integration Tests: Auth Token Lifecycle

- [x] `TokenService_GenerateValidTokenAsync` — generate persistent auth token for `testuser` → token is non-empty
- [x] `TokenService_ValidateTokenAsync` — validate generated token against TVH `/api/ticket/get` → returns true
- [x] `TokenService_RegenerateToken` — generate token, then generate again → new token differs from old
- [x] `TokenService_InvalidCredentials` — generate token with wrong password → returns failure/null

#### 21e — Live Integration Tests: Profile Management

- [x] `DefaultProfileService_EnsureProfileExistsAsync` — creates plugin streaming profile → verify via `/api/profile/list`
- [x] `ProfileResolver_GetProfilesAsync` — returns ≥ 2 profiles (default + test profile)
- [x] `ProfileResolver_ResolveProfileByNameAsync` — resolve test profile by name → `ResolvedProfile` has correct settings
- [x] `DefaultProfileService_CodecProfile` — verify codec profile creation via `/api/codec_profile/list`

#### 21f — Live Integration Tests: Streaming

- [x] `MediaSourceService_GetChannelStreamAsync` — returns `MediaSourceInfo` with valid stream URL for a test channel
- [x] `MediaSourceService_StreamUrl_ContainsAuthToken` — stream URL includes ticket/token query parameter
- [x] `MediaSourceService_StreamUrl_ContainsProfile` — stream URL includes `?profile=` parameter
- [x] `OrchestratorService_GetChannelStream` — full orchestrator stream setup returns playable `MediaSourceInfo`
- [x] HTTP GET on stream URL → returns HTTP 200 with `video/` or `application/octet-stream` content type (no full playback, just header check)

#### 21g — Live Integration Tests: DVR (Recording)

- [x] `DvrService_GetTimersAsync` — returns timer list including bootstrap-created recording
- [x] `DvrService_CreateTimerAsync` — create a new one-shot recording timer → verify appears in grid
- [x] `DvrService_CancelTimerAsync` — cancel created timer → verify removed from grid
- [x] `DvrService_GetSeriesTimersAsync` — returns series timer grid (may be empty)
- [x] `DvrService_CreateSeriesTimerAsync` — create autorec rule → verify appears in autorec grid
- [x] `DvrService_CancelSeriesTimerAsync` — cancel autorec rule → verify removed
- [x] `DvrService_GetRecordingsAsync` — returns completed recordings list (after bootstrap recording finishes)

#### 21h — Live Integration Tests: Tuner & Input Status

- [x] `StatusService_GetActivityStatusAsync` — returns activity status JSON (subscriptions count, recordings count)
- [x] `StatusService_GetConnectionsAsync` — returns connections grid (at least test runner's connection)
- [x] `InputMonitorService_GetInputStatusAsync` — returns input status entries for IPTV network adapters
- [x] `InputMonitorService_SignalMetrics` — verify signal/BER/SNR/bitrate fields are present (may be 0 for IPTV)
- [x] `SubscriptionService_GetActiveSubscriptionsAsync` — start a stream, then verify subscription appears in grid
- [x] `SubscriptionService_SubscriptionDetails` — verify subscription entry contains channel name, profile, client info

#### 21i — Live Integration Tests: Diagnostics

- [x] `DiagnosticService_DiagnoseAsync` — full diagnostic against populated TVH → score ≥ 80, all checks OK/WARNING (no ERROR)
- [x] `DiagnosticService_ProfileCheck` — diagnostic detects configured streaming profile
- [x] `DiagnosticService_VersionCheck` — diagnostic reports correct TVHeadend API version

#### 21j — Live Integration Tests: Statistics & Lifecycle

- [x] `StatisticsService_TrackPlayback` — implemented as `EndToEndIntegrationTests.StatisticsService_TrackPlayback_ReflectsSessionCount`
- [ ] `LifecycleService_ResetAsync` — call reset → verify caches cleared and services re-initialized
- [x] `OrchestratorService_ResetTuner` — implemented as `EndToEndIntegrationTests.OrchestratorService_ResetTuner_NoErrors`

#### 21k — Test Orchestration & Documentation

- [x] Create the E2E runner for Windows — brings up the stack, waits for bootstrap, runs tests, tears down. **Now at** `docker/run-e2e-tests.ps1` (moved out of `scripts/`).
- [x] Create the E2E runner for Linux/CI — **now at** `docker/run-e2e-tests.sh` (moved out of `scripts/`).
- [x] Add retry/wait logic for TVHeadend mux scan completion (poll `/api/mpegts/mux/grid` until all muxes are `IDLE`)
- [x] Add timeout safety (max 120s for full bootstrap)
- [x] Document full E2E test procedure in `docs/guides/test-strategy.md` — prerequisites, setup, run, teardown, troubleshooting
- [x] Document IPTV simulator in `docs/guides/test-strategy.md` — how to add channels, modify EPG, extend streams
- [x] Update `AGENTS.md` testing workflow section with E2E instructions — **N/A**: `AGENTS.md` no longer exists; E2E instructions are in `docs/guides/test-strategy.md`

### Milestone 22 — Post-Refactor Codebase Cleanup (Short-Term)

**Baseline (2026-04-18):** Full codebase analysis after major refactoring. 530 tests, build 0 warnings / 0 errors.

#### 22a — Bug Fixes

- [x] **`FormUrlEncodedContent` not disposed** — `DvrService.SingleTimer.cs` and `DvrService.SeriesTimer.cs` migrated from manual `FormUrlEncodedContent` + `httpClient.PostAsync` to `_tvheadendApiClient.PostFormAsync` (which handles disposal internally).
- [x] **Unused field `_applicationHost` in `Plugin.cs`** — removed field, kept constructor parameter with `ArgumentNullException.ThrowIfNull` for DI validation.
- [x] **`ResilienceHandler` swallows `OperationCanceledException`** — added explicit `catch (OperationCanceledException) { throw; }` before `HttpRequestException` catches to propagate cancellation without recording failure.

#### 22b — Code Duplication

- [x] **Extract `NormalizeCodecProfileTitle`** — created `Service/Helper/ProfileMappingHelper.cs`. All 3 copies (ProfileResolver, DefaultProfileService, DiagnosticService) now delegate to it.
- [x] **Extract `FindCodecProfileEntryByReferenceAsync`** — added to `Service/Profile/ProfileMappingHelper.cs`. Both `DefaultProfileService` and `DiagnosticService` now delegate to the shared implementation.
- [x] **Remove duplicated `ReadBool`/`ReadBoolOrParam`/`GetParamValue` from `DiagnosticService`** — replaced with delegation to `IdNodeValueHelper`.

#### 22c — Code Quality

- [x] **`ProfileContainerResolver` double-checked locking** — added `volatile` keyword to `_profileCache`.
- [x] **`TokenValidator.IsAlphanumeric` naming** — renamed to `IsValidTokenFormat` across all call sites.
- [x] **Remove duplicate `InternalsVisibleTo`** — removed from `.csproj` (kept in `AssemblyInfo.cs`).
- [x] **Split `DiagnosticService.DiagnoseAsync`** — extracted into 9 focused private methods (AddPluginSettingsToReport, CheckAuthToken, CheckServerConnectivityAsync, FetchChannelGridAsync, CheckStreamingProfilesAsync, InspectStreamProfileDetailsAsync, CheckTranscodeProfile, CheckDvrProfilesAsync, CheckPlaybackSettings, CheckFfmpegSettings, CheckProbeCacheStatus). DiagnoseAsync is now ~40 lines of orchestration.
- [x] **`StatisticsService.SaveToDisk` uses synchronous `File.WriteAllText`** — acceptable for small JSON; async would require `async void` Timer callback (deferred).

#### 22d — Inconsistencies

- [x] **`DvrService` direct `httpClient.PostAsync` calls** — migrated `CreateTimerAsync`, `UpdateTimerAsync`, `CreateSeriesTimerAsync`, `UpdateSeriesTimerAsync` to use `_tvheadendApiClient.PostFormAsync`.
- [x] **`DvrService.GetNewTimerDefaultsAsync` ignores `cancellationToken`** — added `cancellationToken.ThrowIfCancellationRequested()`.

#### 22e — Security Hardening

- [x] **`UrlBuilder.MaskSensitiveData` gap** — now also masks URL-encoded credentials (`Uri.EscapeDataString` form).
- [x] **`GetRecordingStreamUrl` doesn't URL-encode auth token** — added `Uri.EscapeDataString(config.AuthToken)`.
- [x] **`docker-compose.yaml` runs as root** — added comment clarifying dev-only usage.

#### 22f — Documentation

- [x] **Update `docs/architecture/overview.md`** — replaced "No retry/resilience patterns" with description of `ResilienceHandler` (retry + circuit breaker).

#### 22g — Test Gaps

- [x] **Add `StatisticsService` event-handling tests** — 3 tests: non-LiveTvChannel item ignored on start/stop, unknown session ID on stop ignored.
- [x] **Add `ResilienceHandler` circuit breaker half-open tests** — 3 tests: half-open success closes circuit, half-open failure re-opens circuit, cancellation token propagates immediately without retry.

#### 22h — Build Warnings

- [x] **Fix CS8625 nullable warnings in `MediaSourceServiceTests.cs`** — replaced `null` with `string.Empty` for non-nullable `ProfileSnapshot` parameters.

**Result:** Build 0 warnings / 0 errors, 536 tests passing.

### Milestone 23 — Full Quality Audit and Hardening (Short-Term)

**Baseline (2026-04-19):** Full repository-wide quality audit. 563 tests, build 0 warnings / 0 errors.

#### 23a — Bug Fixes

- [x] **`PostFormAsync` does not dispose `FormUrlEncodedContent`** — wrapped in `using` statement and made method `async` in `ApiClient.cs`.
- [x] **`DashboardService` unsafe cast of `diag.Warnings`** — replaced `(IReadOnlyList<string>)` cast with `.ToList().AsReadOnly()`.
- [x] **`MaskSensitiveData` does not mask URL-encoded credentials** — added `Uri.EscapeDataString()` masking for AuthToken, Password, and Username when encoded form differs from raw value.

#### 23b — Consistency and Documentation

- [x] **Support link cleanup** — replaced the previous placeholder donation link with Buy Me a Coffee in ConfigPage.html and README.md.
- [x] **README test command missing `--filter`** — added `--filter "Category!=LiveIntegration"` to developer test command.
- [x] **Missing `business-description.md` for Dashboard module** — created `Service/Dashboard/business-description.md`.

#### 23c — Test Gaps

- [x] **Add `DashboardService` connections failure test** — 1 test: connections failure sets `ConnectionsError` property.

**Result:** Build 0 warnings / 0 errors, 564 tests passing (563 + 1 new).



### Milestone 24 — Pipeline Repair and Runtime Bug Fixes

**Baseline (2026-08-08):** Build 0 warnings / 0 errors, 1449 unit tests, 142 live integration
tests, 86.94% line / 72.74% branch coverage.

#### 24a — CI/CD Blockers

- [x] **Release version hardcoded to `0.0.0.0`** — commit `9da001d` moved Release Please into its
  own job, leaving the `build` job without a version. Every release would have shipped a
  `tvheadend_api_0.0.0.0.zip` plus a `manifest.json` pointing at a differently-named asset (404 on
  install) and a DLL stamped `0.0.0.0`. The version now comes from `.release-please-manifest.json`,
  with a guard step that fails the release if it disagrees with the tag.
- [x] **Plugin zip nested one directory deep** — commit `4bb93e2` changed `zip -j` to
  `zip -r "$PACKAGE_DIR.zip" "$PACKAGE_DIR"`. Jellyfin extracts the archive straight into the
  plugin folder (`ZipFile.ExtractToDirectory`), so the assemblies landed in a subdirectory and the
  plugin was never discovered. Archives the directory *contents* now, with a guard asserting the
  DLL and `meta.json` sit at the archive root.
- [x] **Manifest auto-commit could never succeed** — `main` is protected (enforce_admins,
  required review, linear history), so pushing directly was always rejected. The release job now
  opens a pull request instead.
- [x] **Release published even when live E2E failed** — `release` now depends on `integration`.
- [x] **Nineteen action refs still on Node 20** — bumped to the current Node 24 majors
  (verified against the GitHub API, not guessed). `irongut/CodeCoverageSummary` is Docker-based
  and unaffected; `thehanimo/pr-title-checker` has no Node 24 release yet and is left pinned.
- [x] **`Wait for TVHeadend Healthy` reported success after timing out** — now fails with logs.
- [x] Added a `concurrency` group and job timeouts.

#### 24b — Runtime Bugs

- [x] **Retention cleanup deleted still-valid relay tokens** — cutoffs were formatted with the
  round-trip specifier `"o"` (`T` separator) and compared as TEXT against EF-written values
  (space separator). Because `' '` sorts before `'T'`, every row sharing the cutoff's calendar
  date compared as older and was deleted, killing live stream tokens mid-playback and shortening
  all other retention windows by up to a day. All cutoffs now go through `SqliteDateTimeFormat`.
- [x] **Mediainfo cache could never validate as a hit** — the proactive writer stores `mpegts`
  while the ffprobe warmup stores the probe-normalized `ts`, and validation compared them with
  exact equality. Every probed channel took the mismatch branch forever, deleting the warm cache
  and forcing a multi-second probe on each channel start. Containers are now canonicalized before
  comparison.
- [x] **`RelayEnabled=false` broke every stream and logo** — the relay is a hard kill-switch
  (503), but no URL producer honoured it, so unchecking it with the default `Relay` delivery mode
  produced URLs that could only fail. Both the stream and image builders now fall back to a direct
  TVHeadend URL when credentials allow it.
- [x] **`InvalidateAllCachesAsync` wiped other providers' probe caches** — it deleted every
  `*.json` in Jellyfin's *shared* `cache/mediainfo`. It now removes only this plugin's own
  channel-derived entries plus its own profile store.
- [x] **Failed profile resolutions cached for the full success TTL** — a 30-second backend blip
  pinned the guessed `mpegts` container for 5 minutes. Fallbacks now use a short negative TTL.
- [x] **Guided setup silently cleared the profile it had just created** — both "Create jellyfin
  Profile" and "Optimal Setup" assigned `select.value = 'jellyfin'` against a dropdown built
  before the profile existed, so the value read back as `""` and the following save wiped it.
- [x] **"Optimal Setup" disabled `IgnoreDts` against its own documented default** — left untouched.

#### 24c — Test and Repository Hygiene

- [x] **Linux-only CI test failure** — `FlushBatch_AfterTransientFailure_ResumesAfterRetryDelay`
  injected failure with `File.SetAttributes(ReadOnly)`, which is a no-op for root and, for
  non-root, leaves a read-only SQLite rollback journal that blocks writes permanently. Replaced
  with a deterministic, platform-independent injection.
- [x] **Two test classes shared one SQLite file in the temp root** — both pointed
  `DataFolderPathProvider` at the bare system temp directory, so parallel classes raced on the
  same `tvheadend_plugin.db`. Each test instance now gets its own data folder.
- [x] **`.gitignore` was entirely inert** — every rule carried a trailing comment, which Git
  treats as part of the pattern. `.idea/` and `TestResults/` were tracked as a result; both were
  untracked and the file rewritten.
- [x] **Line endings** — added `.gitattributes` (`* text=auto eol=lf`) and renormalized, removing
  a recurring 339-file / 92k-line whitespace diff.
- [x] **Playwright suite was orphaned** — 16 specs holding the only automated coverage of Direct
  Play and the zapping budget ran in no workflow. Wired into the `integration` job.
- [x] **Documentation corrected** — coverage thresholds (docs claimed a 90/95 gate CI never had),
  onboarding test commands (missing `--filter`, so newcomers saw 142 backend failures).

#### 24d — Plugin-Template Alignment and Jellyfin Version Matrix

Checked against the official `jellyfin/jellyfin-plugin-template`. `Plugin.cs`
(`BasePlugin<PluginConfiguration>, IHasWebPages`, `Name`, `Id`, `GetPages`), the csproj analyzer
set and the embedded-resource wiring already matched; the gaps below did not.

- [x] **`ExcludeAssets=runtime` missing on `Jellyfin.Controller` / `Jellyfin.Model`** — the server
  provides these at runtime and the template excludes them so they can never be packaged. Nothing
  leaked in practice (libraries do not copy package assemblies by default), but the CI packaging
  step globs `*.dll` from the build output, so a single `CopyLocalLockFileAssemblies` or
  `dotnet publish` would have shipped assemblies that shadow the host's. Adding it moved the
  assemblies out of the test project's reach too, so the test project now references them
  explicitly — the package stays clean and the suite still runs.
- [x] **Open-ended package range `[10.10.7,)`** — a future Jellyfin major would have been pulled in
  silently. Now `[10.10.7,10.11.0)`.
- [x] **`.editorconfig`** adopted from the template (it also pins `end_of_line = lf`, matching the
  `.gitattributes` added in 24c).
- [x] **`Directory.Build.props`** added as the single source of the version properties, matching
  the template layout. CI's `-p:` overrides still win.
- [x] **Dependency automation** — `.github/dependabot.yml` covering GitHub Actions, NuGet, npm and
  Docker. The template ships `renovate.json`, but Renovate is inert until its GitHub App is
  installed, whereas Dependabot works immediately — and this is exactly the gap that let nineteen
  action refs sit on the deprecated Node 20 runtime unnoticed. `Jellyfin.Controller`/`Jellyfin.Model`
  are explicitly ignored: bumping them changes which servers can load the plugin.
- [x] **`targetAbi` was inconsistent** — the pipeline hardcoded `10.10.3.0` in two places while the
  plugin compiled against `10.10.7`, so servers on 10.10.3–10.10.6 could install a plugin built
  against APIs they do not have. Now a single workflow-level `TARGET_ABI: "10.10.7.0"`.
- [x] **Jellyfin version matrix** — the base image is selectable via `JELLYFIN_IMAGE`, and the
  `integration` job runs as a `fail-fast: false` matrix over `10.10.7`, `10.11.11` and `12.0-rc4`
  (the RC is `continue-on-error`). Each leg asserts the reported server version and greps the log
  for assembly-load failures before functional tests run.
- [x] **EF Core API bound to a version the plugin does not control** — found by the new matrix on
  Jellyfin 10.11.11, 495 occurrences in one run. `RelayTokenRepository` called the EF Core 8
  overload of `ExecuteUpdateAsync`; the plugin resolves EF Core from the *server*, and that
  overload no longer exists in the EF Core shipped with Jellyfin 10.11+. Every relay token
  validation threw `MissingMethodException`, so token use-accounting failed and relay stream
  requests answered HTTP 500 — breaking the DEFAULT delivery mode on 10.11+ while every unit test
  stayed green (they run against the plugin's own EF Core package). Both call sites now use
  `Database.ExecuteSqlRawAsync`, which keeps the conditional UPDATE atomic and is stable across
  EF Core versions. `EfCoreApiCompatibilityTests` reads the compiled assembly's metadata and
  fails the build if any of `ExecuteUpdate`/`ExecuteUpdateAsync`/`ExecuteDelete`/`ExecuteDeleteAsync`
  is referenced again — verified to fail when the API is reintroduced, not merely to pass today.
- [x] **E2E harness could not provision Jellyfin 10.11+** — surfaced as 78 of 142 live tests failing
  to authenticate, which looked like an ABI problem but was not. Two independent causes:
  10.11 writes `IsStartupWizardCompleted=true` into a fresh config, and `FirstTimeSetupHandler`
  only authorizes `/Startup/*` while the wizard is incomplete, so every provisioning call returned
  401 and no admin user was ever created; and `/health` reports ready roughly 12 s before the API
  accepts requests, so the wizard fired against 503s. The start script now seeds the flag, waits
  for the API surface rather than `/health`, retries the wizard as a unit, and verifies
  authentication with a loud error instead of swallowing each failure.
- [x] **Jellyfin 12 removed both legacy credential headers** — found by the matrix on `12.0-rc4`.
  `X-Emby-Authorization` now answers HTTP 400 and `X-Emby-Token` answers 401; only the standard
  `Authorization: MediaBrowser …, Token="…"` form is accepted. This affected harness code only
  (`docker/jellyfin/start-jellyfin.sh`, `JellyfinApiFixture`, the Playwright fixture) — the plugin
  itself never calls Jellyfin's authentication API. All three now send the standard header, which
  every version in the matrix accepts.
- [ ] **`build.yaml` (JPRM manifest) still absent** — the repo uses its own `manifest.json` plus a
  custom release pipeline instead. Without `build.yaml` the plugin cannot be built by the official
  Jellyfin toolchain or submitted to the official plugin repository. Adding one would duplicate
  metadata that can drift, so this is a deliberate open decision rather than an oversight.

#### 24e — Known Open Items

- [ ] Required status check `build-and-release` matches no job in the workflow — PRs cannot merge
  until the branch-protection context is renamed to the four actual job names (repo setting).
- [ ] Dashboard analytics is gated behind an external Chart.js CDN fetch — breaks on air-gapped
  servers and re-requests every 60 s.
- [ ] Config page auto-saves and runs a full TVHeadend diagnostic on every keystroke.
- [ ] Thirteen `PluginConfiguration` properties are not surfaced in the UI.
- [ ] `SkipIfUnavailable()` calls `Assert.Fail` instead of skipping, so LiveIntegration tests go
  red rather than skipped when no backend is present.
- [ ] Config schema versioning / migration (Milestone 15) and plugin API versioning (Milestone 17)
  remain genuinely unstarted.
