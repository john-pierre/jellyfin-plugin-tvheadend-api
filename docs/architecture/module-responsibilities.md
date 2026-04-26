# Module Responsibilities

Quick reference for what each module owns and its boundaries.

## Service Modules

### Service/Guide (`GuideService`)

- **Owns:** Channel listing, EPG program fetching, content type mapping, channel tag mapping.
- **Boundary:** Receives raw TVHeadend grid responses, maps to Jellyfin `ChannelInfo` / `ProgramInfo`.
- **Does not:** Handle stream URLs, DVR operations, or authentication.

### Service/Dvr (`DvrService`, `SingleTimerService`, `SeriesTimerService`)

- **Owns:** Single timer CRUD (`SingleTimerService`), series timer CRUD (`SeriesTimerService`), recording profile UUID lookup (`DvrService`).
- **Boundary:** Maps between Jellyfin `TimerInfo` / `SeriesTimerInfo` and TVHeadend DVR entry/autorec models.
- **Does not:** Handle EPG, stream construction, or channel listing.

### Service/Stream (`MediaSourceService`, `LifecycleService`, `MediaInfoCacheService`)

- **Owns:** Stream URL construction, `MediaSourceInfo` building, mediainfo cache management (`MediaInfoCacheService`), stream close/tuner reset.
- **Boundary:** Produces Jellyfin `MediaSourceInfo` with correct path, container, codec hints.
- **Does not:** Fetch EPG data or manage timers.

### Service/Auth (`TokenService`, `TokenValidator`)

- **Owns:** Auth token generation via TVHeadend user API, token alphanumeric validation.
- **Boundary:** Interacts with TVHeadend idnode/user endpoints.
- **Does not:** Handle stream URLs or channel data.

### Service/Profile (`ProfileResolver`, `ProfileContainerResolver`, `DefaultProfileService`, `ProfileMappingHelper`)

- **Owns:** Resolving active streaming profile metadata (codec, container), creating default profiles in TVHeadend, mapping profile types to containers.
- **Boundary:** Reads TVHeadend profile grid/idnode APIs, produces `ProfileSnapshot` / `ResolvedProfile`.
- **Does not:** Build stream URLs or manage cache files.

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

### Service/Metric (`MetricService`)

- **Owns:** `System.Diagnostics.Metrics` instruments for API calls, durations, cache hits/misses.
- **Boundary:** Provides metric counters and histograms consumed by `dotnet-counters` or OpenTelemetry.
- **Does not:** Contain business logic or make HTTP calls.

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

### Service/Logging (`PluginLogService`, `LogParser`, `LogSanitizer`, `PluginLoggerFactory`)

- **Owns:** Plugin log querying, log parsing, credential sanitization, custom logger factory.
- **Boundary:** Reads and filters Jellyfin log output relevant to the plugin.
- **Does not:** Modify Jellyfin's logging pipeline configuration.

### Service/Relay (`RelayService`, `RelayUrlBuilder`, `RelayTokenService`, `RelayMetricsService`, etc.)

- **Owns:** Stream relay URL construction, relay token generation/validation/cleanup, relay activity tracking, relay metrics.
- **Boundary:** Provides an alternative stream path where clients connect through Jellyfin instead of directly to TVHeadend.
- **Does not:** Handle direct TVHeadend streaming or profile resolution.

### Service/Dashboard (`DashboardService`)

- **Owns:** Aggregating diagnostics, status, input, and subscription data for the admin dashboard.
- **Boundary:** Reads from `DiagnosticService`, `StatusService`, `InputMonitorService`, `SubscriptionService`, `CometService`.
- **Does not:** Expose REST endpoints directly (that is `PluginController`'s responsibility).

## Non-Service Modules

### Model/{domain}

- **Owns:** Data shapes for TVHeadend API responses and plugin result objects.
- **Rule:** No logic beyond simple property declarations. Some use `record` types.

### Configuration

- **Owns:** `PluginConfiguration` (all plugin settings with defaults) and `ConfigPage.html` (admin UI).
- **Rule:** Configuration is a passive model. Validation is in services, not in the config class.

### Api

- **Owns:** REST endpoints for admin UI, split across multiple controllers: `PluginController` (config, diagnostics, profiles, auth), `StatisticsController` (viewing statistics), `MonitoringController` (status, connections, inputs, subscriptions, health), `DashboardController` (aggregated dashboard data, relay metrics), `StreamingProfileController` (discovery, resolution, validation, channels, groups), `LogsController` (logs, disk space), `DashboardLogsController` (filtered log queries), `RelayController` (stream/image proxy, token security, status).
- **Rule:** Thin controllers — delegate to services. No business logic.

### Plugin.cs / ServiceRegistrator.cs

- **Owns:** Plugin lifecycle, DI registration.
- **Rule:** No business logic. Changes only when adding/removing services.
