1. `ProfileProvisioningService` (20-25 tests)
2. `LiveTvGuideService` (15-20 tests)
3. `TvheadendDvrService` (25-35 tests)
4. `LiveStreamSourceService` (15-20 tests)
1. `TvheadendHttpClientFactory` (5-8 tests)
2. `TvheadendApiClient` (10-15 tests)
3. `TvheadendIdNodeService` (8-12 tests)
4. `TvheadendStreamProfileResolver` (10-15 tests)
2. **TvheadendUrlBuilder** edge cases (5-10 tests) - Already 91% coverage
3. **TvheadendJsonHelper** (10-15 tests)
### 15. Service/Streaming/LiveStreamLifecycleService.cs
### 12. Service/Diagnostics/DiagnoseService.cs
### 9. Service/Tvheadend/TvheadendStreamProfileResolver.cs
### 8. Service/Tvheadend/TvheadendIdNodeService.cs
### 7. Service/Tvheadend/TvheadendHttpClientFactory.cs
### 6. Service/Tvheadend/TvheadendApiClient.cs
### 5. Service/Streaming/LiveStreamSourceService.cs
### 4. Service/Profiles/ProfileProvisioningService.cs
### 3. Service/Dvr/TvheadendDvrService.cs
### 2. Service/Guide/LiveTvGuideService.cs
# 🚨 PRIORITIZED TEST ROADMAP FOR 99% COVERAGE

## Summary
- **Current:** 10.66% line coverage (280 / 2,626 lines)
- **Target:** 99% coverage
- **Gap:** 2,320 additional lines needed
- **Estimated Effort:** 225-315 tests | 5-8 weeks

---

## 🔴 TIER 1: CRITICAL (0% Coverage - Highest Impact)

These 15 files **MUST** be tested first. They contain core business logic.

### 1. Service/LiveTvService.cs
**Importance:** ⭐⭐⭐⭐⭐ (Main plugin interface)
- Implements `ILiveTvService` for Jellyfin
- Entry point for all LiveTV operations
- **Lines:** ~80 lines of code
- **Tests needed:** 8-12 tests
- **Key methods to test:**
  - `GetChannelsAsync()`
  - `GetProgramsAsync()`
  - `GetTimersAsync()` / `GetSeriesTimersAsync()`
  - `CreateTimerAsync()` / `CreateSeriesTimerAsync()`
  - `UpdateTimerAsync()` / `UpdateSeriesTimerAsync()`
  - `CancelTimerAsync()` / `CancelSeriesTimerAsync()`

### 2. Service/Guide/GuideService.cs
**Importance:** ⭐⭐⭐⭐⭐ (Channel & Program data)
- Fetches channels and programs from TVHeadend
- **Lines:** ~150 lines
- **Tests needed:** 15-20 tests
- **Key methods to test:**
  - `GetChannelsAsync()` - channel list retrieval
  - `GetProgramsAsync()` - EPG data
  - `GetChannelTagsAsync()` - channel categories
  - `GetContentTypesAsync()` - program content types

### 3. Service/Dvr/DvrService.cs
**Importance:** ⭐⭐⭐⭐⭐ (Recording management)
- Manages DVR timers and recordings
- Split across two partial files (.SingleTimers.cs, .SeriesTimers.cs)
- **Lines:** ~250 lines combined
- **Tests needed:** 25-35 tests
- **Key methods to test:**
  - All timer CRUD operations
  - Series timer management
  - Recording queries

### 4. Service/Profile/ProvisioningService.cs
**Importance:** ⭐⭐⭐⭐ (Codec profiles)
- Sets up streaming profiles in TVHeadend
- Complex async operations
- **Lines:** ~200 lines
- **Tests needed:** 20-25 tests
- **Key methods to test:**
  - `CreateProfileAsync()`
  - `GenerateAuthTokenAsync()`
  - `GetStreamingProfileByNameAsync()`
  - Profile validation

### 5. Service/Stream/SourceService.cs
**Importance:** ⭐⭐⭐⭐ (Stream delivery)
- Manages streaming URLs and media sources
- **Lines:** ~180 lines
- **Tests needed:** 15-20 tests
- **Key methods to test:**
  - `GetChannelStreamMediaSourcesAsync()`
  - `BuildMediaSourceInfoAsync()`
  - Stream lifecycle

---

## 🟠 TIER 2: HIGH PRIORITY (0% Coverage - Core Infrastructure)

### 6. Service/Tvheadend/TvheadendApiClient.cs
**Importance:** ⭐⭐⭐⭐ (HTTP communication)
- All HTTP requests to TVHeadend
- **Lines:** ~65 lines
- **Tests needed:** 10-15 tests
- **Key methods to test:**
  - `GetCurrentConfiguration()`
  - `BuildHttpClient()`
  - `GetStringAsync()` / `PostFormAsync()`

### 7. Service/Tvheadend/TvheadendHttpClientFactory.cs
**Importance:** ⭐⭐⭐ (HTTP setup)
- Creates HttpClient with auth headers
- **Lines:** ~35 lines
- **Tests needed:** 5-8 tests
- **Key test scenarios:**
  - Basic auth (username/password)
  - Digest auth
  - HTTP/HTTPS protocol selection

### 8. Service/Tvheadend/TvheadendIdNodeService.cs
**Importance:** ⭐⭐⭐ (Node navigation)
- Loads DVR configs and nodes
- **Lines:** ~65 lines
- **Tests needed:** 8-12 tests
- **Key methods to test:**
  - `LoadIdNodeByUuidAsync()`
  - `LoadDvrConfigsAsync()`
  - JSON response parsing

### 9. Service/Tvheadend/TvheadendStreamProfileResolver.cs
**Importance:** ⭐⭐⭐ (Profile resolution)
- Resolves encoding profiles
- **Lines:** ~110 lines
- **Tests needed:** 10-15 tests
- **Key methods to test:**
  - `GetProfilesAsync()`
  - `GetProfileDetailsByUuidAsync()`

### 10. Api/TvHeadendApiController.cs
**Importance:** ⭐⭐⭐⭐ (REST endpoints)
- Currently only 6% coverage!
- **Lines:** ~80 lines
- **Tests needed:** 12-18 tests (extends existing 3)
- **Missing methods to test:**
  - `Diagnose()` endpoint
  - `CreateProfile()` endpoint
  - `GenerateAuthToken()` endpoint
  - `ResetToDefaults()` endpoint
  - Error handling

---

## 🟡 TIER 3: MEDIUM PRIORITY (0% Coverage - Support)

### 11. Service/Images/ImageProxyService.cs & TvheadendImageGateway.cs
**Importance:** ⭐⭐⭐ (Image handling)
- **Lines:** ~60 lines combined
- **Tests needed:** 8-10 tests
- **Key methods:** Proxy image retrieval

### 12. Service/Diagnostic/DiagnosticService.cs
**Importance:** ⭐⭐⭐ (System diagnostics)
- **Lines:** ~150 lines
- **Tests needed:** 12-15 tests
- **Key methods:** System compatibility checks

### 13. Plugin.cs & ServiceRegistrator.cs
**Importance:** ⭐⭐⭐ (Plugin lifecycle)
- **Lines:** ~65 lines combined
- **Tests needed:** 8-10 tests
- **Key methods:** Plugin initialization, service registration

### 14. Configuration/PluginConfiguration.cs
**Importance:** ⭐⭐ (Settings model)
- **Lines:** ~40 lines
- **Tests needed:** 5-8 tests
- **Key tests:** Configuration validation, defaults

### 15. Service/Stream/LifecycleService.cs
**Importance:** ⭐⭐ (Stream cleanup)
- **Lines:** ~50 lines
- **Tests needed:** 5-8 tests

---

## 🟢 TIER 4: DATA MODELS (Currently 0%)

All model/DTO classes need serialization tests:

**Files (18 total):** ~200 lines combined
**Tests needed:** 20-30 tests

- ✓ Verify JSON serialization roundtrip
- ✓ Test null handling
- ✓ Test default values
- ✓ Test edge cases

**Example classes:**
- `TvhApiChannelGridEntry` / `Response`
- `TvhApiDvrEntryGridEntry` / `Response`
- `ProfileDetectionResult`
- `DiagnoseResult`
- `AuthTokenGenerationResult`

---

## 📊 Implementation Order (Recommended)

### Week 1-2: Foundation (35-45 tests)
1. **Model tests** (20-30 tests) - Easy wins, build confidence
2. **TvheadendUrlBuilder** edge cases (5-10 tests) - Already 91% coverage
3. **TvheadendJsonHelper** (10-15 tests)

### Week 2-3: Infrastructure (40-50 tests)
1. `TvheadendHttpClientFactory` (5-8 tests)
2. `TvheadendApiClient` (10-15 tests)
3. `TvheadendIdNodeService` (8-12 tests)
4. `TvheadendStreamProfileResolver` (10-15 tests)

### Week 3-5: Core Services (80-120 tests)
1. `ProvisioningService` (20-25 tests)
2. `GuideService` (15-20 tests)
3. `DvrService` (25-35 tests)
4. `SourceService` (15-20 tests)
5. `LifecycleService` (5-8 tests)

### Week 5-6: Integration & Controllers (50-80 tests)
1. Expand `TvHeadendApiController` (12-18 tests)
2. `ImageProxyService` (8-10 tests)
3. `DiagnosticService` (12-15 tests)
4. `Plugin` & `ServiceRegistrator` (8-10 tests)
5. Integration workflows (10-17 tests)

### Week 6-8: Polish & Edge Cases (40-80 tests)
1. Error scenarios
2. Async/await edge cases
3. Null-safety tests
4. Configuration variations

---

## 🛠️ Test Infrastructure Setup

### 1. Install Required Packages
```bash
dotnet add Jellyfin.Plugin.TvHeadendApi.Tests package Moq 4.18.5
dotnet add Jellyfin.Plugin.TvHeadendApi.Tests package AutoFixture 4.18.1
dotnet add Jellyfin.Plugin.TvHeadendApi.Tests package FluentAssertions 6.12.1
```

### 2. Create Test Fixtures
Create `Fixtures/` directory with:
- `TvHeadendApiClientFixture.cs` - Mock HTTP responses
- `PluginConfigurationFixture.cs` - Test configurations
- `JsonDataFixture.cs` - Sample JSON responses

### 3. Create Builders/Factories
- `TvheadendApiClientBuilder.cs` - Builder for test setup
- `PluginConfigurationBuilder.cs` - Config builder

### 4. Update Project File
```xml
<ItemGroup>
  <PackageReference Include="Moq" Version="4.18.5" />
  <PackageReference Include="AutoFixture" Version="4.18.1" />
  <PackageReference Include="FluentAssertions" Version="6.12.1" />
</ItemGroup>
```

---

## 📋 Coverage Targets by Phase

| Phase | Target % | Lines | Effort | Timeline |
|-------|----------|-------|--------|----------|
| After Tier 4 (Models) | 15-20% | 400-520 | 1 week | Week 1 |
| After Tier 3 (Infrastructure) | 35-45% | 900-1180 | 2 weeks | Week 3 |
| After Tier 2 (Core Services) | 70-80% | 1830-2090 | 3 weeks | Week 6 |
| After Tier 1 (Controllers) | 90-95% | 2350-2500 | 1 week | Week 7 |
| Final Polish | 99%+ | 2600+ | 1 week | Week 8 |

---

## ✅ Completion Checklist

### Per Test Suite
- [ ] All public methods tested
- [ ] Happy path covered
- [ ] Error scenarios covered
- [ ] Async/await patterns tested
- [ ] Null-safety verified
- [ ] Configuration variations tested

### Before Merging
- [ ] `dotnet test` passes 100%
- [ ] Coverage report > 99%
- [ ] No untested branches
- [ ] Documentation updated

### Validation Command
```bash
dotnet test Jellyfin.Plugin.TvHeadendApi.Tests/Jellyfin.Plugin.TvHeadendApi.Tests.csproj `
  --collect:"XPlat Code Coverage" `
  --results-directory TestResults `
  --logger "console;verbosity=detailed"
```

---

## 📞 Questions & Notes

1. **Should we test private methods?** No - focus on public contracts
2. **Mock depth?** Mock external dependencies (HTTP, file I/O), test logic in isolation
3. **Integration tests?** Add after reaching 90%+ line coverage
4. **Performance?** Tests should complete in < 10 seconds total

---

**Generated:** 2026-04-08  
**Status:** Ready to implement  
**Next Step:** Install Moq and create first fixture

