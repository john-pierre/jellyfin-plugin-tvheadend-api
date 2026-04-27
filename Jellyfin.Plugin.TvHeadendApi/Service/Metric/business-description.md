# Metric Service

Centralized `System.Diagnostics.Metrics` instrumentation for plugin observability.

## Detailed Description

`MetricService` is a static class that defines a shared `Meter` (`Jellyfin.Plugin.TvHeadendApi`) and pre-built instruments for key plugin operations. No external dependencies — uses the built-in .NET metrics API.

**Instruments:**

| Instrument | Type | Description |
|---|---|---|
| `tvh.api.calls` | Counter | Total TVHeadend HTTP API calls |
| `tvh.api.duration` | Histogram | TVHeadend API call duration (ms) |
| `tvh.stream.setup` | Counter | Channel stream setup requests |
| `tvh.stream.setup.duration` | Histogram | Stream setup duration including cache (ms) |
| `tvh.cache.hit` | Counter | Mediainfo cache hits |
| `tvh.cache.miss` | Counter | Mediainfo cache misses |
| `tvh.cache.invalidation` | Counter | Mediainfo cache invalidations |
| `tvh.epg.fetch.duration` | Histogram | EPG data fetch duration (ms) |
| `tvh.channels.fetch` | Counter | Channel list fetch count |

Also exposes an `ActivitySource` for distributed tracing spans.

Metrics can be consumed by `dotnet-counters`, OpenTelemetry exporters, or any .NET metrics listener.

## Domain Context

- **Use Case:** Plugin-wide observability for performance monitoring and diagnostics
- **Module Type:** Infrastructure (Static)
- **Key Domain Entities:** Meter, Counter, Histogram, ActivitySource
