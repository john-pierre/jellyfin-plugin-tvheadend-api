- `Service/Streaming/LiveStreamSourceService.cs` - Stream management
- `Service/Streaming/LiveStreamLifecycleService.cs` - Stream lifecycle
- `Service/Profiles/ProfileProvisioningService.cs` - Codec profile setup
- `Service/Diagnostics/DiagnoseService.cs` - System diagnostics
# Unit Test Coverage Analysis Report

**Date:** 2026-04-08  
**Project:** Jellyfin TVHeadend API Plugin  
**Framework:** xUnit + Coverlet  

---

## 📊 Executive Summary

### Current Coverage Status
- **Line Coverage:** 10.66% (280 / 2,626 lines)
- **Branch Coverage:** 7.62% (69 / 906 branches)
- **Files Covered:** 5 out of 62 C# files (8%)
- **Test Count:** 16 passing tests

### Target: 99% Coverage
- **Required Lines:** 2,600 lines (need 2,320 more)
- **Required Branches:** 897 branches (need 828 more)
- **Coverage Gap:** 88.34% for lines | 91.38% for branches

---

## 🎯 Priority Areas for Testing

### CRITICAL PRIORITY (0% Coverage)
These are the main business logic files with **zero test coverage**:

#### 1. **Service Layer** (Core Business Logic)
- `Service/LiveTvService.cs` - Main ILiveTvService implementation
- `Service/Guide/LiveTvGuideService.cs` - Channel/Program guide integration
- `Service/Dvr/TvheadendDvrService.cs` - DVR recording management
- `Service/Guide/GuideService.cs` - Channel/Program guide integration
- `Service/Dvr/DvrService.cs` - DVR recording management
  - `.SingleTimers.cs` partial
  - `.SeriesTimers.cs` partial
- `Service/Stream/SourceService.cs` - Stream management
- `Service/Stream/LifecycleService.cs` - Stream lifecycle
- `Service/Profile/ProvisioningService.cs` - Codec profile setup
- `Service/Diagnostic/DiagnosticService.cs` - System diagnostics

#### 2. **API Controller** (HTTP Endpoints)
- `Api/TvHeadendApiController.cs` - REST endpoints
  - `Diagnose()` method
  - `CreateProfile()` method
  - `GenerateAuthToken()` method

#### 3. **Core Infrastructure**
- `Service/Tvheadend/TvheadendApiClient.cs` - TVHeadend HTTP client
- `Service/Tvheadend/TvheadendHttpClientFactory.cs` - HTTP factory
- `Service/Tvheadend/TvheadendIdNodeService.cs` - Node loading
- `Service/Tvheadend/TvheadendStreamProfileResolver.cs` - Profile resolution
- `Service/Images/ImageProxyService.cs` - Image proxy
- `Service/Images/TvheadendImageGateway.cs` - Image gateway

#### 4. **Configuration & Plugin**
- `Plugin.cs` (5% coverage - needs improvement)
- `ServiceRegistrator.cs` (0% coverage)
- `Configuration/PluginConfiguration.cs` (0% coverage)

#### 5. **Data Models** (DTO Classes)
All model classes in `Model/` folder have **0% coverage**:
- `TvhApiChannelGridEntry.cs`
- `TvhApiChannelGridResponse.cs`
- `TvhApiChannelTagEntry.cs`
- `TvhApiChannelTagResponse.cs`
- `TvhApiDvrAutoRecGridEntry.cs`
- `TvhApiDvrAutoRecGridResponse.cs`
- `TvhApiDvrConfigGridEntry.cs`
- `TvhApiDvrConfigGridResponse.cs`
- `TvhApiDvrEntryGridEntry.cs`
- `TvhApiDvrEntryGridResponse.cs`
- `TvhApiEpgContentTypeListEntry.cs`
- `TvhApiEpgContentTypeListResponse.cs`
- `TvhApiEpgEventsGridEntry.cs`
- `TvhApiEpgEventsGridResponse.cs`
- `ProfileDetectionResult.cs`
- `DiagnoseResult.cs`
- `AuthTokenGenerationResult.cs`
- `DiagnoseCheck.cs`

---

## ✅ Currently Covered (Low Coverage Areas)

### Partial Coverage (3-7%)
1. **TvheadendUrlBuilder.cs** - 91.17% ✓ (Good!)
2. **TvheadendJsonHelper.cs** - 34.22%
3. **LiveStreamProfileContainerResolver.cs** - 7%
4. **TvheadendApiController.cs** - 6%
5. **ImageProxyService.cs** - 3%

### Minimal Coverage (0-2%)
- TvheadendJsonReader.cs
- TvheadendStreamProfileDetails.cs
- TvheadendStreamProfileReference.cs

---

## 📋 Test Coverage Breakdown by Module

| Module | Status | Comments |
|--------|--------|----------|
| **Guide Service** | ❌ 0% | Needs 50+ tests for channel/program queries |
| **DVR Service** | ❌ 0% | Needs 40+ tests for timer management |
| **Streaming** | ❌ 0% | Needs 30+ tests for stream lifecycle |
| **Profiles** | ❌ 0% | Needs 20+ tests for codec profile setup |
| **TVHeadend API Client** | ❌ 0% | Needs 25+ tests for HTTP requests |
| **Diagnostics** | ❌ 0% | Needs 15+ tests for system checks |
| **API Controller** | ⚠️ 6% | Partially covered, needs 10+ additional tests |
| **URL Builder** | ✅ 91% | Good coverage, minor gaps remain |

---

## 🔧 Estimated Test Count Required

### To Reach 99% Coverage
Based on complexity and branching:

- **Service Layer Tests:** 150-200 tests
- **API Controller Tests:** 15-20 tests (currently: 1 test)
- **Integration Tests:** 30-50 tests
- **Model/DTO Tests:** 20-30 tests
- **Helper/Utility Tests:** 10-15 tests

**Total Estimated:** 225-315 additional tests needed

### Current Test Files
- `TvHeadendApiControllerTests.cs` - 3 tests
- `ImageProxyServiceTests.cs` - Tests exist
- `LiveStreamProfileContainerResolverTests.cs` - Tests exist
- `TvheadendJsonReaderTests.cs` - Tests exist
- `TvheadendUrlBuilderTests.cs` - Tests exist

**Current Total:** ~16 tests

---

## 📝 Recommended Action Plan

### Phase 1: High-Impact Tests (40% → 60% Coverage)
**Effort: 1-2 weeks**
1. Add comprehensive tests for `TvheadendUrlBuilder` edge cases
2. Test `TvheadendJsonHelper` with various JSON structures
3. Add model/DTO serialization tests
4. Test `PluginConfiguration` initialization and validation

### Phase 2: Core Service Tests (60% → 80% Coverage)
**Effort: 2-3 weeks**
1. Create fixtures for `TvheadendApiClient` HTTP interactions
2. Mock TVHeadend responses and test `GuideService`
3. Test DVR timer CRUD operations
4. Test stream lifecycle management

### Phase 3: Controller & Integration Tests (80% → 99% Coverage)
**Effort: 2-3 weeks**
1. Expand `TvHeadendApiController` tests (Diagnose, CreateProfile, GenerateAuthToken)
2. Add integration tests for full workflows
3. Test error handling and edge cases
4. Test streaming profile resolution logic

---

## 🎓 Testing Strategy

### For Service Classes
```csharp
// Use Moq for dependencies
var mockGuide = new Mock<IGuideService>();
var mockDvr = new Mock<IDvrService>();
var sut = new OrchestratorService(mockGuide.Object, mockDvr.Object, ...);
```

### For TVHeadend API Client
```csharp
// Use HttpClientFactory with mocked responses
var mockHttpClientFactory = new Mock<IHttpClientFactory>();
var mockHttpClient = new Mock<HttpClient>();
```

### For Models
```csharp
// Verify JSON serialization/deserialization
var json = JsonSerializer.Serialize(model);
var deserialized = JsonSerializer.Deserialize<Model>(json);
Assert.NotNull(deserialized);
```

---

## 📊 Coverage Target by File

| File Type | Current | Target | Effort |
|-----------|---------|--------|--------|
| Service (Core) | 0% | 95%+ | High |
| API Controllers | 6% | 90%+ | Medium |
| Helpers/Utils | 34% | 85%+ | Medium |
| Models | 0-5% | 70%+ | Low |
| Infrastructure | 0% | 85%+ | High |

---

## ⚠️ Notes

1. **Async Methods:** Many service methods are async and need special handling with `async`/`await` in tests
2. **External Dependencies:** TVHeadend API calls should be mocked
3. **Plugin Integration:** LiveTV provider methods require Jellyfin mocks
4. **Configuration:** Tests need to handle plugin configuration state

---

## 📚 Test Framework Setup

Current Stack:
- **Framework:** xUnit 2.9.2
- **Mocking:** No mocking library (recommend: Moq 4.18+)
- **Coverage:** Coverlet 8.0.1

Recommended additions:
```xml
<PackageReference Include="Moq" Version="4.18.5" />
<PackageReference Include="AutoFixture" Version="4.18.1" />
<PackageReference Include="FluentAssertions" Version="6.12.1" />
```

---

## 🚀 Next Steps

1. **Install mocking framework:** `dotnet add Jellyfin.Plugin.TvHeadendApi.Tests package Moq`
2. **Create test fixtures** for common objects
3. **Start with high-impact tests** in Phase 1
4. **Run coverage regularly:** `dotnet test --collect:"XPlat Code Coverage"`
5. **Generate reports:** HTML reports for visualization

---

**Generated:** 2026-04-08  
**Current Coverage:** 10.66% lines | 7.62% branches  
**Target:** 99% coverage  
**Estimated Work:** 225-315 additional tests | 5-8 weeks

