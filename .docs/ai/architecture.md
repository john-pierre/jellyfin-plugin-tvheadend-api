# Architecture Reference

## Layer Diagram

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

**Dependency direction:** Controller -> Service -> Backend -> HTTP (never reverse).

## Service Folder Structure

All services live under `Jellyfin.Plugin.TvHeadendApi/Service/`:

| Folder | Key Types |
|--------|-----------|
| `Auth/` | `DigestAuthHandler`, `TokenService`, `TokenValidator` |
| `Backend/` | `ApiClient`, `UrlBuilder`, `GridFetcher`, `HttpRequestCloner` |
| `Comet/` | `CometService` (IHostedService) |
| `Common/` | `JsonDefaults` |
| `Configuration/` | `ConfigurationProvider`, `ConfigurationSaver` |
| `Dashboard/` | `DashboardService` |
| `Database/` | `DatabaseProvider`, `DatabaseWriteCoordinator`, `DatabaseCleanupService`, `DatabaseCleanupHostedService`, `DatabaseConnectionFactory` |
| `Diagnostic/` | `DiagnosticService`, `EncodingOptionsReader` |
| `Dvr/` | `SingleTimerService`, `SeriesTimerService` |
| `Guide/` | `GuideService` |
| `Health/` | `HealthService` |
| `Input/` | `InputMonitorService` |
| `Logging/` | `PluginLogService` (IHostedService), `LogSanitizer` |
| `Metric/` | `MetricService` |
| `Profile/` | `ProfileResolver`, `ProfileContainerResolver`, `DefaultProfileService`, `ProfileDiscoveryService` |
| `Relay/` | `RelayService`, `RelayUrlBuilder`, `RelayTokenService`, `RelayTokenRepository`, `RelayTokenHasher`, `RelayTokenValidatorService`, `RelayTokenCleanupService` (IHostedService), `RelayMetricsService` (IHostedService), `RelayActivityTracker` |
| `Resilience/` | `ResilienceHandler`, `ResiliencePolicies`, `FailureClassifier` |
| `Statistic/` | `StatisticsService` (IHostedService), `ViewingSessionContext` |
| `Status/` | `StatusService` |
| `Storage/` | `CachePathProvider`, `DataFolderPathProvider` |
| `Stream/` | `MediaSourceService`, `LifecycleService`, `MediaInfoCacheService` |
| `StreamingProfile/` | `StreamingProfileResolver` |
| `Subscription/` | `SubscriptionService` |

Plus `OrchestratorService.cs` at the `Service/` root.

## Model Folder Structure

`Jellyfin.Plugin.TvHeadendApi/Model/` — 11 sub-domains:

`Auth/`, `Dashboard/`, `Diagnostic/`, `Dvr/`, `Guide/`, `Input/`, `Profile/`, `Relay/`, `Statistic/`, `Status/`, `Subscription/`

Models are pure data shapes (DTOs). No logic beyond property declarations.

## EF Core DbContexts (3)

| Context | Database | Purpose |
|---------|----------|---------|
| `ViewingSessionContext` | Plugin SQLite DB | Viewing sessions, health transitions, log entries |
| `RelayTokenDbContext` | Plugin SQLite DB | Relay token persistence (HMAC hashes only) |
| `RelayMetricsContext` | Plugin SQLite DB | Relay request metrics |

All write operations are serialized through `DatabaseWriteCoordinator` (SQLite single-writer constraint).

## IHostedService Implementations (6)

| Service | Purpose |
|---------|---------|
| `StatisticsService` | Viewing session tracking, periodic persistence |
| `PluginLogService` | Plugin log file monitoring |
| `CometService` | TVHeadend Comet long-poll for log/disk updates |
| `DatabaseCleanupHostedService` | Periodic database cleanup and retention |
| `RelayTokenCleanupService` | Expired relay token purging |
| `RelayMetricsService` | Relay request metrics persistence |

## API Controllers

| Controller | Base Route | Auth |
|------------|------------|------|
| `PluginController` | `/TvHeadendApi/` | Admin elevation required |
| `StatisticsController` | (viewing statistics endpoints) | Admin elevation required |
| `MonitoringController` | (status, connections, inputs, subscriptions, health endpoints) | Admin elevation required |
| `DashboardController` | (dashboard endpoints) | Admin elevation required |
| `RelayController` | (relay stream endpoints) | Anonymous + token-secured |
| `LogsController` | (log endpoints) | Admin elevation required |
| `DashboardLogsController` | (dashboard log endpoints) | Admin elevation required |
| `StreamingProfileController` | (profile endpoints) | Admin elevation required |

## Configuration

`Configuration/PluginConfiguration.cs` — 50+ properties covering:

- TVHeadend connection (host, port, credentials)
- Streaming profiles and playback mode
- Relay settings (token TTL, max uses)
- Statistics retention
- Health check settings
- Logging preferences
- Dashboard preferences

Supporting types: `StreamingProfileRule`, `StreamingProfileSettings`, `ChannelProfileOverride`, `ChannelGroupProfileOverride`, `PlaybackMode`, `StatisticsRetentionPeriod`.

## Dashboard Pages (2 embedded HTML)

| File | Purpose |
|------|---------|
| `Configuration/ConfigPage.html` | Plugin settings page in Jellyfin admin |
| `Page/DashboardPage.html` | Real-time status dashboard in Jellyfin admin |
