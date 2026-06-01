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

### End-to-End Tests (Docker Compose)

Full E2E tests run against a real TVHeadend + Jellyfin + IPTV simulator stack via `docker-compose.test.yaml`. These tests exercise the complete plugin surface including channels, EPG, streams, profiles, auth tokens, DVR, tuner status, subscriptions, and diagnostics.

#### Prerequisites

| Tool | Version | Notes |
|------|---------|-------|
| Docker | latest | With Compose V2 (`docker compose`) |
| .NET SDK | 8.0+ | For running the test project |
| ~2 GB disk | — | Docker images + volumes |
| Ports free | 18888, 19981, 19982, 18096 | IPTV simulator, TVHeadend HTTP/HTSP, Jellyfin |

#### Architecture

The test stack consists of four containers:

```
┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐
│ iptv-simulator   │────▶│ tvheadend        │────▶│ jellyfin         │
│ (M3U + TS + EPG) │     │ (API on :9981)   │     │ (HTTP on :8096)  │
│ Port: 18888      │     │ Port: 19981      │     │ Port: 18096      │
└─────────────────┘     └─────────────────┘     └─────────────────┘
                              ▲
                              │
                        ┌─────────────────┐
                        │ tvheadend-       │
                        │ bootstrap        │
                        │ (runs once, exit)│
                        └─────────────────┘
```

- **iptv-simulator** — Python HTTP server generating live MPEG-TS streams (ffmpeg testsrc2 + clock overlay), XMLTV EPG, M3U playlist, and channel logos/thumbnails. Serves 5 test channels in groups: News, Entertainment, Sports, Documentary, Music.
- **tvheadend** — TVHeadend 4.3+ with `-C` flag (no initial auth). After bootstrap, all access requires `testuser`/`testpass`.
- **tvheadend-bootstrap** — Runs once after TVHeadend is healthy. Creates IPTV network, triggers mux scan, maps channels, configures EPG grabber, creates test user/profiles/DVR config, then exits.
- **jellyfin** — Jellyfin instance with the plugin installed for full integration testing.

#### Running E2E Tests

**Option A: Automated script (recommended)**

```powershell
# Windows PowerShell
.\docker\run-e2e-tests.ps1
```

```bash
# Linux / CI
docker/run-e2e-tests.sh
```

Both scripts:
1. Tear down any previous stack (clean volumes — both the `tvh-test` project and the compose file's own project name, to avoid `container_name` reuse conflicts)
2. Build and start all containers (`docker compose up -d --build --wait`)
3. Wait for bootstrap completion (max 120s); a non-zero bootstrap exit fails the run
4. Run live integration tests (`--filter "Category=LiveIntegration"`)
5. Output TRX results to `TestResults/e2e-results.trx`
6. **Propagate the `dotnet test` exit code** so CI actually gates on the E2E outcome, and always tear the stack down (clean volumes) in a `finally`/`trap`.

**Connection environment variables.** The plugin runs *inside* the Jellyfin container, so it reaches
TVHeadend via the Docker service name; direct service-layer tests run from the host and use the
published port. The scripts set both:

| Variable | Value | Used by |
|---|---|---|
| `TVH_HOST` / `TVH_PORT` | `tvheadend` / `9981` | Plugin config written into Jellyfin (container-internal) |
| `TVHEADEND_URL` | `http://localhost:19981` | Direct service/raw-API tests (from the host) |
| `JELLYFIN_URL` | `http://localhost:18096` | Jellyfin HTTP API tests |
| `TVH_USER` / `TVH_PASS` | `testuser` / `testpass` | Bootstrap-created TVHeadend account |

> **Note:** Jellyfin only ingests Live-TV channels into its library on a guide refresh. After a fresh
> Jellyfin start, trigger the `RefreshGuide` scheduled task (or wait for the scheduled run) before
> querying `/LiveTv/Channels` or `PlaybackInfo`.

**Option B: Manual steps**

```bash
# 1. Start the stack (builds images on first run)
docker compose -f docker/docker-compose.test.yaml -p tvh-test up -d --build

# 2. Wait for TVHeadend healthcheck + bootstrap completion (~30-60s)
docker logs -f tvheadend-bootstrap-test
# Wait until you see: "Bootstrap complete."

# 3. Run live integration tests
dotnet test Jellyfin.Plugin.TvHeadendApi.Tests/Jellyfin.Plugin.TvHeadendApi.Tests.csproj \
  -c Release --filter "Category=LiveIntegration"

# 4. Tear down (removes volumes for clean state)
docker compose -f docker/docker-compose.test.yaml -p tvh-test down -v
```

#### Teardown and Cleanup

```bash
# Full cleanup (removes containers, networks, and volumes)
docker compose -f docker/docker-compose.test.yaml -p tvh-test down -v --remove-orphans
```

Always use `-v` to remove volumes. TVHeadend persists configuration in a Docker volume — leaving it behind can cause subsequent bootstrap runs to fail or produce inconsistent state.

#### Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| Bootstrap times out | TVHeadend mux scan slow or IPTV simulator not ready | Check `docker logs iptv-simulator-test` and `docker logs tvheadend-test`. Increase `MAX_WAIT` env var on bootstrap. |
| 0 channels after bootstrap | Bouquet auto-mapping failed | Bootstrap falls back to manual service mapper. Check `docker logs tvheadend-bootstrap-test` for mapper output. |
| EPG empty | XMLTV grabber module not found | TVHeadend build may lack internal XMLTV support. Check bootstrap logs for "XMLTV URL grabber module not found". |
| Port conflicts | Other services using 18888/19981/18096 | Stop conflicting services or change ports in `docker-compose.test.yaml`. |
| Tests fail with connection refused | Stack not fully started | Wait for all healthchecks to pass: `docker compose -f docker/docker-compose.test.yaml ps` should show all services as "healthy". |
| Auth failures (401) | Bootstrap user creation failed | Check `docker logs tvheadend-bootstrap-test`. The default `-C` user is removed after bootstrap creates `testuser`. |

#### IPTV Simulator

The IPTV simulator (`docker/iptv-simulator/`) is a self-contained Python HTTP server that provides deterministic test content for TVHeadend.

**Endpoints:**

| Route | Content |
|-------|---------|
| `GET /health` | Health check (returns `OK`) |
| `GET /playlist.m3u` | M3U playlist with 5 test channels |
| `GET /epg.xml` | XMLTV EPG with 48 hours of 1-hour programmes per channel |
| `GET /logo{n}.png` | Dynamically generated 256×256 channel logo (colored square with number) |
| `GET /thumb/{id}/{hour}.png` | Dynamically generated 320×180 programme thumbnail |
| `GET /stream/ch{n}.ts` | Live MPEG-TS stream via ffmpeg (testsrc2 + clock overlay + 880 Hz tick audio) |

**Channels:**

| ID | Name | Group | Color |
|----|------|-------|-------|
| test-ch1 | Test Channel 1 | News | Red |
| test-ch2 | Test Channel 2 | Entertainment | Blue |
| test-ch3 | Test Channel 3 | Sports | Green |
| test-ch4 | Test Channel 4 | Documentary | Orange |
| test-ch5 | Test Channel 5 | Music | Purple |

**Adding channels:**

Edit the `CHANNELS` list in `docker/iptv-simulator/server.py`:

```python
CHANNELS = [
    {"id": "test-ch1", "name": "Test Channel 1", "group": "News", "color": (180, 30, 30)},
    # Add new channels here:
    {"id": "test-ch6", "name": "My Channel", "group": "Kids", "color": (255, 180, 0)},
]
```

The playlist, EPG, logos, thumbnails, and streams are all generated dynamically from this list. No other files need editing.

**Modifying EPG:**

The EPG is generated in `make_epg()` in `server.py`. By default, each channel gets 48 one-hour slots starting from midnight UTC today. To change programme duration, genre, or descriptions, edit the loop in `make_epg()`.

**Extending streams:**

Streams are generated live by ffmpeg. To change resolution, bitrate, or overlay content, edit the `stream_channel()` function in `server.py`. Key parameters:
- Video: `testsrc2=size=1280x720:rate=25`, `libx264 800k`
- Audio: `aac 64k 44100 Hz`, 880 Hz tick pattern
- Container: MPEG-TS

The `BASE_URL` environment variable controls the URL prefix in M3U/EPG. Inside Docker Compose, it defaults to `http://iptv-simulator` (the service name).

### Live Integration Tests

Opt-in tests against a real TVHeadend instance. **Run in CI** as a separate job (see `.github/workflows/build-release.yaml`, `integration` job) and also available for local runs.

#### Raw API Tests (`TvHeadendLiveTests`)

- **Location:** `Tests/Integration/TvHeadendLiveTests.cs`
- **Gate:** `[Trait("Category", "LiveIntegration")]` — excluded from default unit-test runs via `--filter "Category!=LiveIntegration"`.
- **Connection:** `TVHEADEND_URL` environment variable (default: `http://localhost:19981`).
- **Test stack:** `docker-compose.test.yaml` provides a ready-made TVHeadend + Jellyfin environment.

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
docker compose -f docker/docker-compose.test.yaml up -d

# Wait for TVHeadend to become healthy (~30s)
# Run live tests only
dotnet test --filter "Category=LiveIntegration"

# Tear down
docker compose -f docker/docker-compose.test.yaml down -v
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

