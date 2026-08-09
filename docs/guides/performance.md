# Performance

The consolidated performance reference for the plugin.

## Direct Play Model — why this plugin exists

The whole design optimizes for **Direct Play**: when it is active the stream URL is handed to the
client unchanged and Jellyfin's processing path is bypassed for the media flow, enabling roughly
2-second channel switching. Any non-Direct-Play path (Direct Stream or Transcode) hits hard-coded
Jellyfin-core live-TV analysis waits of about 6 seconds minimum.

The levers that keep playback on the Direct Play path are:

- the managed `jellyfin` TVHeadend profile,
- probing, and
- MediaInfo cache pre-creation (`MediaSourceService` writes `cache/mediainfo/*.json`).

> **Do not make changes that push clients off Direct Play without flagging it.**

Which URL the client receives is a separate axis: in the default `Relay` delivery mode the client
plays a token-secured plugin URL and Jellyfin proxies the bytes; in `Direct to TVHeadend` mode the
client connects straight to TVHeadend. Both are Direct Play — the relay adds a proxy hop, not a
re-encode.

## Relay Hot Path

The relay proxies live TV streams from TVHeadend through Jellyfin. It is the highest-throughput
code path in the plugin (`Service/Relay/RelayService.cs`).

- **Zero-copy**: stream directly from the TVHeadend response to the client response. Never buffer
  an entire response in memory.
- **No heap allocations in the streaming loop** — no `new`, no LINQ, no string operations, no
  closures. Prefer `ArrayPool<byte>` over per-chunk `byte[]`.
- **No blocking calls** — all I/O async. No `.Result`, no `.Wait()`.
- **No logging inside the per-chunk loop** — log at stream start and end only.
- **Buffer sizing** matters in both directions: too small costs syscalls, too large wastes memory.
- **Metric persistence must not block the relay** — `RelayMetricsService` persists fire-and-forget
  on the ThreadPool.
- `RelayService` manages socket-handler lifecycle separately from auth handlers so connection
  pooling survives.
- All relay endpoints validate their token before proxying (see `docs/guides/security.md`).

## Grid Fetcher

`GridFetcher` handles paginated TVHeadend grid responses (channels, EPG, DVR entries):

- Uses **cached `UTF8Encoding` instances** for DVB string sanitization — do not construct new ones
  per call.
- Probe-then-fetch: the first request determines the total count, subsequent requests fetch the
  remaining pages. Keep the probe lightweight.

## Database Writes

SQLite supports only one writer at a time:

- **All writes go through `DatabaseWriteCoordinator`**, which serializes them via
  `SemaphoreSlim(1, 1)`.
- Writers: `StatisticsService`, `RelayTokenRepository`, `RelayMetricsService`,
  `DatabaseCleanupService`, `PluginLogService`.
- Context pooling is disabled — contexts are created per operation because writes are serialized.
- Cleanup services must not hold the write lock for long operations.
- Raw-SQL cutoffs must be formatted with `SqliteDateTimeFormat` so they compare correctly against
  the TEXT that EF Core wrote. See `docs/architecture/decisions.md`.

## Configuration Reads

- Read configuration through the injected `ConfigurationProvider`.
- **Do not re-read configuration per request** — the provider caches the current config.
- Never call `Plugin.Instance.Configuration` directly in a service.

## HTTP Client Management

- `ApiClient` builds the configured `HttpClient` with the chain
  `ResilienceHandler → DigestAuthHandler → HttpClientHandler`.
- Do not create ad-hoc `HttpClient` instances — use the shared `ApiClient`.
- `DigestAuthHandler` can cause a double request on first call (401 + retry); nonce caching matters.

## JSON Serialization

- Use the shared `JsonDefaults.Api` options (`Service/Common/JsonDefaults.cs`).
- **Never construct new `JsonSerializerOptions` per call** — they are expensive and meant to be
  reused.

## Async Discipline

- No blocking I/O in async paths — no `.Result`, `.Wait()`, `.GetAwaiter().GetResult()`.
- `ConfigureAwait(false)` on awaited calls in library code.
- Forward `CancellationToken` everywhere.
- Do not wrap pass-through methods in `async`/`await` — return the `Task` directly.

## Health Check

- `HealthService` probes TVHeadend with a lightweight `/api/serverinfo` request.
- Keep the timeout short so health aggregation does not block.
- Circuit breaker (`ResilienceHandler`): 5 consecutive failures open the circuit for 30 seconds.
- Callers should check `IHealthService` before issuing TVHeadend calls, and use `OperationTimeouts`
  appropriate to the operation type.

## Caching

| Cache | Location | TTL |
|---|---|---|
| Profile metadata | `ProfileContainerResolver` | Configurable; guessed fallbacks use a short negative TTL |
| Profile discovery | `ProfileDiscoveryService` | TTL-based |
| MediaInfo | `MediaSourceService` (Jellyfin cache dir) | File-based |
| Comet state | `CometService` (in-memory) | Continuously refreshed |

No other shared mutable state exists across services.

## Performance Review Checklist

When reviewing a change for performance impact:

1. Allocations in the relay streaming loop (`new`, LINQ, string ops, closures).
2. `byte[]` allocations that could use `ArrayPool<byte>`.
3. `Stream.CopyToAsync` versus a manual read/write loop — which fits this case?
4. Write batching opportunities behind `DatabaseWriteCoordinator`.
5. Unnecessary writes (per-chunk instead of per-request metrics).
6. Index usage on frequently queried columns.
7. Unbounded state growth in singletons — caches without eviction, lists that only append.
8. Regex without `RegexOptions.Compiled` on a hot path (e.g. `LogSanitizer`).
9. Missing `ConfigureAwait(false)` in library code.
10. Sequential awaits that could be `Task.WhenAll`.

Report each finding as: **Location** (file and method) → **Issue** → **Impact** (latency, memory,
throughput) → **Recommendation** → **Risk** if applied incorrectly.

## Related

- `docs/guides/security.md` — relay token validation, credential handling
- `docs/architecture/overview.md` — layering and module map
- `docs/architecture/decisions.md` — ADRs behind the profile and relay design
