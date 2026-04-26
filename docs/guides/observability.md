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
| `MediaSourceService` | Info/Warning | Stream URL construction, cache read/write/validation/invalidation |
| `TokenService` | Info/Warning/Error | Auth token generation, validation, retry attempts |
| `StatisticsService` | Info/Debug/Warning | Session tracking, persistence, retention cleanup |
| `ProfileContainerResolver` | Info/Warning | Profile resolution, container mapping |
| `DefaultProfileService` | Info/Warning/Error | Profile creation in TVHeadend |
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

| Instrument | Type | Unit | Description |
|---|---|---|---|
| `tvh.api.calls` | Counter | calls | Total TVHeadend HTTP API calls |
| `tvh.api.duration` | Histogram | ms | TVHeadend API call duration |
| `tvh.stream.setup` | Counter | requests | Channel stream setup requests |
| `tvh.stream.setup.duration` | Histogram | ms | Stream setup duration (including cache warm-up) |
| `tvh.cache.hit` | Counter | hits | Mediainfo cache hits |
| `tvh.cache.miss` | Counter | misses | Mediainfo cache misses (new file written) |
| `tvh.cache.invalidation` | Counter | invalidations | Mediainfo cache invalidations (stale file deleted) |
| `tvh.epg.fetch.duration` | Histogram | ms | EPG data fetch duration per channel |
| `tvh.channels.fetch` | Counter | calls | Channel list fetch count |

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
| Retry attempt | `ResilienceHandler` retries silently; monitor `tvh.api.calls` counter for repeated calls |
| Circuit open | `ResilienceHandler` throws `InvalidOperationException` with "Circuit breaker is open" message |
| Circuit half-open | After the break duration elapses, one trial request is allowed through |

## Troubleshooting

1. **Slow channel switching:** Check `tvh.stream.setup.duration` histogram and `tvh.cache.miss` counter. High miss rate means channels lack mediainfo cache files — enable `EnableMediaInfoCacheWrite`.
2. **TVHeadend connectivity issues:** Check `tvh.api.calls` and `tvh.api.duration`. Look for retry/circuit breaker log messages.
3. **EPG loading slow:** Check `tvh.epg.fetch.duration` histogram. Large EPG windows increase fetch time.

