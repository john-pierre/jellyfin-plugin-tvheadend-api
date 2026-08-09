# jellyfin-plugin-tvheadend-api — Domain Language

> **Version:** 1.1 | **Date:** 2026-04-23
>
> This file defines the canonical domain language for jellyfin-plugin-tvheadend-api.
> Always use these terms consistently in code, comments, documentation, and issues.
> Never invent synonyms for these terms.

---

## 1 — Core Concepts

| Term | Class / Package | Description |
|------|----------------|-------------|
| **ILiveTvService** | `MediaBrowser.Controller.LiveTv` | Jellyfin's contract for Live TV providers. The plugin implements this via `OrchestratorService`. |
| **OrchestratorService** | `Service.OrchestratorService` | Thin facade implementing `ILiveTvService`; pure delegation to domain services. |
| **PluginConfiguration** | `Configuration.PluginConfiguration` | All runtime settings with backward-compatible defaults (connection, auth, streaming, DVR). |
| **TVHeadend** | _(external system)_ | Open-source TV streaming server providing HTTP/JSON API for channels, EPG, DVR, profiles, status. |

---

## 2 — Guide & EPG

| Term | Class / Package | Description |
|------|----------------|-------------|
| **Channel Grid** | `Model.Guide.ChannelGridResponse` | Paginated list of TV channels from `/api/channel/grid`. |
| **EPG Event** | `Model.Guide.EpgEventsGridEntry` | A single electronic program guide entry with start/stop/title/description. |
| **Content Type** | `Model.Guide.EpgContentTypeListEntry` | DVB content descriptor (genre) mapped to Jellyfin `ProgramAudio`/genre. |
| **Channel Tag** | `Model.Guide.ChannelTagEntry` | TVHeadend grouping label for channels. |

---

## 3 — DVR (Recording)

| Term | Class / Package | Description |
|------|----------------|-------------|
| **Timer** | `Model.Dvr.DvrEntryGridEntry` | A single scheduled or completed recording (maps to Jellyfin `TimerInfo`). |
| **Series Timer** | `Model.Dvr.DvrAutoRecGridEntry` | An automatic recording rule (maps to Jellyfin `SeriesTimerInfo`). |
| **Recording Profile** | `Model.Dvr.DvrConfigGridEntry` | TVHeadend DVR configuration determining storage path and file format. |

---

## 4 — Streaming

| Term | Class / Package | Description |
|------|----------------|-------------|
| **Direct Play** | _(playback mode)_ | Client plays the stream as-is from TVHeadend without transcoding. Preferred mode. |
| **Direct Stream** | _(playback mode)_ | Jellyfin remuxes the container but preserves codecs. |
| **Transcoding** | _(playback mode)_ | Jellyfin re-encodes the stream. Requires FFmpeg and `jellyfin` profile in TVHeadend. |
| **MediaSourceInfo** | `MediaBrowser.Model.MediaInfo` | Jellyfin's stream descriptor containing URL, container, codec hints. |
| **MediaInfo Cache** | `Service.Stream.MediaSourceService` | Pre-created `cache/mediainfo/*.json` files matching Jellyfin's internal hash to skip FFmpeg probing. |
| **Streaming Profile** | `Model.Profile.ProfileListEntry` | TVHeadend profile (e.g., `pass`, `matroska`, `jellyfin`) determining codec/container output. |

---

## 4a — Streaming Profile Selection

| Term | Class / Package | Description |
|------|----------------|-------------|
| **PlaybackMode** | `Configuration.PlaybackMode` | Enum controlling how streams are delivered: Auto, PassThrough, TvHeadendTranscode, JellyfinTranscode. |
| **StreamingProfileSettings** | `Configuration.StreamingProfileSettings` | Configuration section for hierarchical profile selection with global defaults, overrides, and rules. |
| **StreamingProfileRule** | `Configuration.StreamingProfileRule` | A configurable rule matching client, device, or user to a specific playback mode and TVHeadend profile. |
| **StreamingProfileRuleMatchType** | `Configuration.StreamingProfileRuleMatchType` | Enum specifying how a rule matches: ClientNameExact, ClientNameContains, DeviceNameExact, DeviceNameContains, UserIdExact. |
| **ChannelProfileOverride** | `Configuration.ChannelProfileOverride` | Per-channel streaming profile override (highest precedence in the resolution hierarchy). |
| **ChannelGroupProfileOverride** | `Configuration.ChannelGroupProfileOverride` | Per-channel-group streaming profile override. |
| **StreamingProfileContext** | `Service.StreamingProfile.StreamingProfileContext` | Input record describing the playback request context (channel, client, device, user). |
| **StreamingProfileResolutionResult** | `Service.StreamingProfile.StreamingProfileResolutionResult` | Output of profile resolution with effective mode, profile, matched rule, and debug reasons. |
| **ResolutionSource** | `Service.StreamingProfile.ResolutionSource` | Enum identifying which hierarchy level produced the resolution result. |
| **StreamingProfileResolver** | `Service.StreamingProfile.StreamingProfileResolver` | Central resolver implementing deterministic hierarchical profile resolution. |
| **ProfileDiscoveryService** | `Service.StreamingProfile.ProfileDiscoveryService` | Discovers available TVHeadend profiles with TTL-based caching and validates configured profile names. |
| **DiscoveredProfile** | `Service.StreamingProfile.DiscoveredProfile` | Public DTO representing a discovered TVHeadend streaming profile (Key, Name). |

---

## 5 — Authentication

| Term | Class / Package | Description |
|------|----------------|-------------|
| **Auth Token** | `Service.Auth.TokenService` | Alphanumeric persistent token appended as `?auth=<token>` to stream/image URLs. Generated via TVHeadend user API. |
| **TokenValidator** | `Service.Auth.TokenValidator` | Validates that a token string is purely alphanumeric. |

---

## 6 — Profile Management

| Term | Class / Package | Description |
|------|----------------|-------------|
| **ProfileResolver** | `Service.Profile.ProfileResolver` | Resolves available streaming profiles from TVHeadend and retrieves metadata. |
| **ProfileContainerResolver** | `Service.Profile.ProfileContainerResolver` | Maps a profile's type/class to a container string (e.g., `matroska` → `mkv`). |
| **DefaultProfileService** | `Service.Profile.DefaultProfileService` | Creates the `jellyfin` transcoding profile in TVHeadend if it doesn't exist. |
| **ProfileSnapshot** | `Model.Profile.ProfileSnapshot` | Immutable record of a resolved profile's codec/container details. |

---

## 7 — Infrastructure & Backend

| Term | Class / Package | Description |
|------|----------------|-------------|
| **ApiClient** | `Service.Backend.ApiClient` | Typed HTTP client for TVHeadend API calls with auth header injection. |
| **UrlBuilder** | `Service.Backend.UrlBuilder` | Constructs TVHeadend URLs with appropriate auth parameters (header, URL, or query). |
| **GridFetcher** | `Service.Backend.GridFetcher` | Generic paginated fetcher for TVHeadend grid endpoints (probe + parallel fetch). |
| **IdNode** | `Service.Backend.IdNodeValueHelper` | TVHeadend's generic entity model; helper extracts typed values from idnode params. |
| **ResilienceHandler** | `Service.Resilience.ResiliencePolicies` | DelegatingHandler implementing retry with exponential back-off and circuit breaker. |
| **MetricService** | `Service.Metric.MetricService` | `System.Diagnostics.Metrics` instruments for API calls, durations, cache hits/misses. |
| **ConfigurationProvider** | `Service.Configuration.ConfigurationProvider` | Resolves current `PluginConfiguration` via DI without direct `Plugin.Instance` access. |
| **ConfigurationSaver** | `Service.Configuration.ConfigurationSaver` | Mutates and persists plugin configuration. |
| **CachePathProvider** | `Service.Storage.CachePathProvider` | Resolves plugin cache path. |
| **DataFolderPathProvider** | `Service.Storage.DataFolderPathProvider` | Resolves plugin data folder path. |
| **JsonDefaults** | `Service.Common.JsonDefaults` | Shared JSON serialization defaults. |

---

## 8 — Monitoring & Status

| Term | Class / Package | Description |
|------|----------------|-------------|
| **DiagnosticService** | `Service.Diagnostic.DiagnosticService` | Runs compatibility checks and produces a structured diagnostic report with score. |
| **StatisticsService** | `Service.Statistic.StatisticsService` | Tracks live TV viewing sessions via `ISessionManager` events; persists to SQLite via `ViewingSessionContext` (EF Core). |
| **StatusService** | `Service.Status.StatusService` | Reads TVHeadend server activity and active connections. |
| **CometService** | `Service.Comet.CometService` | Hosted service maintaining a WebSocket connection to TVHeadend's Comet endpoint; buffers log messages and disk-space updates for the admin UI. |
| **InputMonitorService** | `Service.Input.InputMonitorService` | Monitors TV tuner signal quality (signal strength, BER, SNR, bitrate). |
| **SubscriptionService** | `Service.Subscription.SubscriptionService` | Lists active streaming subscriptions in TVHeadend. |

---

## 9 — Database

| Term | Class / Package | Description |
|------|----------------|-------------|
| **DatabaseProvider** | `Service.Database.DatabaseProvider` | Provides SQLite database connections for the plugin. |
| **DatabaseMigrationService** | `Service.Database.DatabaseMigrationService` | Applies schema migrations to the plugin's SQLite database. |
| **DatabaseHealthService** | `Service.Database.DatabaseHealthService` | Monitors database health status and reports health snapshots. |
| **DatabaseCleanupService** | `Service.Database.DatabaseCleanupService` | Performs periodic database maintenance and cleanup. |
| **DatabaseRecoveryService** | `Service.Database.DatabaseRecoveryService` | Recovers from database corruption or connection failures. |
| **DatabaseWriteCoordinator** | `Service.Database.DatabaseWriteCoordinator` | Coordinates concurrent write access to the SQLite database. |
| **ViewingSessionContext** | `Service.Statistic.ViewingSessionContext` | EF Core `DbContext` for viewing session persistence in SQLite. |

---

## 10 — Health

| Term | Class / Package | Description |
|------|----------------|-------------|
| **HealthService** | `Service.Health.HealthService` | Aggregates health signals from various services into an overall plugin health state. |
| **HealthSnapshot** | `Service.Health.HealthSnapshot` | Immutable snapshot of the aggregated TVHeadend upstream health (declared in `Service/Health/HealthState.cs` alongside `HealthStatus`, `CircuitState` and `CircuitBreakerMetrics`). |

---

## 11 — Logging

| Term | Class / Package | Description |
|------|----------------|-------------|
| **PluginLogService** | `Service.Logging.PluginLogService` | Queries and filters plugin-relevant log entries from Jellyfin's log output. |
| **LogParser** | `Service.Logging.LogParser` | Parses structured log entries from Jellyfin's log format. |
| **LogSanitizer** | `Service.Logging.LogSanitizer` | Sanitizes sensitive data (credentials, tokens) from log output. |
| **PluginPersistingLogger** | `Service.Logging.PluginPersistingLogger` | Transparent `ILogger<>` decorator that forwards to the host logger and additionally persists plugin-namespace entries for the dashboard log view. |
| **PluginLogLevelPolicy** | `Service.Logging.PluginLogLevelPolicy` | Applies the plugin-specific log level override from `PluginConfiguration.PluginLogLevel`. |

---

## 12 — Relay

| Term | Class / Package | Description |
|------|----------------|-------------|
| **RelayService** | `Service.Relay.RelayService` | Provides an alternative stream path where clients connect through Jellyfin to TVHeadend. |
| **RelayUrlBuilder** | `Service.Relay.RelayUrlBuilder` | Constructs relay stream URLs for proxied playback. |
| **RelayTokenService** | `Service.Relay.RelayTokenService` | Generates and manages relay authentication tokens. |
| **RelayTokenValidatorService** | `Service.Relay.RelayTokenValidatorService` | Validates relay tokens for stream access. |
| **RelayMetricsService** | `Service.Relay.RelayMetricsService` | Tracks relay usage metrics (connections, bandwidth). |
| **RelayTokenCleanupService** | `Service.Relay.RelayTokenCleanupService` | Cleans up expired relay tokens. |
| **RelayActivityTracker** | `Service.Relay.RelayActivityTracker` | Tracks active relay connections and activity. |

