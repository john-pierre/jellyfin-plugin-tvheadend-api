# Performance Rules

## Relay Hot Path

The relay service proxies live TV streams from TVHeadend through Jellyfin. This is the highest-throughput code path:

- **Zero-copy streaming**: relay data directly from TVHeadend response stream to Jellyfin client response stream — no intermediate buffering of entire payloads
- **No unnecessary allocations**: avoid creating objects per-chunk in the relay loop
- **HTTP client reuse**: `RelayService` manages socket handler lifecycle separately from auth handlers to preserve connection pooling

## Grid Fetcher

`GridFetcher` handles paginated TVHeadend grid API responses (channels, EPG, DVR entries):

- Uses **cached `UTF8Encoding` instances** for DVB string sanitization — do not create new instances per call
- Probe-then-fetch: first request determines total count, subsequent requests fetch remaining pages

## Database Writes

SQLite supports only one writer at a time:

- **All writes must go through `DatabaseWriteCoordinator`** which serializes write operations via `SemaphoreSlim(1, 1)`
- Services that write: `StatisticsService`, `RelayTokenRepository`, `RelayMetricsService`, `DatabaseCleanupService`
- DB context pooling is disabled — contexts are created per-operation because writes are serialized

## Configuration Reads

- Use the injected `ConfigurationProvider` to read plugin configuration
- **Do not re-read configuration per request** — the provider caches the current config
- Never call `Plugin.Instance.Configuration` directly in services

## HTTP Client Management

- `ApiClient` creates a properly configured `HttpClient` with the handler chain: `ResilienceHandler` -> `DigestAuthHandler` -> `HttpClientHandler`
- Do not create ad-hoc `HttpClient` instances — always use the shared `ApiClient`
- Connection pooling is handled by the underlying `HttpClientHandler`

## JSON Serialization

- Use the shared `JsonDefaults.Api` options (`Service/Common/JsonDefaults.cs`) for all TVHeadend API serialization
- **Never create new `JsonSerializerOptions` instances** per call — `System.Text.Json` options are expensive to construct and should be reused

## Async Discipline

- No blocking I/O in async paths — no `.Result`, `.Wait()`, `.GetAwaiter().GetResult()` on tasks
- `ConfigureAwait(false)` on all awaited calls (library code, not on UI thread)
- Forward `CancellationToken` everywhere

## Health Check

- `HealthService` uses a lightweight `/api/serverinfo` probe to check TVHeadend connectivity
- Configurable timeout — keep it short to avoid blocking health aggregation
- Circuit breaker pattern (via `ResilienceHandler`): 5 failures open the circuit for 30 seconds

## Caching

| Cache | Location | TTL |
|-------|----------|-----|
| Profile metadata | `ProfileContainerResolver` | Configurable |
| Profile discovery | `ProfileDiscoveryService` | TTL-based |
| MediaInfo | `MediaSourceService` (Jellyfin cache dir) | File-based |
| Comet state | `CometService` (in-memory) | Continuously refreshed |

No other shared mutable state across services.
