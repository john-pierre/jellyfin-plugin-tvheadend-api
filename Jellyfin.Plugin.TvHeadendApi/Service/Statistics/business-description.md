# Statistics Service

Tracks live TV viewing sessions by listening to Jellyfin playback events and persists session history to JSON for admin reporting.

## Detailed Description

The Statistics Service subscribes to `ISessionManager` playback start/stop events, records viewing session metadata (user, channel, duration, client), and maintains a persistent JSON store with configurable retention. It provides aggregated statistics for the admin UI dashboard.

## Domain Context

- **Use Case:** Viewing statistics and usage reporting in the plugin admin panel
- **Module Type:** Service
- **Key Domain Entities:** StatisticsService

## Internal Dependencies

- **`Configuration`** — Reads retention settings from PluginConfiguration

