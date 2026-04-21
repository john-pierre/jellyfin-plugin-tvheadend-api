# Comet Service

Maintains the real-time TVHeadend Comet connection for dashboard log and disk-space updates.

## Detailed Description

`CometService` is a hosted service that opens a WebSocket connection to TVHeadend's `/comet/ws` endpoint and buffers incoming log messages and disk-space notifications for the plugin admin UI. It reuses the plugin's shared URL building and authentication flow so the WebSocket handshake follows the configured web root, SSL mode, and auth token or HTTP authentication settings. Controllers read those buffered snapshots through `ICometSnapshotReader`, which keeps endpoint code decoupled from the hosted-service lifecycle concerns.

## Domain Context

- **Use Case:** Real-time operational visibility in the plugin admin panel
- **Module Type:** Service
- **Key Domain Entities:** CometService, ICometSnapshotReader, LogMessage, DiskSpaceUpdate

## Internal Dependencies

- **`Helper`** — `IUrlBuilder` builds the Comet endpoint URL and masks sensitive values for logging
- **`Helper`** — `IApiClient` provides the authenticated HTTP client for the WebSocket handshake
- **`Configuration`** — `PluginConfigurationProvider` supplies connection, SSL, web root, and authentication settings
