# Test Strategy

This document defines the testing approach for the Jellyfin TVHeadend API plugin.

## Test Types

### Unit Tests (primary)

- **Framework:** xUnit + FluentAssertions + Moq + AutoFixture.
- **Location:** `Jellyfin.Plugin.TvHeadendApi.Tests/Service/{domain}/`.
- **Scope:** Individual service methods, mapping logic, parsing, validation, cache logic.
- **Mocking:** Mock `IApiClient`, `IUrlBuilder`, `ILogger<T>`, `ILibraryManager`, and other injected interfaces.
- **What must not be mocked:** The system under test itself, pure functions, data models.

### Integration Tests

- **Location:** `Jellyfin.Plugin.TvHeadendApi.Tests/Integration/`.
- **Scope:** Two categories:

#### Contract Tests (`TvHeadendApiContractTests`)

Validate that realistic TVHeadend API JSON payloads deserialize correctly into plugin model types.
Each test uses JSON matching actual TVHeadend 4.3+ API output.

Covered endpoints:
- `/api/channel/grid` — `ChannelGridResponse`
- `/api/epg/events/grid` — `EpgEventsGridResponse`
- `/api/dvr/entry/grid` — `DvrEntryGridResponse`
- `/api/dvr/autorec/grid` — `DvrAutoRecGridResponse`
- `/api/dvr/config/grid` — `DvrConfigGridResponse`
- `/api/channeltag/list` — `ChannelTagResponse`
- `/api/epg/content_type/list` — `EpgContentTypeListResponse`
- `/api/profile/list` — `ProfileListResponse`
- `/api/serverinfo` — `ServerInfoResponse`
- Unknown field resilience

#### Service Integration Tests (`GridFetcherIntegrationTests`)

Exercise the full HTTP→deserialize→model pipeline using `Moq`-based fake `HttpMessageHandler`.

Covered scenarios:
- `GridFetcher.FetchAllAsync` with small total (probe-only path)
- `GridFetcher.FetchAllAsync` with null response
- `GridFetcher.FetchAllAsync` with HTTP error
- `GridFetcher.FetchAllAsync` for DVR entry grid
- `GridFetcher.FetchAllAsync` for DVR autorec grid
- `UrlBuilder` auth parameter composition
- `UrlBuilder` anonymous access
- `UrlBuilder.MaskSensitiveData`

### No End-to-End Tests

- Full Jellyfin + TVHeadend E2E testing is out of scope for automated CI.
- Manual smoke testing is documented in `CONTRIBUTING.md` (Docker Compose workflow).

### Live Integration Tests

Opt-in tests against a real TVHeadend instance. **Run in CI** as a separate job (see `.github/workflows/build-release.yaml`, `integration` job) and also available for local runs.

#### Raw API Tests (`TvHeadendLiveTests`)

- **Location:** `Tests/Integration/TvHeadendLiveTests.cs`
- **Gate:** `[Trait("Category", "LiveIntegration")]` — excluded from default unit-test runs via `--filter "Category!=LiveIntegration"`.
- **Connection:** `TVHEADEND_URL` environment variable (default: `http://localhost:19981`).
- **Test stack:** `docker-compose.test.yml` provides a ready-made TVHeadend + Jellyfin environment.

Covered endpoints:
- `/api/serverinfo` — connectivity and version check
- `/api/channel/grid` — channel grid structure
- `/api/epg/events/grid` — EPG grid structure
- `/api/profile/list` — streaming profile list
- `/api/dvr/entry/grid` — DVR entry grid structure
- Invalid endpoint — error handling

#### Service-Layer Tests (`LiveServiceIntegrationTests`)

- **Location:** `Tests/Integration/LiveServiceIntegrationTests.cs`
- **Gate:** `[Trait("Category", "LiveIntegration")]`
- **Purpose:** Exercise the full plugin service layer (not raw HTTP) against a real TVHeadend instance. Validates that services correctly parse API responses, handle empty grids, and interact with TVHeadend's built-in defaults.

Covered services (15 tests):
- `GuideService` — `GetChannelsAsync`, `GetProgramsAsync`, `GetContentTypesAsync`, `GetChannelTagsAsync`
- `DvrService` — `GetTimersAsync`, `GetSeriesTimersAsync`, `GetRecordingProfileUuidAsync`
- `DiagnosticService` — `DiagnoseAsync` (connectivity OK, score > 0)
- `StatusService` — `GetActivityStatusAsync`, `GetConnectionsAsync`
- `InputMonitorService` — `GetInputStatusAsync`
- `SubscriptionService` — `GetActiveSubscriptionsAsync`
- `ProfileResolver` — `GetProfilesAsync` (≥ 1 profile), `ResolveProfileByNameAsync` (resolves "pass")

How to run:
```bash
# Start test environment
docker compose -f docker/docker-compose.test.yml up -d

# Wait for TVHeadend to become healthy (~30s)
# Run live tests only
dotnet test --filter "Category=LiveIntegration"

# Tear down
docker compose -f docker/docker-compose.test.yml down -v
```

## What Must Be Unit Tested

| Area | Coverage Expectation |
|---|---|
| `OrchestratorService` | All delegation paths |
| `GuideService` | Channel mapping, EPG mapping, content types, tags |
| `DvrService` | Timer CRUD, series timer CRUD, recording profile lookup |
| `MediaSourceService` | Stream URL construction, media source building, cache read/write/validation |
| `LifecycleService` | Stream close, tuner reset |
| `TokenService` | Token generation, validation, retry behavior |
| `DiagnosticService` | Diagnostic report construction |
| `ProfileResolver` / `ProfileContainerResolver` | Profile resolution, container mapping |
| `DefaultProfileService` | Profile creation |
| `StatisticsService` | Session tracking, persistence, retention, cleanup |
| `UrlBuilder` | All URL variants (header auth, URL auth, parameter auth, masking) |
| `ApiClient` | Basic delegation (infrastructure tests) |
| `ProfileMappingHelper` | Codec/container mapping logic |
| `PluginController` | Endpoint routing and service delegation |
| Model DTOs | Serialization round-trips |

## Test Naming Convention

```
{MethodName}_{Scenario}_{ExpectedResult}
```

Examples:
- `GetChannelsAsync_ReturnsChannels_WhenGridHasEntries`
- `BuildUrlWithParameterAuth_AppendsToken_WhenTokenIsSet`
- `TryParseCacheSnapshot_ReturnsFalse_WhenJsonIsEmpty`

Test class names: `{ClassUnderTest}Tests` (e.g., `MediaSourceServiceTests`, `UrlBuilderTests`).

## Test Location Convention

Mirror the source folder structure:

```
Tests/Service/Guide/GuideServiceTests.cs       → tests for Service/Guide/GuideService.cs
Tests/Service/Stream/MediaSourceServiceTests.cs → tests for Service/Stream/MediaSourceService.cs
Tests/Api/PluginControllerTests.cs              → tests for Api/PluginController.cs
Tests/Model/ModelTests.cs                       → DTO serialization tests
```

Exception: `Tests/Service/Core/` contains cross-cutting tests (OrchestratorService, ServiceRegistrator, LifecycleService).

## Mocking Guidelines

1. Mock all external dependencies (IApiClient, IUrlBuilder, ILogger, Jellyfin interfaces).
2. Do not mock the class under test.
3. Do not mock pure functions or static helpers — test them directly.
4. Use `AutoFixture` for generating test data when the exact values are not important.
5. Use `FluentAssertions` for readable assertions.

## Edge Cases and Error Cases

Every service test suite should include:

- Null/empty input handling.
- Missing configuration values.
- Malformed TVHeadend responses (empty JSON, missing fields).
- Cancellation token behavior where applicable.

## Flaky Test Prevention

- No network calls in unit tests.
- No file system access in unit tests (mock file operations or use in-memory alternatives).
- No `Thread.Sleep` or time-dependent assertions.
- No test ordering dependencies.

## Coverage

- **Baseline (2026-04-17):** 530 tests, 92.63% line coverage, 73.9% branch coverage.
- **Tooling:** Coverlet (Cobertura XML) → `irongut/CodeCoverageSummary` in CI.
- **Thresholds (enforced in CI):**
  - **90% minimum** — PR fails if line coverage drops below this (`fail_below_min: true`).
  - **95% good** — coverage badge turns green at or above this level.
- **Policy:**
  - The 90% floor is a hard quality gate. PRs that reduce coverage below this threshold are blocked.
  - The 95% target is aspirational. New code should aim for ≥95% line coverage.
  - Threshold values live in `.github/workflows/build-release.yaml` (`thresholds: '90 95'`).
  - Coverage results are posted as a sticky comment on every PR.
- **Gap:** No per-module coverage enforcement. Overall project-level gate only.

## Minimum Expectations for New Changes

1. Every new service method must have at least one happy-path test and one error/edge-case test.
2. Every bug fix must include a regression test.
3. Mapping logic changes must include tests for the mapping transformation.
4. Configuration changes must verify default values are preserved.

