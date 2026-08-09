# Metrics Services

Real-time stream session tracking, dashboard aggregation over the consolidated relay
telemetry table, and centralized `System.Diagnostics.Metrics` instrumentation.

## Detailed Description

**Session tracking (in-memory):** `ActiveSessionStore` + `SessionTracker` manage the
live view of ongoing relay streams (channel, client, rolling/average/peak bitrate,
startup latency, lifecycle state). Sessions exist only in memory — persistence happens
exactly once per stream request through `RelayTimingContext.ToMetric()` →
`RelayMetricsService.RecordMetric()` into the `relay_request_metric` table.
`StreamBitrateTracker` computes an honest ~10-second rolling bitrate window and the
peak of those samples inside the relay drain loop.

**Aggregation (read-only):** `MetricsAggregator` + `StreamingDashboardService` serve
`GET /TvHeadendApi/Metrics/{Live,History,Session/{id}}`. Historical queries read the
`relay_request_metric` table (stream rows) — the former parallel
`completed_stream_sessions` / `relay_events` / `active_stream_sessions` tables were
removed.

**Instrumentation:** `MetricService` is a static class that defines a shared `Meter`
(`Jellyfin.Plugin.TvHeadendApi`) and pre-built instruments. No external dependencies —
uses the built-in .NET metrics API. See `docs/guides/observability.md` for the
instrument table and which instruments are actually recorded.

## Domain Context

- **Use Case:** Live streaming telemetry for the admin dashboard, zapping-performance observability
- **Module Type:** Domain service + Infrastructure (static instruments)
- **Key Domain Entities:** ActiveStreamSession, CompletedStreamSession (read model), RelayRequestMetric, Meter/Counter/Histogram
