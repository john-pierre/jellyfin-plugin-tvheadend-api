# Dashboard Module

The Dashboard module aggregates data from multiple TVHeadend monitoring services into a single `DashboardStatus` snapshot for the Jellyfin admin UI. Each section — diagnostics, activity, inputs, subscriptions, connections — is fetched independently with per-section error isolation so that a failure in one area never prevents the remaining sections from loading. The service composes already-owned data from downstream domain services and does not perform direct TVHeadend HTTP calls.

## Detailed Description

`DashboardService.GetDashboardStatusAsync` builds a `DashboardStatus` by sequentially populating five sections:

| Section | Source Service | Data Populated | Error Field |
|---------|---------------|----------------|-------------|
| Diagnostics | **DiagnosticService** | `IsReachable`, `IsAuthenticated`, `ServerVersion`, `LatencyMs`, `ChannelCount`, `DvrEntryCount`, `CompatibilityScore`, `DiagnosticStatus`, `BaseUrl`, `Warnings` | `ConnectionError` |
| Activity | **StatusService** | `Activity` (subscription + connection counts) | _(silently skipped)_ |
| Inputs | **InputMonitorService** | `Inputs` (tuner signal quality) | `InputsError` |
| Subscriptions | **SubscriptionService** | `Subscriptions` (active streaming subscriptions) | `SubscriptionsError` |
| Connections | **StatusService** | `Connections` (active client connections) | `ConnectionsError` |

**Error handling strategy:** Each section is wrapped in its own try/catch. On failure the section's dedicated error field on `DashboardStatus` is set to the exception message and logged at Warning level; all other sections continue unaffected.

**Activity fallback:** If the Activity section fails or returns null, the service constructs a synthetic `ActivityStatus` from the already-collected Subscriptions and Connections counts, ensuring the dashboard always reports activity metrics.

**Base URL resolution:** The diagnostics step resolves `BaseUrl` via **ApiClient** configuration and **UrlBuilder**. If no plugin configuration is available, it falls back to the connection string from the diagnostic result.

Every snapshot is stamped with the current plugin version (`PluginVersion`) and a UTC timestamp (`Timestamp`).

## Domain Context

- **Use Case:** Admin dashboard — real-time TVHeadend server health and activity overview
- **Module Type:** Service
- **Key Domain Entities:** DashboardStatus, ActivityStatus, DiagnoseResult, InputStatus, Subscription, Connection

## Internal Dependencies

- **`DiagnosticService`** — connection health, server version, compatibility score, warnings
- **`StatusService`** — activity status (subscription/connection counts) and connection list
- **`InputMonitorService`** — tuner/input signal quality monitoring
- **`SubscriptionService`** — active streaming subscriptions
- **`UrlBuilder`** — constructs the TVHeadend base URL for display
- **`ApiClient`** — provides current plugin configuration for URL resolution
