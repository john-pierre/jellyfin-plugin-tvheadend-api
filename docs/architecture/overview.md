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
│  │                   Helper Layer                            │  │
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
| `StatisticsService` | Tracks live TV viewing sessions via Jellyfin playback events | Background (`IHostedService`) |
| `DashboardService` | Aggregates diagnostics, status, input, and subscription data for the admin dashboard | Admin UI |
| `TvHeadendCometService` | Buffers TVHeadend Comet log and disk-space updates for dashboard endpoints | Background (`IHostedService`) |
| `EncodingOptionsReader` | Reads Jellyfin's FFmpeg encoding options for diagnostic reporting | DiagnosticService |

### API Controller

`PluginController` exposes admin-only REST endpoints under `/TvHeadendApi/`:

- `GET /PluginInfo` — plugin metadata
- `POST /ResetToDefaults` — reset configuration
- `GET /Diagnose` — compatibility report
- `POST /CreateProfile` — create TVHeadend profiles
- `POST /GenerateAuthToken` — generate auth token
- `GET /ProfileOptions` — available profiles
- `GET /Statistics` — viewing stats
- `DELETE /Statistics` — clear stats

All endpoints require Jellyfin admin elevation.

## Module Responsibilities

### What Each Module Is Responsible For

| Module | Responsibility |
|---|---|
| `Plugin.cs` | Plugin lifecycle, configuration page registration, static instance |
| `ServiceRegistrator.cs` | DI container wiring |
| `OrchestratorService` | Thin `ILiveTvService` facade — delegation only, no business logic |
| `Service/Guide/` | Channel listing, EPG programs, content types, channel tags |
| `Service/Dvr/` | Single timers, series timers, recording profile lookup |
| `Service/Stream/` | Stream URL construction, media source info, stream lifecycle |
| `Service/Auth/` | Auth token generation, validation, TVHeadend user management |
| `Service/Profile/` | Profile resolution, container mapping, default profile creation |
| `Service/Diagnostic/` | Compatibility checks, configuration analysis |
| `Service/Statistics/` | Viewing session tracking, persistence, retention |
| `Service/Helper/` | Low-level HTTP, URL building, grid pagination, idnode helpers |
| `Model/` | TVHeadend API response DTOs and result objects |
| `Configuration/` | Plugin configuration model and admin HTML page |
| `Api/` | REST controller for admin UI integration |

### What Each Module Must NOT Do

| Module | Must Not |
|---|---|
| `OrchestratorService` | Contain business logic, HTTP calls, or mapping |
| Domain services | Access `Plugin.Instance` directly (use injected dependencies) |
| `Model/` | Contain logic beyond simple data holding |
| `Service/Helper/` | Contain domain-specific business logic |
| `Api/` | Contain business logic (delegate to services) |
| `Configuration/` | Contain runtime behavior logic |

## Dependency Direction Rules

1. **Controller → Service → Helper → HTTP** (never reverse).
2. **Services depend on interfaces** (`IApiClient`, `IUrlBuilder`, `IProfileResolver`, etc.).
3. **Models are passive data containers** — no service dependencies.
4. **Configuration is read-only at runtime** — services read config via `IApiClient.GetCurrentConfiguration()`.

## Where Things Belong

| Concern | Location |
|---|---|
| Business logic | Domain services (`Service/{domain}/`) |
| Integration/HTTP logic | `Service/Helper/ApiClient`, `GridFetcher` |
| DTOs / response models | `Model/{domain}/` |
| Mapping (TVHeadend → Jellyfin) | Domain services (inline in service methods) |
| Validation | Service layer (argument guards) or `TokenValidator` |
| Retry / error handling | Service layer (currently: basic exception handling, no retry) |
| Configuration | `Configuration/PluginConfiguration.cs` |
| Admin UI integration | `Api/PluginController.cs` + `Configuration/ConfigPage.html` |

## Caching and State

- **Profile cache**: `ProfileContainerResolver` caches resolved profile metadata with configurable TTL.
- **MediaInfo cache**: `MediaSourceService` reads/writes Jellyfin's `cache/mediainfo/*.json` files to pre-populate probe data.
- **Statistics state**: `StatisticsService` maintains active sessions in memory and persists history to SQLite.
- **Comet state**: `TvHeadendCometService` buffers recent log messages and the latest disk-space update in memory.
- **No other shared mutable state** across services.

## Error Handling Strategy

- Services use try/catch with `ILogger.LogWarning` for non-fatal failures.
- Argument validation via `ArgumentNullException.ThrowIfNull` and `ArgumentException.ThrowIfNullOrWhiteSpace`.
- No global error handling middleware (standard for Jellyfin plugins).
- Transient HTTP failures are handled by `ResilienceHandler` (custom `DelegatingHandler`) with exponential back-off retry (3 attempts) and circuit breaker (5 failures → 30s open).

