# Module Responsibilities

Quick reference for what each module owns and its boundaries.

## Service Modules

### Service/Guide (`GuideService`)

- **Owns:** Channel listing, EPG program fetching, content type mapping, channel tag mapping.
- **Boundary:** Receives raw TVHeadend grid responses, maps to Jellyfin `ChannelInfo` / `ProgramInfo`.
- **Does not:** Handle stream URLs, DVR operations, or authentication.

### Service/Dvr (`DvrService`)

- **Owns:** Single timer CRUD, series timer CRUD, recording profile UUID lookup.
- **Boundary:** Maps between Jellyfin `TimerInfo` / `SeriesTimerInfo` and TVHeadend DVR entry/autorec models.
- **Does not:** Handle EPG, stream construction, or channel listing.
- **Note:** Uses partial classes (`DvrService.SingleTimer.cs`, `DvrService.SeriesTimer.cs`) for file-size management.

### Service/Stream (`MediaSourceService`, `LifecycleService`)

- **Owns:** Stream URL construction, `MediaSourceInfo` building, mediainfo cache management, stream close/tuner reset.
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

### Service/Statistics (`StatisticsService`)

- **Owns:** Tracking live TV viewing sessions, persisting to JSON, retention cleanup.
- **Boundary:** Listens to Jellyfin `ISessionManager` playback events.
- **Does not:** Interact with TVHeadend.

### Service/Helper (`ApiClient`, `UrlBuilder`, `GridFetcher`, `IdNodeValueHelper`)

- **Owns:** HTTP client creation, URL building (base URL, auth variants), paginated grid fetching, idnode value extraction.
- **Boundary:** Generic TVHeadend HTTP infrastructure — no domain logic.
- **Does not:** Contain business rules, mapping logic, or domain-specific decisions.

## Non-Service Modules

### Model/{domain}

- **Owns:** Data shapes for TVHeadend API responses and plugin result objects.
- **Rule:** No logic beyond simple property declarations. Some use `record` types.

### Configuration

- **Owns:** `PluginConfiguration` (all plugin settings with defaults) and `ConfigPage.html` (admin UI).
- **Rule:** Configuration is a passive model. Validation is in services, not in the config class.

### Api

- **Owns:** `PluginController` — REST endpoints for admin UI.
- **Rule:** Thin controller — delegates to services. No business logic.

### Plugin.cs / ServiceRegistrator.cs

- **Owns:** Plugin lifecycle, DI registration.
- **Rule:** No business logic. Changes only when adding/removing services.

