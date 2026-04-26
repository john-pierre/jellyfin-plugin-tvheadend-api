# Architecture Overview

This document describes the architecture of the Jellyfin TVHeadend API plugin.

## Purpose

The plugin integrates TVHeadend with Jellyfin's Live TV subsystem using TVHeadend's HTTP/JSON API exclusively (no HTSP).

## High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  Jellyfin Server                                                │
│                                                                 │
│  ┌──────────────┐    ┌──────────────────────────────────────┐   │
│  │ LiveTV Core   │───>│ OrchestratorService (ILiveTvService) │   │
│  └──────────────┘    └──────────┬───────────────────────────┘   │
│                                 │ delegates to                  │
│         ┌───────────────────────┼───────────────────┐           │
│         │                       │                   │           │
│  ┌──────▼──────┐  ┌────────────▼───┐  ┌────────────▼────────┐  │
│  │ GuideService │  │  DvrService    │  │ Stream services     │  │
│  │ (channels,   │  │ (timers,       │  │ (MediaSourceService,│  │
│  │  EPG, tags)  │  │  series timers,│  │  LifecycleService)  │  │
│  └──────┬───────┘  │  recordings)   │  └─────────┬──────────┘  │
│         │          └───────┬────────┘            │              │
│         │                  │                     │              │
│  ┌──────▼──────────────────▼─────────────────────▼──────────┐  │
│  │                   Backend Layer                            │  │
│  │  IApiClient · IUrlBuilder · GridFetcher                    │  │
│  └──────────────────────────┬────────────────────────────────┘  │
│                             │ HTTP/JSON                         │
└─────────────────────────────┼───────────────────────────────────┘
                              │
                     ┌────────▼────────┐
                     │   TVHeadend     │
                     │   HTTP API      │
                     └─────────────────┘
```

### Supporting Services

| Service | Purpose | Integration |
|---|---|---|
| `TokenService` | Auth token generation/validation via TVHeadend user API | Admin UI |
| `DiagnosticService` | Compatibility checks and configuration validation | Admin UI |
| `DefaultProfileService` | Creates recommended TVHeadend streaming profiles | Admin UI |
| `ProfileResolver` / `ProfileContainerResolver` | Resolves active streaming profile metadata for cache/container decisions | Stream services |
| `StreamingProfileResolver` | Hierarchical rule-based profile selection (channel → group → client → user → global → fallback) | Stream services |
| `ProfileDiscoveryService` | Discovers available TVHeadend profiles with TTL caching; validates configured profile names | Admin UI, StreamingProfileResolver |
| `StatisticsService` | Tracks live TV viewing sessions via Jellyfin playback events | Background (`IHostedService`) |
| `DashboardService` | Aggregates diagnostics, status, input, and subscription data for the admin dashboard | Admin UI |
| `CometService` | Buffers TVHeadend Comet log and disk-space updates for dashboard endpoints | Background (`IHostedService`) |
| `EncodingOptionsReader` | Reads Jellyfin's FFmpeg encoding options for diagnostic reporting | DiagnosticService |

### API Controllers

The plugin exposes admin-only REST endpoints under `/TvHeadendApi/` via multiple focused controllers:

| Controller | Responsibility |
|---|---|
| `PluginController` | Config, diagnostics, profiles, auth |
| `StatisticsController` | Viewing statistics |
| `MonitoringController` | Status, connections, inputs, subscriptions, health |
| `DashboardController` | Aggregated dashboard data, relay metrics |
| `StreamingProfileController` | Discovery, resolution, validation, channels, groups |
| `LogsController` | Logs, disk space |
| `DashboardLogsController` | Filtered log queries |
| `RelayController` | Stream/image proxy, token security, status |

All endpoints require Jellyfin admin elevation (except `RelayController` which uses anonymous + token-secured access).

## Module Responsibilities

### What Each Module Is Responsible For

| Module | Responsibility |
|---|---|
| `Plugin.cs` | Plugin lifecycle, configuration page registration, static instance |
| `ServiceRegistrator.cs` | DI container wiring |
| `OrchestratorService` | Thin `ILiveTvService` facade — delegation only, no business logic |
| `Service/Guide/` | Channel listing, EPG programs, content types, channel tags |
| `Service/Dvr/` | Single timers (`SingleTimerService`), series timers (`SeriesTimerService`), recording profile lookup |
| `Service/Stream/` | Stream URL construction, media source info, stream lifecycle |
| `Service/Auth/` | Auth token generation, validation, TVHeadend user management |
| `Service/Profile/` | Profile resolution, container mapping, default profile creation |
| `Service/StreamingProfile/` | Hierarchical streaming profile selection, discovery, validation |
| `Service/Diagnostic/` | Compatibility checks, configuration analysis |
| `Service/Statistic/` | Viewing session tracking, SQLite persistence via `ViewingSessionContext`, retention |
| `Service/Backend/` | Low-level HTTP (`ApiClient`), URL building (`UrlBuilder`), grid pagination (`GridFetcher`), idnode helpers |
| `Service/Resilience/` | Retry with exponential back-off and circuit breaker (`ResiliencePolicies`, `FailureClassifier`) |
| `Service/Metric/` | `System.Diagnostics.Metrics` instruments for API calls, durations, cache hits/misses (`MetricService`) |
| `Service/Configuration/` | Plugin configuration access (`ConfigurationProvider`) and mutation (`ConfigurationSaver`) |
| `Service/Storage/` | Plugin path resolution (`CachePathProvider`, `DataFolderPathProvider`) |
| `Service/Common/` | Shared utilities (`JsonDefaults`) |
| `Service/Database/` | SQLite database lifecycle, migrations, health, cleanup, recovery |
| `Service/Health/` | Aggregated plugin health state |
| `Service/Logging/` | Plugin log query, parsing, sanitization |
| `Service/Relay/` | Stream relay URL building, token management, relay metrics |
| `Service/Dashboard/` | Aggregates diagnostics, status, input, and subscription data for the admin dashboard |
| `Model/` | TVHeadend API response DTOs and result objects |
| `Configuration/` | Plugin configuration model and admin HTML page |
| `Api/` | REST controller for admin UI integration |

### What Each Module Must NOT Do

| Module | Must Not |
|---|---|
| `OrchestratorService` | Contain business logic, HTTP calls, or mapping |
| Domain services | Access `Plugin.Instance` directly (use injected dependencies) |
| `Model/` | Contain logic beyond simple data holding |
| `Service/Backend/` | Contain domain-specific business logic |
| `Api/` | Contain business logic (delegate to services) |
| `Configuration/` | Contain runtime behavior logic |

## Dependency Direction Rules

1. **Controller → Service → Backend → HTTP** (never reverse).
2. **Services depend on interfaces** (`IApiClient`, `IUrlBuilder`, `IProfileResolver`, etc.).
3. **Models are passive data containers** — no service dependencies.
4. **Configuration is read-only at runtime** — services read config via `IApiClient.GetCurrentConfiguration()`.

## Where Things Belong

| Concern | Location |
|---|---|
| Business logic | Domain services (`Service/{domain}/`) |
| Integration/HTTP logic | `Service/Backend/ApiClient`, `GridFetcher` |
| DTOs / response models | `Model/{domain}/` |
| Mapping (TVHeadend → Jellyfin) | Domain services (inline in service methods) |
| Validation | Service layer (argument guards) or `TokenValidator` |
| Retry / error handling | Service layer (currently: basic exception handling, no retry) |
| Configuration | `Configuration/PluginConfiguration.cs` |
| Admin UI integration | `Api/PluginController.cs` + `Configuration/ConfigPage.html` |

## Caching and State

- **Profile cache**: `ProfileContainerResolver` caches resolved profile metadata with configurable TTL.
- **MediaInfo cache**: `MediaSourceService` reads/writes Jellyfin's `cache/mediainfo/*.json` files to pre-populate probe data.
- **Statistics state**: `StatisticsService` maintains active sessions in memory and persists history to SQLite via `ViewingSessionContext` (EF Core).
- **Comet state**: `CometService` buffers recent log messages and the latest disk-space update in memory.
- **No other shared mutable state** across services.

## Error Handling Strategy

- Services use try/catch with `ILogger.LogWarning` for non-fatal failures.
- Argument validation via `ArgumentNullException.ThrowIfNull` and `ArgumentException.ThrowIfNullOrWhiteSpace`.
- No global error handling middleware (standard for Jellyfin plugins).
- Transient HTTP failures are handled by `ResilienceHandler` (custom `DelegatingHandler` in `Service/Resilience/`) with exponential back-off retry (3 attempts) and circuit breaker (5 failures → 30s open).
