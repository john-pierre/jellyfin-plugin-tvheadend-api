# Repository Assessment

Initial assessment date: 2026-04-16
Baseline: build 0 warnings / 0 errors, 230 tests passing.

Final state (Milestone 7 complete): build 0 warnings / 0 errors, 268 tests passing.
All milestones 0–7 delivered. See `docs/ROADMAP.md` for details.

## 1. Architecture Inventory

### Layers

| Layer | Location | Purpose |
|---|---|---|
| Plugin entry point | `Plugin.cs`, `ServiceRegistrator.cs` | Jellyfin plugin registration, DI wiring |
| API controller | `Api/PluginController.cs` | Admin-facing REST endpoints (diagnostics, profile creation, token generation, statistics) |
| Orchestrator | `Service/OrchestratorService.cs` | Thin `ILiveTvService` facade — delegates to domain services |
| Domain services | `Service/{Auth,Diagnostic,Dvr,Guide,Profile,Statistics,Stream}/` | Focused domain logic per concern |
| Helper / infra | `Service/Helper/` | HTTP client, URL building, grid pagination |
| Models / DTOs | `Model/{Auth,Diagnostic,Dvr,Guide,Profile,Statistics}/` | TVHeadend JSON response DTOs and result objects |
| Configuration | `Configuration/PluginConfiguration.cs`, `ConfigPage.html` | Runtime config model and admin UI |

### Dependency Direction

```
PluginController ──> Domain services ──> Helper/IApiClient ──> TVHeadend HTTP
OrchestratorService ──> Guide/Dvr/Stream services
Stream services ──> Profile services
```

No circular dependencies detected. All domain services depend on `IApiClient` + `IUrlBuilder` from Helper.

### Integration Points

- **Jellyfin**: `ILiveTvService`, `IPluginServiceRegistrator`, `IHasWebPages`, `IHostedService` (StatisticsService), `ISessionManager` (playback events).
- **TVHeadend**: HTTP/JSON API only (grid endpoints, idnode endpoints, stream URLs).

## 2. Structural Findings

### Strengths

1. **Clean orchestrator pattern** — `OrchestratorService` is a thin delegator, no mixed logic.
2. **Domain-organized services** — Auth, Dvr, Guide, Profile, Stream, Statistics, Diagnostic are well-separated.
3. **Model/Service mirror** — Model folders match service folders 1:1.
4. **Nullable enabled**, `TreatWarningsAsErrors` enabled, StyleCop + analyzers active.
5. **DvrService uses partial classes** for SeriesTimer/SingleTimer — manageable file sizes.
6. **230 tests** with xUnit, Moq, FluentAssertions, AutoFixture, Coverlet.
7. **CI pipeline** with Release Please, PR title checks, test execution, artifact packaging.
8. **Good README** — user-focused with Quick Start, compatibility matrix, troubleshooting.
9. **Existing naming convention document** and agent instructions.

### Issues and Technical Debt

#### Architecture / Design

| ID | Severity | Finding |
|---|---|---|
| A1 | Low | `UrlBuilder.cs` contains **two types** (`UrlHelper` static class + `UrlBuilder` class). Violates file == type rule from `NAMING_CONVENTIONS.md`. |
| A2 | Low | `ApiClient` delegates to static `UrlHelper` methods but also has its own `BuildUrl`/`GetBaseUrl`/`GetWebRoot`. `IApiClient` mixes HTTP concerns with URL/config concerns — could be split. |
| A3 | Info | `Plugin.Instance` static singleton pattern — standard for Jellyfin plugins but creates test difficulty. Already mitigated via constructor injection of `Func<string?>` in `MediaSourceService`. |
| A4 | Low | `JsonSerializerOptions` duplicated as `static readonly` in multiple services (GuideService, DvrService, etc.). Could be a shared constant. |
| A5 | Low | `#pragma warning disable CS1591` in `OrchestratorService.cs` suppresses XML doc warnings for the whole file. |

#### Naming Inconsistencies

| ID | Severity | Finding |
|---|---|---|
| N1 | Medium | `AGENTS.md` and `README.md` reference `Api/TvHeadendApiController.cs` but the actual file is `Api/PluginController.cs`. |
| N2 | Low | `NAMING_CONVENTIONS.md` tree shows `Service/Profile/ProvisioningService.cs` but actual file is `DefaultProfileService.cs`. |
| N3 | Low | `NAMING_CONVENTIONS.md` tree shows `Service/Helper/IdNodeService.cs` but actual file is `IdNodeValueHelper.cs`. |
| N4 | Low | `NAMING_CONVENTIONS.md` tree shows `Service/Stream/StreamUrlBuilder.cs` — no such file exists; URL building is in `Helper/UrlBuilder.cs`. |

#### Tests

| ID | Severity | Finding |
|---|---|---|
| T1 | Medium | No test for `StatisticsService` (playback event tracking, persistence, retention). |
| T2 | Low | Test folder `Service/Core/` does not mirror source folder structure (no `Service/Core/` in source). |
| T3 | Info | No integration tests or contract tests against TVHeadend API shapes. |
| T4 | Info | No code coverage threshold enforced in CI. |

#### Documentation

| ID | Severity | Finding |
|---|---|---|
| D1 | Medium | `AGENTS.md` line "Api/TvHeadendApiController.cs" — stale reference. |
| D2 | Medium | `README.md` line "Api/TvHeadendApiController.cs" — stale reference. |
| D3 | Low | No architecture overview document (this assessment fills the gap). |
| D4 | Low | No test strategy document. |
| D5 | Low | `docs/ai/README.md` references "README.md section Naming and Structure Conventions" — that section does not exist in the root README. |

#### Pipeline

| ID | Severity | Finding |
|---|---|---|
| P1 | Low | No formatting/lint step (StyleCop is build-integrated, so this is already enforced — acceptable). |
| P2 | Low | No code coverage threshold or report published to PR. |
| P3 | Info | `pr-title-check.yaml` uses `pull_request_target` — fine but requires awareness of fork security. |

#### Code Quality

| ID | Severity | Finding |
|---|---|---|
| Q1 | Low | Duplicated `JsonSerializerOptions` instances across services. |
| Q2 | ~~Low~~ | ~~`HttpClient` created via `using var httpClient = _tvheadendApiClient.BuildHttpClient(config)` in every call — creates new HttpClient per request.~~ ✅ Resolved: migrated to `IHttpClientFactory` with named clients in Milestone 8. |
| Q3 | Info | `GetStreamAsync` in `ApiClient` reads full response into `MemoryStream` — fine for current use (images) but not for large streams. |
| Q4 | Info | No retry/resilience logic for TVHeadend API calls. |

## 3. Quick Wins vs Larger Structural Issues

### Quick Wins (can fix immediately)

1. Fix stale file references in `AGENTS.md`, `README.md`, `.github/copilot-instructions.md`.
2. Fix `NAMING_CONVENTIONS.md` examples to match actual file names.
3. Remove `#pragma warning disable CS1591` from `OrchestratorService.cs` by adding XML docs.
4. Extract `UrlHelper` into its own file to satisfy file == type rule.

### Medium Effort

5. Create architecture overview document.
6. Create test strategy document.
7. Add `StatisticsService` tests.
8. Shared `JsonSerializerOptions` constant.
9. Update Copilot instructions to be accurate routing references.

### Larger Structural (future milestones)

10. Consider `IHttpClientFactory` integration.
11. Retry/resilience layer for TVHeadend calls.
12. Code coverage thresholds in CI.
13. Integration test scaffolding.

## 4. Risk List

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Stale docs mislead agents/contributors | High | Medium | Fix immediately (Quick Win 1-2) |
| Missing StatisticsService tests hide regressions | Medium | Medium | Add tests in Milestone 3 |
| HttpClient-per-request overhead | Low | Low | Monitor; consider `IHttpClientFactory` later |
| No resilience on TVHeadend calls | Medium | Low | Add retry policy in future milestone |

