# Developer Onboarding Guide

Welcome! This guide walks you through the plugin architecture with annotated explanations so you can start contributing quickly.

## Prerequisites

- .NET 8.0 SDK
- An IDE (JetBrains Rider, Visual Studio, or VS Code with C# extension)
- Basic understanding of Jellyfin plugin development (helpful but not required)
- Optional: Docker for local Jellyfin test instance

## Build and Run in 2 Minutes

```bash
# Clone
git clone https://github.com/john-pierre/jellyfin-plugin-tvheadend-api.git
cd jellyfin-plugin-tvheadend-api

# Build
dotnet restore Jellyfin.Plugin.TvHeadendApi.sln
dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release --no-restore

# Test
dotnet test Jellyfin.Plugin.TvHeadendApi.Tests/Jellyfin.Plugin.TvHeadendApi.Tests.csproj -c Release --no-build

# Optional: start dev Jellyfin
docker compose up -d --build
# → http://localhost:8096
```

## Architecture at a Glance

```
┌─────────────────────────────────────────────────────┐
│  Jellyfin Server                                    │
│  ┌───────────────────────────────────────────────┐  │
│  │  Plugin.cs  →  ServiceRegistrator.cs          │  │  ← Entry point: registers all services in DI
│  └───────────────────────────────────────────────┘  │
│  ┌───────────────────────────────────────────────┐  │
│  │  PluginController.cs (REST API)               │  │  ← Admin endpoints: diagnose, profiles, tokens, stats
│  └───────────────────────────────────────────────┘  │
│  ┌───────────────────────────────────────────────┐  │
│  │  OrchestratorService (ILiveTvService)         │  │  ← Thin facade: delegates to domain services
│  │  ┌─────────┐ ┌─────────┐ ┌──────────────────┐│  │
│  │  │ Guide   │ │  DVR    │ │ MediaSource      ││  │  ← Domain services: each owns one concern
│  │  │ Service │ │ Service │ │ Service          ││  │
│  │  └────┬────┘ └────┬────┘ └────────┬─────────┘│  │
│  │       └───────────┴───────────────┘           │  │
│  │  ┌───────────────────────────────────────────┐│  │
│  │  │  ApiClient  ·  UrlBuilder  ·  GridFetcher ││  │  ← Infrastructure: HTTP, URLs, pagination
│  │  │  ResiliencePolicies  ·  PluginMetrics     ││  │  ← Resilience + observability
│  │  └───────────────────────────────────────────┘│  │
│  └───────────────────────────────────────────────┘  │
│                         │                            │
│                    HTTP/JSON API                      │
│                         ▼                            │
│              ┌──────────────────┐                    │
│              │   TVHeadend      │                    │
│              └──────────────────┘                    │
└─────────────────────────────────────────────────────┘
```

## Key Files: Where to Start Reading

Read these files in order for the best understanding:

### 1. `Plugin.cs` — Entry Point

The Jellyfin plugin bootstrap. Defines plugin GUID, name, version. Registers the config page HTML as an embedded resource.

### 2. `ServiceRegistrator.cs` — DI Wiring

Registers all services into Jellyfin's DI container. This is where you see the full dependency graph. Key registrations:

- Named `HttpClient` instances (`TvHeadend`, `TvHeadendUnsafe`) with Polly resilience policies
- All domain services (Guide, DVR, Stream, Profile, Auth, Diagnostic, Statistics)
- `OrchestratorService` as `ILiveTvService` — the contract Jellyfin uses for Live TV

### 3. `OrchestratorService.cs` — The Facade

Implements Jellyfin's `ILiveTvService` interface. Does NO business logic — just delegates:

- `GetChannelsAsync()` → `IGuideService`
- `GetProgramsAsync()` → `IGuideService`
- `GetChannelStream()` → `IMediaSourceService`
- Timer CRUD → `IDvrService`

**Why this matters:** When adding a new Live TV feature, you add the logic in a domain service and wire a one-liner delegation here.

### 4. `Service/Guide/GuideService.cs` — Channel + EPG

Fetches channels and EPG data from TVHeadend. Maps TVHeadend JSON responses to Jellyfin's `ChannelInfo` and `ProgramInfo` models. Uses `GridFetcher` for paginated API calls.

### 5. `Service/Stream/MediaSourceService.cs` — Stream Setup

The most performance-critical service. Builds the `MediaSourceInfo` that tells Jellyfin how to play a channel:

- Constructs the TVHeadend stream URL with auth token
- Resolves the streaming profile's codec/container info
- Manages the mediainfo cache (read/write/validate/invalidate)

### 6. `Service/Helper/ApiClient.cs` — HTTP Layer

The centralized HTTP abstraction. Uses `IHttpClientFactory` for managed client lifetimes. All TVHeadend API calls flow through here.

### 7. `Configuration/ConfigPage.html` — Admin UI

A single-file HTML page embedded as a resource. Contains:

- Connection/auth/streaming/DVR/statistics configuration forms
- Inline diagnostics display
- Profile dropdowns populated from TVHeadend API
- Chart.js-based viewing statistics

## How Data Flows: Channel Switch Example

```
1. User clicks channel in Jellyfin client
2. Jellyfin calls OrchestratorService.GetChannelStream(channelId)
3. OrchestratorService delegates to MediaSourceService.GetChannelStreamAsync()
4. MediaSourceService:
   a. Gets config from Plugin.Instance
   b. Builds stream URL via UrlBuilder (includes auth token)
   c. Resolves profile snapshot via ProfileContainerResolver
   d. Checks/writes mediainfo cache
   e. Returns MediaSourceInfo to Jellyfin
5. Jellyfin evaluates MediaSourceInfo → decides Direct Play / Remux / Transcode
6. Client receives stream URL and starts playback
```

## Adding a New Feature: Step-by-Step

### Example: Adding a new TVHeadend API endpoint

1. **Add the DTO** in `Model/{Domain}/` — match TVHeadend JSON field names with `[JsonPropertyName]`.
2. **Add the service method** in the appropriate domain service (or create a new service if it's a new domain).
3. **Wire the service** in `ServiceRegistrator.cs` if new.
4. **Add a delegation method** in `OrchestratorService.cs` if it's a `ILiveTvService` method.
5. **Add tests** — at minimum one happy-path and one error test per method.
6. **Add a contract test** in `Tests/Integration/` if you're consuming a new API shape.
7. **Build and test:**
   ```bash
   dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release
   dotnet test Jellyfin.Plugin.TvHeadendApi.Tests/Jellyfin.Plugin.TvHeadendApi.Tests.csproj -c Release --no-build
   ```

## Key Conventions

| Convention | Rule |
|---|---|
| File naming | File name == primary type name (`GuideService.cs` → `class GuideService`) |
| Folder structure | `Service/{Domain}/` for domain services, `Service/Helper/` for infrastructure |
| Test naming | `{MethodName}_{Scenario}_{Expected}` (e.g., `GetChannelsAsync_ReturnsEmpty_WhenNoChannels`) |
| Test location | Mirror source structure: `Tests/Service/Guide/GuideServiceTests.cs` |
| Logging | Use `ILogger<T>` with semantic templates: `_logger.LogDebug("Fetching channels from {Url}.", url)` |
| Null safety | `Nullable` is enabled. Use `ArgumentNullException.ThrowIfNull()` in constructors. |

## Documentation Map

| Document | Purpose |
|---|---|
| `docs/architecture/architecture-overview.md` | Layers, dependency direction, module map |
| `docs/architecture/architecture-decisions.md` | ADR-style design decision records |
| `docs/architecture/module-responsibilities.md` | Per-module ownership and boundary rules |
| `docs/guides/test-strategy.md` | Test types, naming, coverage policy |
| `docs/guides/naming-conventions.md` | File/folder/type naming rules |
| `docs/guides/observability.md` | Logging categories, metrics, troubleshooting |
| `docs/guides/client-compatibility.md` | Client testing guidance and matrix |
| `docs/ROADMAP.md` | Milestone tracking and future plans |
| `AGENTS.md` | AI agent context and ground rules |

## Common Tasks Cheat Sheet

| Task | Command / Location |
|---|---|
| Build | `dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release` |
| Test | `dotnet test Jellyfin.Plugin.TvHeadendApi.Tests/Jellyfin.Plugin.TvHeadendApi.Tests.csproj -c Release --no-build` |
| Run dev Jellyfin | `docker compose up -d --build` → `http://localhost:8096` |
| Add a service | Create in `Service/{Domain}/`, register in `ServiceRegistrator.cs` |
| Add a test | Create in `Tests/Service/{Domain}/`, follow `{Method}_{Scenario}_{Expected}` naming |
| Check coverage | `dotnet test ... --collect:"XPlat Code Coverage"` → check `TestResults/` |
| Monitor metrics | `dotnet-counters monitor --counters "Jellyfin.Plugin.TvHeadendApi"` |

