# Roadmap

This document tracks the structured refactor and quality improvement of the Jellyfin TVHeadend API plugin.

**Baseline (2026-04-16):** Build 0 warnings / 0 errors, 230 tests passing.

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

- [x] Add `Microsoft.Extensions.Http.Polly` 8.0.26 dependency
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

- [x] Create `docker-compose.test.yml` with TVHeadend + Jellyfin stack (offset ports 19981/18096)
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
- [x] Add donation/support links to ConfigPage.html and README.md (GitHub Sponsors, Ko-fi, PayPal)
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
- [ ] Add tests for remaining ProfileResolver/ProfileContainerResolver branches
- [ ] Add tests for DvrService.SingleTimer and SeriesTimer uncovered paths
- [ ] Add tests for DiagnosticService.DiagnoseAsync remaining branches
- [ ] Add tests for MediaSourceService cache and stream paths
- [ ] Add tests for StatisticsService remaining branches
- [ ] Add tests for GuideService remaining branches
- [ ] Raise CI coverage threshold to 99%
- [ ] Verify tests passing with ≥99% line coverage

