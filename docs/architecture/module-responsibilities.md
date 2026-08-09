# Module Responsibilities

Quick reference for what each module owns and its boundaries.

## Service Modules

### Service/Guide (`GuideService`, `ChannelNameCache`, `ChannelNameCacheWarmupService`)

- **Owns:** Channel listing, EPG program fetching, content type mapping, channel tag mapping. Also the channel-name cache used by telemetry (`ChannelNameCache`, filled as a side effect of channel fetches) and its startup warmup (`ChannelNameCacheWarmupService`, an `IHostedService` that retries with back-off so sessions recorded right after a restart show channel names instead of raw UUIDs).
- **Boundary:** Receives raw TVHeadend grid responses, maps to Jellyfin `ChannelInfo` / `ProgramInfo`.
- **Does not:** Handle stream URLs, DVR operations, or authentication.

### Service/Dvr (`DvrService`, `SingleTimerService`, `SeriesTimerService`)

- **Owns:** Single timer CRUD (`SingleTimerService`), series timer CRUD (`SeriesTimerService`), recording profile UUID lookup (`DvrService`).
- **Boundary:** Maps between Jellyfin `TimerInfo` / `SeriesTimerInfo` and TVHeadend DVR entry/autorec models.
- **Does not:** Handle EPG, stream construction, or channel listing.

### Service/Stream (`MediaSourceService`, `LifecycleService`, `MediaInfoCacheService`)

- **Owns:** Stream URL construction, `MediaSourceInfo` building, mediainfo cache management (`MediaInfoCacheService`), stream close/tuner reset.
- **Cache specifics (`MediaInfoCacheService`):** reconciles the live Jellyfin cache file on every stream start (hit/miss/invalidation), refreshes the cached `Path` with the current stream URL (fresh relay token / delivery-mode shape), and maintains the per-(channel × profile) store under `cache/mediainfo/profiles/<channel>.<profileKey>.json` — outgoing files are preserved before a profile switch and restored when a rule switches back; warmup pre-seeds the store for all rule-reachable profile combinations.
- **Boundary:** Produces Jellyfin `MediaSourceInfo` with correct path, container, codec hints.
- **Does not:** Fetch EPG data or manage timers.

### Service/Auth (`TokenService`, `TokenValidator`)

- **Owns:** Auth token generation via TVHeadend user API, token alphanumeric validation.
- **Boundary:** Interacts with TVHeadend idnode/user endpoints.
- **Does not:** Handle stream URLs or channel data.

### Service/Profile (`ProfileResolver`, `ProfileContainerResolver`, `DefaultProfileService`, `ProfileMappingHelper`)

- **Owns:** Resolving active streaming profile metadata (codec, container), mapping profile types to containers, and creating/maintaining the managed `jellyfin` TVHeadend transcode profile (`DefaultProfileService`): encoder auto-detection (hardware preferred), self-verification via a short test stream, automatic libx264 fallback when the encoder delivers no data, and the `sid=1`/`rewrite_nit` MPEG-TS muxer workaround (see ADR-009).
- **Boundary:** Reads TVHeadend profile grid/idnode APIs, produces `ProfileSnapshot` / `ResolvedProfile`.
- **Does not:** Build stream URLs or manage cache files.

### Service/StreamingProfile (`StreamingProfileResolver`, `ProfileDiscoveryService`, `PlaybackContextAccessor`, `KnownClientsService`)

- **Owns:** Hierarchical streaming-profile selection (channel → group → client → user → global → fallback), TVHeadend profile discovery with TTL caching, playback context enrichment (client/device/user), known-client tracking.
- **Boundary:** Produces `StreamingProfileResolutionResult` with the effective TVHeadend profile and playback mode consumed by stream services.
- **Does not:** Build stream URLs, create TVHeadend profiles, or manage cache files.

### Service/Diagnostic (`DiagnosticService`, `EncodingOptionsReader`)

- **Owns:** Compatibility checks, configuration analysis, structured diagnostic reports.
- **Boundary:** Reads plugin config + TVHeadend server info + profile data + Jellyfin encoding options.
- **Does not:** Modify configuration or create profiles.

### Service/Statistic (`StatisticsService`)

- **Owns:** Tracking live TV viewing sessions, persisting to SQLite via `ViewingSessionContext` (EF Core), retention cleanup.
- **Boundary:** Listens to Jellyfin `ISessionManager` playback events.
- **Does not:** Interact with TVHeadend.

### Service/Status (`StatusService`)

- **Owns:** TVHeadend server activity status, active connection listing.
- **Boundary:** Reads `/api/status/activity` and `/api/status/connections`.
- **Does not:** Monitor inputs or subscriptions (separate services).

### Service/Input (`InputMonitorService`)

- **Owns:** TV input/tuner status monitoring (signal, BER, SNR, bitrate).
- **Boundary:** Reads `/api/status/inputs`.
- **Does not:** Manage subscriptions or connections.

### Service/Subscription (`SubscriptionService`)

- **Owns:** Active streaming subscription listing.
- **Boundary:** Reads `/api/status/subscriptions`.
- **Does not:** Manage connections or input status.

### Service/Comet (`CometService`)

- **Owns:** Real-time Comet/WebSocket connection, dashboard log buffering, disk-space update buffering.
- **Owns:** `ICometSnapshotReader`, the read-only snapshot contract consumed by dashboard endpoints.
- **Boundary:** Connects to `/comet/ws` using the configured TVHeadend web root, SSL mode, and authentication settings.
- **Does not:** Expose dashboard endpoints directly or perform polling-based status reads.

### Service/Backend (`ApiClient`, `UrlBuilder`, `GridFetcher`, `IdNodeValueHelper`)

- **Owns:** HTTP client creation, URL building (base URL, auth variants), paginated grid fetching, idnode value extraction.
- **Boundary:** Generic TVHeadend HTTP infrastructure — no domain logic.
- **Does not:** Contain business rules, mapping logic, or domain-specific decisions.

### Service/Resilience (`ResiliencePolicies`, `FailureClassifier`)

- **Owns:** Retry with exponential back-off and circuit breaker policies, failure classification.
- **Boundary:** Applied as `DelegatingHandler` in the `HttpClient` pipeline.
- **Does not:** Contain domain logic or make business decisions.

### Service/Metrics (`MetricService`, `SessionTracker`, `ActiveSessionStore`, `MetricsAggregator`, `StreamBitrateTracker`, `StreamingDashboardService`)

- **Owns:** Relay streaming telemetry for the admin dashboard — in-memory session lifecycle tracking (start/update/finalize with outcome classification), rolling/peak bitrate computation, read-only aggregation over the consolidated `relay_request_metric` table — plus the `System.Diagnostics.Metrics` instruments (`MetricService`; the former singular `Service/Metric/` folder was merged in). Cache and stream-setup instruments are populated; the `tvh.api.*`/`tvh.epg.*`/`tvh.channels.*` instruments are defined but not yet wired.
- **Boundary:** Fed by the relay stream path; exposed via dashboard/metrics endpoints. Persistence of stream telemetry happens exactly once per request through `RelayMetricsService` (Service/Relay) — this module never writes to the database.
- **Does not:** Contain HTTP calls or own database writes.

### Service/Configuration (`ConfigurationProvider`, `ConfigurationSaver`)

- **Owns:** Resolving current `PluginConfiguration` and persisting configuration changes.
- **Boundary:** Bridges `Plugin.Instance` for testability; services receive these via constructor injection.
- **Does not:** Contain validation logic or domain rules.

### Service/Storage (`CachePathProvider`, `DataFolderPathProvider`)

- **Owns:** Resolving plugin cache and data folder paths.
- **Boundary:** Bridges `Plugin.Instance` path accessors for testability.
- **Does not:** Manage files or perform I/O beyond path resolution.

### Service/Common (`JsonDefaults`)

- **Owns:** Shared JSON serialization defaults.
- **Boundary:** Provides reusable `JsonSerializerOptions` configuration.
- **Does not:** Contain domain-specific logic.

### Service/Database (`DatabaseProvider`, `DatabaseMigrationService`, `DatabaseHealthService`, `DatabaseCleanupService`, etc.)

- **Owns:** SQLite database lifecycle, schema migrations, health monitoring, cleanup, recovery, write coordination.
- **Boundary:** Provides database connections and manages the underlying SQLite store used by `ViewingSessionContext` and relay modules.
- **Does not:** Contain domain-specific business logic.

### Service/Health (`HealthService`)

- **Owns:** Aggregated plugin health state.
- **Boundary:** Combines health signals from various services into an overall `HealthState`.
- **Does not:** Perform health checks itself — delegates to specialized services.

### Service/Logging (`PluginLogService`, `PluginPersistingLogger`, `PluginLogLevelPolicy`, `LogParser`, `LogSanitizer`)

- **Owns:** Plugin log persistence and querying, log parsing, credential sanitization, and the `ILogger<>` decorator that captures plugin-namespace entries.
- **Boundary:** Reads and filters Jellyfin log output relevant to the plugin.
- **Does not:** Modify Jellyfin's logging pipeline configuration.

### Service/Relay (`RelayService`, `RelayUrlBuilder`, `RelayTokenService`, `RelayMetricsService`, etc.)

- **Owns:** Stream relay URL construction, relay token generation/validation/cleanup, relay activity tracking, relay metrics.
- **Boundary:** Provides an alternative stream path where clients connect through Jellyfin instead of directly to TVHeadend.
- **Does not:** Handle direct TVHeadend streaming or profile resolution.

### Service/Dashboard (`DashboardService`)

- **Owns:** Aggregating diagnostics, status, input, and subscription data for the admin dashboard.
- **Boundary:** Reads from `DiagnosticService`, `StatusService`, `InputMonitorService`, `SubscriptionService`, `CometService`.
- **Does not:** Expose REST endpoints directly (that is `DashboardController`'s responsibility).

## Non-Service Modules

### Model/{domain}

- **Owns:** Data shapes for TVHeadend API responses and plugin result objects.
- **Rule:** No logic beyond simple property declarations. Some use `record` types.

### Configuration

- **Owns:** `PluginConfiguration` (all plugin settings with defaults) and `ConfigPage.html` (admin UI).
- **Rule:** Configuration is a passive model. Validation is in services, not in the config class.

### Api

- **Owns:** REST endpoints for admin UI, split across multiple controllers: `PluginController` (config, diagnostics, profiles, auth), `StatisticsController` (viewing statistics), `MonitoringController` (status, connections, inputs, subscriptions, health), `DashboardController` (aggregated dashboard data, relay metrics), `StreamingProfileController` (discovery, resolution, validation, channels, groups), `LogsController` (logs, disk space), `DashboardLogsController` (filtered log queries), `MetricsController` (live and historical streaming telemetry), `RelayController` (stream/image proxy, token security, status).
- **Rule:** Thin controllers — delegate to services. No business logic.

### Plugin.cs / ServiceRegistrator.cs

- **Owns:** Plugin lifecycle, DI registration.
- **Rule:** No business logic. Changes only when adding/removing services.
