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
| `DefaultProfileService` | Creates and maintains the managed `jellyfin` TVHeadend transcode profile: auto-detects the best H.264 encoder (hardware preferred), self-verifies the profile by reading a test stream, and falls back to software libx264 if the encoder delivers no data | Admin UI |
| `MediaInfoCacheService` | Pre-creates/reconciles Jellyfin mediainfo cache files, maintains the per-(channel × profile) cache store, and provides cache warmup/invalidation | Stream services, Admin UI |
| `ChannelNameCacheWarmupService` | Warms the channel-name cache at startup (with retry/back-off) so telemetry shows channel names instead of raw UUIDs | Background (`IHostedService`) |
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
| `MetricsController` | Live and historical streaming telemetry (`/TvHeadendApi/Metrics`) |
| `RelayController` | Stream/image proxy, token security, status |

The admin controllers are routed under `/TvHeadendApi/` and require Jellyfin admin elevation.

`RelayController` is routed under `/api/tvheadend/` and is **not** wholly anonymous: it carries a
class-level `[Authorize(Policy = Policies.LiveTvAccess)]`. Four actions opt out with
`[AllowAnonymous]` and rely on a scoped, time-limited relay token instead — `status`,
`stream/{channelId}`, `relay/stream/{channelId}` and `relay/images/{**path}` — because media
players and Jellyfin's image fetcher cannot send Jellyfin auth headers. Everything else on that
controller still requires Live TV access.

## Module Responsibilities

### What Each Module Is Responsible For

| Module | Responsibility |
|---|---|
| `Plugin.cs` | Plugin lifecycle, embedded page registration (`TvHeadendApiConfig` config page, `TvHeadendDashboard` dashboard page), static instance |
| `ServiceRegistrator.cs` | DI container wiring |
| `OrchestratorService` | Thin `ILiveTvService` facade — delegation only, no business logic |
| `Service/Guide/` | Channel listing, EPG programs, content types, channel tags |
| `Service/Dvr/` | Single timers (`SingleTimerService`), series timers (`SeriesTimerService`), recording profile lookup |
| `Service/Stream/` | Stream URL construction, media source info, stream lifecycle, mediainfo cache management (`MediaInfoCacheService`) |
| `Service/Auth/` | Auth token generation, validation, TVHeadend user management |
| `Service/Profile/` | Profile resolution, container mapping, default profile creation |
| `Service/StreamingProfile/` | Hierarchical streaming profile selection, discovery, validation |
| `Service/Diagnostic/` | Compatibility checks, configuration analysis |
| `Service/Statistic/` | Viewing session tracking, SQLite persistence via `ViewingSessionContext`, retention |
| `Service/Backend/` | Low-level HTTP (`ApiClient`), URL building (`UrlBuilder`), grid pagination (`GridFetcher`), idnode helpers |
| `Service/Resilience/` | Retry with exponential back-off and circuit breaker (`ResiliencePolicies`, `FailureClassifier`) |
| `Service/Metrics/` | Streaming telemetry for the dashboard (in-memory `SessionTracker`/`ActiveSessionStore`, read-only `MetricsAggregator` over `relay_request_metric`, `StreamingDashboardService`, `StreamBitrateTracker`) plus the `System.Diagnostics.Metrics` instruments (`MetricService`; cache + stream-setup instruments are populated) |
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
| `Api/` | REST controllers for admin UI integration and the anonymous token-secured relay |

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
| Retry / error handling | `ResilienceHandler` in the HTTP pipeline (retry + circuit breaker); services add try/catch + logging |
| Configuration | `Configuration/PluginConfiguration.cs` |
| Admin UI integration | `Api/` controllers + `Configuration/ConfigPage.html` + `Page/DashboardPage.html` |

## Caching and State

- **Profile cache**: `ProfileContainerResolver` caches resolved profile metadata with configurable TTL.
- **MediaInfo cache**: `MediaInfoCacheService` reads/writes Jellyfin's `cache/mediainfo/*.json` files to pre-populate probe data. In addition it keeps a per-(channel × profile) store under `cache/mediainfo/profiles/<channel>.<profileKey>.json`: the outgoing cache file is preserved there before a profile switch replaces it, and restored when a rule switches the channel back — so probed data survives profile ping-pong. Warmup also pre-seeds the store for every profile reachable via configured rules. On every stream start the cached `Path` is refreshed with the current stream URL (fresh relay token / delivery-mode URL shape), because Jellyfin hands that path verbatim to Direct Play clients.
- **Statistics state**: `StatisticsService` maintains active sessions in memory and persists history to SQLite via `ViewingSessionContext` (EF Core).
- **Comet state**: `CometService` buffers recent log messages and the latest disk-space update in memory.
- **No other shared mutable state** across services.

## Error Handling Strategy

- Services use try/catch with `ILogger.LogWarning` for non-fatal failures.
- Argument validation via `ArgumentNullException.ThrowIfNull` and `ArgumentException.ThrowIfNullOrWhiteSpace`.
- No global error handling middleware (standard for Jellyfin plugins).
- Transient HTTP failures are handled by `ResilienceHandler` (custom `DelegatingHandler` in `Service/Resilience/`) with exponential back-off retry (3 attempts) and circuit breaker (5 failures → 30s open).
