# Relay Service

Zero-copy HTTP relay proxy that streams TVHeadend images and live TV to Jellyfin clients, with token-based security, metrics persistence, and live activity tracking.

## Detailed Description

The Relay module proxies TVHeadend resources through Jellyfin so that clients never receive direct TVHeadend URLs or credentials.

### Relay Core

`RelayService` forwards GET requests to TVHeadend using `HttpCompletionOption.ResponseHeadersRead` for zero-copy body passthrough — the upstream response stream is returned directly without buffering. It manages a `SocketsHttpHandler` tuned for LAN connection reuse (30-min pool lifetime, 20 max connections, keep-alive pings). The handler pipeline uses two-tier fingerprinting: socket-level config changes (host/port/SSL) trigger a full TCP pool rebuild, while auth-only changes (username/password) rebuild just the `DigestAuthHandler` wrapper — preserving the connection pool. New pools are warmed with parallel probe requests to eliminate cold-start latency. Separate `HttpClient` instances with different timeouts serve images (10s) and streams (24h). The circuit breaker from `HealthService` blocks requests when TVHeadend is known to be unreachable.

`RelayResult` carries upstream response metadata (status, content-type, ETag, cache-control) and the body stream without ownership transfer — the underlying `HttpResponseMessage` is disposed when the result is disposed.

### URL Building

`RelayUrlBuilder` constructs Jellyfin-local relay URLs (`/api/tvheadend/images/...`, `/api/tvheadend/stream/...`) using `IServerApplicationHost` for auto-detected base URL with an optional manual override. When relay token security is enabled, it issues scoped tokens via `RelayTokenService` and appends them as `?token=` query parameters.

### Token Security

`RelayTokenService` issues scoped, short-lived tokens for stream and image relay URLs. Raw tokens are returned to the client but never stored — only HMAC-SHA256 hashes are persisted in SQLite via `RelayTokenRepository`.

`RelayTokenValidatorService` validates tokens at public relay endpoints with an 8-step pipeline: presence check, well-formedness, hash lookup, expiry (with clock skew tolerance), revocation, max-use enforcement, relay-type matching, and optional strict scope validation. Use count is incremented atomically on success.

`RelayTokenHasher` generates cryptographically random base64url tokens and computes HMAC-SHA256 hashes using a stable server-side secret (pepper). Raw tokens are never persisted.

`RelayTokenOptions` reads token policy values (TTL, max uses, cleanup interval, clock skew, strict scope) from plugin configuration with enforced minimum bounds to prevent misconfiguration.

`RelayTokenCleanupService` is a hosted service that periodically removes expired and revoked tokens from SQLite.

### Metrics & Activity

`RelayMetricsService` persists per-request relay metrics to SQLite (fire-and-forget on ThreadPool) and provides aggregated dashboard summaries: latency percentiles, bandwidth, cache hit ratios, failure distributions, hourly trends, and slowest/error request lists.

`RelayActivityTracker` is a lightweight thread-safe counter for currently active relay streams, using `Interlocked` operations with floor-at-zero decrement.

### Persistence

`RelayTokenRepository` is a SQLite-backed repository for relay token CRUD with all writes serialized via `DatabaseWriteCoordinator`. `RelayTokenDbContext` and `RelayMetricsContext` are EF Core DbContexts with lowercase_with_underscore column conventions; schema creation is owned by `DatabaseMigrationService`.

## Domain Context

- **Use Case:** Secure, high-performance proxy for TVHeadend streams and images through Jellyfin
- **Module Type:** Service + Infrastructure
- **Key Domain Entities:** RelayResult, RelayTokenRecord, RelayRequestMetric, RelayTimingContext, RelayTokenValidationResult

## Internal Dependencies

- **`Backend`** — `IUrlBuilder` for constructing upstream TVHeadend URLs
- **`Auth`** — `DigestAuthHandler` for authenticated upstream requests
- **`Health`** — `IHealthService` for circuit breaker and failure recording
- **`Configuration`** — `ConfigurationProvider` for plugin settings
- **`Database`** — `DatabaseHealthService`, `DatabaseWriteCoordinator`, `DatabaseMigrationService` for SQLite persistence
