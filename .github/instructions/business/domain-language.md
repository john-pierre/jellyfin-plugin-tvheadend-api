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
| **EPG Event** | `Model.Guide.EpgEvent` | A single electronic program guide entry with start/stop/title/description. |
| **Content Type** | `Model.Guide.EpgContentType` | DVB content descriptor (genre) mapped to Jellyfin `ProgramAudio`/genre. |
| **Channel Tag** | `Model.Guide.ChannelTag` | TVHeadend grouping label for channels. |

---

## 3 — DVR (Recording)

| Term | Class / Package | Description |
|------|----------------|-------------|
| **Timer** | `Model.Dvr.DvrEntry` | A single scheduled or completed recording (maps to Jellyfin `TimerInfo`). |
| **Series Timer** | `Model.Dvr.DvrAutoRecEntry` | An automatic recording rule (maps to Jellyfin `SeriesTimerInfo`). |
| **Recording Profile** | `Model.Dvr.DvrConfig` | TVHeadend DVR configuration determining storage path and file format. |

---

## 4 — Streaming

| Term | Class / Package | Description |
|------|----------------|-------------|
| **Direct Play** | _(playback mode)_ | Client plays the stream as-is from TVHeadend without transcoding. Preferred mode. |
| **Direct Stream** | _(playback mode)_ | Jellyfin remuxes the container but preserves codecs. |
| **Transcoding** | _(playback mode)_ | Jellyfin re-encodes the stream. Requires FFmpeg and `jellyfin` profile in TVHeadend. |
| **MediaSourceInfo** | `MediaBrowser.Model.MediaInfo` | Jellyfin's stream descriptor containing URL, container, codec hints. |
| **MediaInfo Cache** | `Service.Stream.MediaSourceService` | Pre-created `cache/mediainfo/*.json` files matching Jellyfin's internal hash to skip FFmpeg probing. |
| **Streaming Profile** | `Model.Profile.ProfileEntry` | TVHeadend profile (e.g., `pass`, `matroska`, `jellyfin`) determining codec/container output. |

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

## 7 — Infrastructure & Helpers

| Term | Class / Package | Description |
|------|----------------|-------------|
| **ApiClient** | `Service.Helper.ApiClient` | Typed HTTP client for TVHeadend API calls with auth header injection. |
| **UrlBuilder** | `Service.Helper.UrlBuilder` | Constructs TVHeadend URLs with appropriate auth parameters (header, URL, or query). |
| **GridFetcher** | `Service.Helper.GridFetcher` | Generic paginated fetcher for TVHeadend grid endpoints (probe + parallel fetch). |
| **IdNode** | `Service.Helper.IdNodeValueHelper` | TVHeadend's generic entity model; helper extracts typed values from idnode params. |
| **ResilienceHandler** | `Service.Helper.ResiliencePolicies` | DelegatingHandler implementing retry with exponential back-off and circuit breaker. |
| **PluginMetrics** | `Service.Helper.PluginMetrics` | `System.Diagnostics.Metrics` instruments for API calls, durations, cache hits/misses. |

---

## 8 — Monitoring & Status

| Term | Class / Package | Description |
|------|----------------|-------------|
| **DiagnosticService** | `Service.Diagnostic.DiagnosticService` | Runs compatibility checks and produces a structured diagnostic report with score. |
| **StatisticsService** | `Service.Statistics.StatisticsService` | Tracks live TV viewing sessions via `ISessionManager` events; persists to JSON. |
| **StatusService** | `Service.Status.StatusService` | Reads TVHeadend server activity and active connections. |
| **CometService** | `Service.Comet.CometService` | Hosted service maintaining a WebSocket connection to TVHeadend's Comet endpoint; buffers log messages and disk-space updates for the admin UI. |
| **InputMonitorService** | `Service.Input.InputMonitorService` | Monitors TV tuner signal quality (signal strength, BER, SNR, bitrate). |
| **SubscriptionService** | `Service.Subscription.SubscriptionService` | Lists active streaming subscriptions in TVHeadend. |

