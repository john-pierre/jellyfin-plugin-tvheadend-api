# Testing Standards

## Frameworks

| Package | Purpose |
|---------|---------|
| xUnit | Test runner |
| FluentAssertions | Readable assertions |
| Moq | Interface mocking |
| AutoFixture | Test data generation |
| WireMock.Net | HTTP mocking for integration tests |
| Microsoft.EntityFrameworkCore.InMemory | In-memory EF Core for database tests |

## Test Naming

```
{MethodName}_{Scenario}_{ExpectedResult}
```

Examples:
- `GetChannelsAsync_ReturnsChannels_WhenGridHasEntries`
- `BuildUrlWithParameterAuth_AppendsToken_WhenTokenIsSet`
- `TryParseCacheSnapshot_ReturnsFalse_WhenJsonIsEmpty`

Test class names: `{ClassUnderTest}Tests` (e.g., `MediaSourceServiceTests`).

## Test Location

Mirror the source folder structure under `Tests/`:

```
Tests/Service/Guide/GuideServiceTests.cs       -> Service/Guide/GuideService.cs
Tests/Service/Stream/MediaSourceServiceTests.cs -> Service/Stream/MediaSourceService.cs
Tests/Api/PluginControllerTests.cs              -> Api/PluginController.cs
Tests/Model/ModelTests.cs                       -> DTO serialization tests
```

Exception: `Tests/Service/Core/` contains cross-cutting tests (OrchestratorService, ServiceRegistrator, LifecycleService).

## Running Tests

```bash
# Unit tests only (default CI gate)
dotnet test Jellyfin.Plugin.TvHeadendApi.Tests/Jellyfin.Plugin.TvHeadendApi.Tests.csproj -c Release --no-build --filter "Category!=LiveIntegration"

# Live integration tests (requires Docker test stack)
dotnet test --filter "Category=LiveIntegration"
```

## Category Traits

| Trait | Gate |
|-------|------|
| `[Trait("Category", "LiveIntegration")]` | Excluded from default runs, requires Docker stack |
| (no trait) | Runs by default in CI |

## Coverage

- **1047+ tests**, 92.63% line coverage (as of baseline)
- **90% minimum** — CI hard gate, PR fails if coverage drops below
- **95% target** — aspirational for new code
- Tooling: Coverlet (Cobertura XML) with `CodeCoverageSummary` in CI
- Coverage results posted as sticky PR comment

## Mocking Guidelines

1. Mock all external dependencies: `IApiClient`, `IUrlBuilder`, `ILogger<T>`, Jellyfin interfaces
2. Never mock the class under test
3. Never mock pure functions or static helpers — test them directly
4. Use `AutoFixture` when exact values don't matter
5. Use `FluentAssertions` for all assertions

## What Must Be Tested

Every service change needs test coverage:

- Happy-path test for each new method
- At least one error/edge-case test per method
- Null/empty input handling
- Missing configuration values
- Malformed TVHeadend responses (empty JSON, missing fields)
- Cancellation token behavior where applicable
- Bug fixes must include a regression test
- Mapping logic changes must test the transformation
- Configuration changes must verify default values are preserved

## Flaky Test Prevention

- No network calls in unit tests
- No file system access in unit tests
- No `Thread.Sleep` or time-dependent assertions
- No test ordering dependencies

## Integration Test Types

### Contract Tests (`TvHeadendApiContractTests`)
Validate TVHeadend API JSON payloads deserialize correctly. Cover all major endpoints:
`/api/channel/grid`, `/api/epg/events/grid`, `/api/dvr/entry/grid`, `/api/dvr/autorec/grid`, `/api/dvr/config/grid`, `/api/channeltag/list`, `/api/epg/content_type/list`, `/api/profile/list`, `/api/serverinfo`.

### Service Integration Tests
Exercise full HTTP -> deserialize -> model pipeline using mock `HttpMessageHandler`.

### Live Integration Tests
Against real TVHeadend via Docker (`docker-compose.test.yaml`). Cover GuideService, DvrService, DiagnosticService, StatusService, InputMonitorService, SubscriptionService, ProfileResolver.

## Validation Before Committing

```bash
dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release
dotnet test Jellyfin.Plugin.TvHeadendApi.Tests/Jellyfin.Plugin.TvHeadendApi.Tests.csproj -c Release --no-build --filter "Category!=LiveIntegration"
```

Both must pass with 0 warnings, 0 errors, 0 test failures.
