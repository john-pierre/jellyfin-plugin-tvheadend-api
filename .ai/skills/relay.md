# Relay Skill

HTTP stream relay for jellyfin-plugin-tvheadend-api.

## Overview

The relay subsystem proxies live TV streams from TVHeadend to Jellyfin clients. All relay code lives in `Service/Relay/` with API endpoints in `Api/RelayController.cs`.

## RelayService

`RelayService` (`Service/Relay/RelayService.cs`) — implements `IRelayService`:

- Zero-copy HTTP stream proxy: reads from TVHeadend upstream, writes directly to the client response stream.
- No intermediate buffering of stream data.
- Forwards `CancellationToken` for client disconnect detection.
- Returns `RelayResult` with status information.

**Hot path rules:**
- No heap allocations in the streaming loop.
- No blocking calls — all I/O is async.
- No logging inside the per-byte/per-chunk loop (log at start/end only).
- Stream data directly — never buffer entire responses in memory.

## Token security

Relay access is secured with HMAC-SHA256 scoped tokens:

| Component | File | Responsibility |
|-----------|------|---------------|
| `RelayTokenService` | `Service/Relay/RelayTokenService.cs` | Issues tokens with TTL, max uses, resource scope |
| `RelayTokenValidatorService` | `Service/Relay/RelayTokenValidatorService.cs` | Validates token signature, expiry, scope, usage count |
| `RelayTokenHasher` | `Service/Relay/RelayTokenHasher.cs` | HMAC-SHA256 computation |
| `RelayTokenOptions` | `Service/Relay/RelayTokenOptions.cs` | Configurable TTL, max uses, enabled flag |
| `RelayTokenRepository` | `Service/Relay/RelayTokenRepository.cs` | SQLite persistence via `RelayTokenDbContext` |
| `RelayTokenCleanupService` | `Service/Relay/RelayTokenCleanupService.cs` | `IHostedService` that removes expired tokens |

Token lifecycle:
1. `RelayTokenService.IssueStreamTokenAsync()` creates a token scoped to channel/user/device.
2. Token is embedded in the relay URL by `RelayUrlBuilder`.
3. `RelayTokenValidatorService` checks the token on each relay request.
4. `RelayTokenCleanupService` periodically purges expired tokens from the database.

## Metrics

`RelayMetricsService` (`Service/Relay/RelayMetricsService.cs`) — implements `IRelayMetricsService`:

- Persists per-request metrics (bytes transferred, duration, status) to SQLite via `RelayMetricsContext`.
- Used by the dashboard for relay statistics.

## Activity tracking

`RelayActivityTracker` (`Service/Relay/RelayActivityTracker.cs`):

- Thread-safe counter of active relay connections.
- Uses `Interlocked` operations for lock-free increment/decrement.
- Queried by dashboard and health services.

## URL building

`RelayUrlBuilder` (`Service/Relay/RelayUrlBuilder.cs`) — implements `IRelayUrlBuilder`:

- Constructs relay URLs with embedded authentication tokens.
- Handles URL encoding and path construction.

## Authorization

`RelayAuthorizationHelper` (`Api/RelayAuthorizationHelper.cs`):

- Validates relay requests at the controller level.
- Extracts and verifies token from request parameters.

## Rules

- All relay endpoints must validate tokens before proxying.
- Never buffer stream data — zero-copy only.
- Never allocate in the streaming hot path.
- All relay writes go through `DatabaseWriteCoordinator`.
- Tokens must be short-lived (configurable TTL) and scoped to a specific resource.
- Metric persistence must not block the stream relay.
