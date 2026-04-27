# Dashboard Skill

Embedded dashboard and configuration UI for jellyfin-plugin-tvheadend-api.

## Overview

The plugin provides two embedded HTML pages served to the Jellyfin web UI, backed by API endpoints that aggregate data from multiple subsystems.

## Embedded pages

| Page | File | Purpose |
|------|------|---------|
| Configuration | `Configuration/ConfigPage.html` | Plugin settings (TVHeadend connection, streaming profiles, relay options, log levels) |
| Dashboard | `Page/DashboardPage.html` | Live system overview (status, streams, tuners, storage, logs) |

Both pages are embedded resources served by Jellyfin's plugin page infrastructure.

## DashboardService

`DashboardService` (`Service/Dashboard/DashboardService.cs`) — implements `IDashboardService`:

- Aggregates data from 6+ subsystems into a single dashboard response:
  - Health status (`IHealthService`)
  - Active subscriptions (`Service/Subscription/`)
  - Tuner/input status (`Service/Input/`)
  - Storage information (`Service/Storage/`)
  - Relay activity (`RelayActivityTracker`)
  - TVHeadend server status (`Service/Status/`)
- Returns structured data for the dashboard page to render.

## API controllers

### PluginController (`Api/PluginController.cs`)
- Plugin configuration endpoints.
- Connection test endpoint for TVHeadend.
- General plugin status.

### DashboardController (`Api/DashboardController.cs`)
- Dashboard data aggregation endpoint consumed by `DashboardPage.html`.
- Returns combined status from all subsystems.

### DashboardLogsController (`Api/Endpoint/DashboardLogsController.cs`)
- Real-time log viewing endpoint.
- Queries `PluginLogService` for recent log entries.
- Supports filtering by level, source, and time range.

### LogsController (`Api/Endpoint/LogsController.cs`)
- Log query and management endpoints.

### StreamingProfileController (`Api/Endpoint/StreamingProfileController.cs`)
- Streaming profile management endpoints.

## CometService integration

`CometService` (`Service/Comet/CometService.cs`):

- Provides real-time updates from TVHeadend via long-poll.
- Dashboard can display live subscription changes, disk space updates, and log messages.
- `ICometSnapshotReader` supplies current state for initial page load.

## Rules

- Dashboard data must be read-only — no mutations through dashboard endpoints.
- API endpoints must use `[Authorize(Policy = "RequiresElevation")]` for admin-only access.
- Never expose raw TVHeadend credentials or tokens in dashboard responses.
- Dashboard page JavaScript must handle API errors gracefully (TVHeadend offline, plugin not configured).
- Keep API response payloads minimal — aggregate on the server, not in the browser.
- Configuration changes through `ConfigPage.html` go through `ConfigurationSaver` in `Service/Configuration/`.
