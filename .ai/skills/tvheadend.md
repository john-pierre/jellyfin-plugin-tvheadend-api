# TVHeadend Skill

TVHeadend HTTP API integration for jellyfin-plugin-tvheadend-api.

## Overview

All TVHeadend communication uses HTTP/JSON endpoints only (no HTSP protocol). The integration layer lives primarily in `Service/Backend/` with authentication in `Service/Auth/`.

## API client

`ApiClient` (`Service/Backend/ApiClient.cs`) — implements `IApiClient`:

- Central HTTP client for all TVHeadend API calls.
- Uses named `HttpClient` instances from `IHttpClientFactory` (registered in `ServiceRegistrator.cs`):
  - `"TvHeadend"` — standard client with certificate revocation checks.
  - `"TvHeadendUnsafe"` — skips certificate validation for self-signed certs.
- All requests pass through `ResilienceHandler` for circuit breaker and retry support.

## Authentication

`DigestAuthHandler` (`Service/Auth/DigestAuthHandler.cs`):

- HTTP Digest authentication (RFC 2617) as a `DelegatingHandler`.
- Handles nonce management, response computation, and retry on 401.
- Credentials come from `PluginConfiguration`.

## Grid endpoints

`GridFetcher` (`Service/Backend/GridFetcher.cs`):

- TVHeadend grid endpoints return paginated data.
- Pattern: **probe + full fetch** — first request probes total count, then fetches all entries.
- Used for channels, EPG events, DVR entries, inputs, subscriptions, etc.

## IdNode API

`IdNodeValueHelper` (`Service/Backend/IdNodeValueHelper.cs`):

- TVHeadend's idnode API manages internal objects (profiles, users, access entries).
- Used by `ProfileResolver`, `DefaultProfileService`, and profile discovery.
- Handles the TVHeadend-specific JSON structure for reading and writing node properties.

## URL building

`UrlBuilder` (`Service/Backend/UrlBuilder.cs`) — implements `IUrlBuilder`:

- Constructs TVHeadend API URLs from the configured base URL.
- Handles path joining and query parameter encoding.

## Timeouts

`OperationTimeouts` (`Service/Resilience/OperationTimeouts.cs`):

- Defines timeout values per operation type (grid fetch, stream, config read, etc.).
- Used throughout the backend to set `HttpClient` timeouts and `CancellationToken` deadlines.

## Degraded mode

`HealthService` (`Service/Health/HealthService.cs`) — implements `IHealthService`:

- Tracks TVHeadend connectivity state via `HealthState`.
- Circuit breaker pattern: when TVHeadend is unreachable, blocks API calls to prevent cascading timeouts.
- Periodically checks connectivity and transitions back to healthy state.

`ResiliencePolicies` (`Service/Resilience/ResiliencePolicies.cs`):

- Defines retry and circuit breaker policies.
- `FailureClassifier` categorizes HTTP failures for policy decisions.

## Log parsing

`LogParser` (`Service/Logging/LogParser.cs`):

- Parses multiple TVHeadend log formats (syslog-style, internal format).
- Extracts timestamps, severity, subsystem, and message text.
- Used by the log viewing features in the dashboard.

## Comet (server-sent events)

`CometService` (`Service/Comet/CometService.cs`):

- Long-poll interface to TVHeadend's comet (server-push) endpoint.
- Receives real-time events: subscription changes, disk space updates, log messages.
- Event models: `DiskSpaceUpdate`, `LogMessage`.
- `ICometSnapshotReader` provides current state snapshots.

## Service domains that consume TVHeadend API

| Domain | Folder | What it fetches |
|--------|--------|----------------|
| Guide | `Service/Guide/` | EPG events |
| Dvr | `Service/Dvr/` | DVR entries and timers |
| Input | `Service/Input/` | Tuner adapter status |
| Subscription | `Service/Subscription/` | Active subscriptions |
| Status | `Service/Status/` | Server status and connections |
| Profile | `Service/Profile/` | Streaming profiles via idnode |
| StreamingProfile | `Service/StreamingProfile/` | Profile discovery and resolution |
| Storage | `Service/Storage/` | Disk space information |
| Statistic | `Service/Statistic/` | Historical statistics |

## Rules

- All TVHeadend communication goes through `IApiClient`. Never construct `HttpClient` calls directly.
- Always forward `CancellationToken` to API calls.
- Check `IHealthService` state before making API calls — respect degraded mode.
- Use `OperationTimeouts` for the appropriate timeout per operation type.
- Never log TVHeadend credentials — use `LogSanitizer` for any URL or header logging.
- Grid fetches must use the probe + full fetch pattern via `GridFetcher`.
