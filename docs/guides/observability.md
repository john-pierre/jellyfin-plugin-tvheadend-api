# Observability

This document describes the logging, metrics, and tracing capabilities of the TVHeadend API plugin.

## Structured Logging

All services use `ILogger<T>` with semantic message templates (e.g., `{ChannelId}`, `{ElapsedMs}`).
Log output follows Jellyfin's Serilog pipeline — no additional configuration is needed.

### Key Log Categories

| Logger Category | Level | What it logs |
|---|---|---|
| `OrchestratorService` | Debug/Info | Channel fetch, EPG fetch, stream setup timing |
| `GuideService` | Debug/Error | Channel + EPG grid fetch, content types, tags |
| `DvrService` | Debug/Error | Timer/series timer CRUD operations |
| `MediaSourceService` | Info/Warning | Stream URL construction, media source building |
| `MediaInfoCacheService` | Info/Warning/Debug | Cache read/write/validation/invalidation, per-profile store restore/seed, cached stream-URL refresh |
| `TokenService` | Info/Warning/Error | Auth token generation, validation, retry attempts |
| `StatisticsService` | Info/Debug/Warning | Session tracking, persistence, retention cleanup |
| `ProfileContainerResolver` | Info/Warning | Profile resolution, container mapping |
| `DefaultProfileService` | Info/Warning/Error | Managed `jellyfin` profile creation, transcode self-verification, libx264 fallback |
| `ChannelNameCacheWarmupService` | Info/Debug/Warning | Startup channel-name cache warmup attempts |
| `LifecycleService` | Debug | Stream close, tuner reset |

### Adjusting Log Levels

Use Jellyfin's standard logging configuration (`logging.json` or environment variables):

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Override": {
        "Jellyfin.Plugin.TvHeadendApi": "Debug"
      }
    }
  }
}
```

## Metrics

The plugin exposes metrics via .NET's built-in `System.Diagnostics.Metrics` API.
No external dependencies are required. Metrics can be consumed by:

- **`dotnet-counters`** — CLI tool for real-time monitoring.
- **OpenTelemetry** — export to Prometheus, Grafana, etc.
- **Any `MeterListener`** — programmatic access.

### Meter Name

```
Jellyfin.Plugin.TvHeadendApi
```

### Available Instruments

| Instrument | Type | Unit | Description | Populated? |
|---|---|---|---|---|
| `tvh.cache.hit` | Counter | hits | Mediainfo cache hits — an existing, matching cache file was used; includes stream-build reuse hits | Yes |
| `tvh.cache.miss` | Counter | misses | Mediainfo cache misses — no cache file existed for the channel | Yes |
| `tvh.cache.mismatch` | Counter | mismatches | Mediainfo cache profile mismatches — a file existed but did not match the effective profile (previously miscounted as hits) | Yes |
| `tvh.cache.invalidation` | Counter | invalidations | Mediainfo cache invalidations — stale, mismatching, or unreadable file deleted (all delete paths) | Yes |
| `tvh.stream.setup` | Counter | requests | Channel stream setup requests (media source builds) | Yes |
| `tvh.stream.setup.duration` | Histogram | ms | Stream setup duration including mediainfo cache reconciliation | Yes |
| `tvh.api.calls` | Counter | calls | Total TVHeadend HTTP API calls | **Defined but not yet recorded** |
| `tvh.api.duration` | Histogram | ms | TVHeadend API call duration | **Defined but not yet recorded** |
| `tvh.epg.fetch.duration` | Histogram | ms | EPG data fetch duration per channel | **Defined but not yet recorded** |
| `tvh.channels.fetch` | Counter | calls | Channel list fetch count | **Defined but not yet recorded** |

> Note: the cache counters and stream-setup instruments are recorded by
> `Service/Stream/MediaInfoCacheService.cs` and `Service/Stream/MediaSourceService.cs`.
> The `tvh.api.*`, `tvh.epg.*`, and `tvh.channels.*` instruments exist in
> `Service/Metrics/MetricService.cs` but no call site records to them yet — they report
> zero in `dotnet-counters`/OTEL until wired up.
>
> The mediainfo cache counters are additionally mirrored as plain in-process counters and
> exposed on `GET /TvHeadendApi/Dashboard` (`MediaInfoCache` object), so the admin dashboard
> shows them without a metrics listener. Stream/relay request telemetry shown on the admin
> dashboard is persisted per request in the SQLite `relay_request_metric` table (the single
> persistent stream-telemetry store — the former `completed_stream_sessions`/`relay_events`/
> `active_stream_sessions` tables were removed); each stream row carries the effective
> profile, the profile resolution source, the mediainfo cache outcome
> (`hit`/`miss`/`mismatch`/`restored`/`unknown`), and the stream setup duration, enabling the
> dashboard's warm-vs-cold-cache startup latency breakdown.

### Monitoring with `dotnet-counters`

```bash
dotnet-counters monitor --process-id <jellyfin-pid> --counters "Jellyfin.Plugin.TvHeadendApi"
```

### Monitoring with OpenTelemetry

If Jellyfin is configured with an OpenTelemetry exporter, add the meter name to the exporter configuration:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("Jellyfin.Plugin.TvHeadendApi"));
```

## Resilience Observability

The custom retry and circuit breaker logic (see `ResilienceHandler` in `Service/Resilience/ResiliencePolicies.cs`) is applied as a `DelegatingHandler` in the `HttpClient` pipeline. No external resilience library (e.g., Polly) is used — the implementation is self-contained to avoid assembly-loading issues in Jellyfin's plugin host.

| Event | How to observe |
|---|---|
| Retry attempt | `ResilienceHandler` retries silently; check plugin logs (the `tvh.api.calls` counter is not yet recorded) |
| Circuit open | `ResilienceHandler` throws `InvalidOperationException` with "Circuit breaker is open" message |
| Circuit half-open | After the break duration elapses, one trial request is allowed through |

## Troubleshooting

1. **Slow channel switching:** Check the `tvh.cache.miss` counter and the dashboard's startup-latency metrics. A high miss rate means channels lack mediainfo cache files — keep `Supports Probing` enabled (cache pre-creation is coupled to it; the old `EnableMediaInfoCacheWrite` toggle is deprecated) and use the Warm Cache action.
2. **TVHeadend connectivity issues:** Check plugin logs for retry/circuit-breaker messages (the `tvh.api.*` instruments are not yet recorded).
3. **EPG loading slow:** Check `GuideService` debug logs for fetch timing. Large EPG windows increase fetch time.

