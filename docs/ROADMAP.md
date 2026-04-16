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
- [x] `docs/assessment.md` created

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
| Q2 | Low | `HttpClient` created per-call via `IApiClient.BuildHttpClient()` — should migrate to `IHttpClientFactory` |
| Q4 | Low | No retry/resilience for TVHeadend API calls |
| A3 | Info | `Plugin.Instance` static singleton — standard Jellyfin pattern but creates test friction (already mitigated with constructor injection in `MediaSourceService` and `StatisticsService`) |
| T3 | Info | No live integration tests against real TVHeadend (contract tests cover JSON shape, but not HTTP behavior) |
| P2 | Info | Coverage thresholds are advisory (50/75) — not enforced as quality gate |

---

## Future Roadmap

### Milestone 8 — HTTP Client Modernization (Short-Term, addresses Q2)

- [ ] Introduce `IHttpClientFactory` registration in `ServiceRegistrator`
- [ ] Refactor `IApiClient` / `ApiClient` to accept `HttpClient` via factory instead of `BuildHttpClient()`
- [ ] Remove per-call `HttpClient` creation
- [ ] Update unit tests to use injected `HttpClient` (via `MockHttpMessageHandler`)
- [ ] Verify build: 0 warnings, 0 errors
- [ ] Verify tests: all passing

### Milestone 9 — Resilience Layer (Short-Term, addresses Q4)

- [ ] Add `Microsoft.Extensions.Http.Polly` (or `Microsoft.Extensions.Http.Resilience`) dependency
- [ ] Define retry + circuit-breaker policy for TVHeadend API calls
- [ ] Apply policy via `IHttpClientFactory` named/typed client pipeline
- [ ] Add unit tests for retry behavior (transient failures, timeout, circuit open)
- [ ] Verify build: 0 warnings, 0 errors
- [ ] Verify tests: all passing

### Milestone 10 — Coverage Quality Gate (Short-Term, addresses P2)

- [ ] Determine stable coverage baseline from current CI runs
- [ ] Convert advisory thresholds (50/75) to enforced `fail_below_min` in CI workflow
- [ ] Add PR-blocking check so coverage regressions fail the build
- [ ] Document threshold policy in `docs/test-strategy.md`
- [ ] Verify pipeline rejects a deliberately lowered threshold

### Milestone 11 — Observability and Performance (Medium-Term)

- [ ] Add structured logging with semantic message templates across services
- [ ] Add optional metrics hooks (stream setup latency, API call duration)
- [ ] Profile and optimize mediainfo cache warm-up path
- [ ] Document observability configuration in README or dedicated doc

### Milestone 12 — Admin UX and Developer Experience (Medium-Term)

- [ ] ConfigPage.html: add profile selector dropdown
- [ ] ConfigPage.html: surface diagnostic results inline
- [ ] Broader client compatibility testing documentation
- [ ] Developer onboarding guide with annotated architecture walkthrough

### Milestone 13 — Live Integration Testing (Medium-Term, addresses T3)

- [ ] Create `docker-compose.test.yml` with TVHeadend + Jellyfin stack
- [ ] Add live HTTP integration tests (channel list, stream start, EPG fetch)
- [ ] Gate live tests behind environment flag (opt-in, not default CI)
- [ ] Document test environment setup in `docs/test-strategy.md`

### Milestone 14 — Plugin Singleton Mitigation (Medium-Term, addresses A3)

- [ ] Audit remaining `Plugin.Instance` usages outside already-injected services
- [ ] Replace direct singleton access with constructor injection where feasible
- [ ] Document any remaining usages that cannot be removed (Jellyfin framework constraint)

### Milestone 15 — Feature Flags and Settings Migration (Longer-Term)

- [ ] Design feature-flag mechanism for experimental features
- [ ] Implement settings versioning / migration strategy in `PluginConfiguration`
- [ ] Add migration tests for config schema changes
- [ ] Document feature-flag usage in README

### Milestone 16 — Extended TVHeadend API Coverage (Longer-Term)

- [ ] Add status API integration (server status, connection info)
- [ ] Add input monitoring API (tuner/adapter status)
- [ ] Add subscription management API (active streams, mux subscriptions)
- [ ] Add recording file playback support via HTTP streaming
- [ ] Add corresponding contract + unit tests for each new API area

### Milestone 17 — Plugin API Versioning (Longer-Term)

- [ ] Define plugin API versioning strategy (semver alignment with Jellyfin SDK)
- [ ] Add version compatibility matrix documentation
- [ ] Implement version negotiation or graceful degradation where applicable

