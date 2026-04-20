# Comet Service

Maintains the real-time TVHeadend Comet connection for dashboard log and disk-space updates.

## Detailed Description

The Comet Service opens a WebSocket connection to TVHeadend's `/comet/ws` endpoint and buffers incoming log messages and disk-space notifications for the plugin admin UI. It reuses the plugin's shared URL building and authentication flow so the WebSocket handshake follows the configured web root, SSL mode, and auth token or HTTP authentication settings. Controllers read those buffered snapshots through `ICometSnapshotReader`, which keeps endpoint code decoupled from the hosted-service lifecycle concerns.

## Domain Context

- **Use Case:** Real-time operational visibility in the plugin admin panel
- **Module Type:** Service
- **Key Domain Entities:** Comet WebSocket, ICometSnapshotReader, LogMessage, DiskSpaceUpdate

## Internal Dependencies

- **`Helper`** — `UrlBuilder` builds the Comet endpoint URL and masks sensitive values for logging
- **`Helper`** — `ApiClient` provides the authenticated HTTP stack for the WebSocket handshake
- **`Configuration`** — Reads connection, SSL, web root, and authentication settings from `PluginConfiguration`

