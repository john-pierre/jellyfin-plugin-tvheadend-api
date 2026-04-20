# Dashboard Module

## Purpose

Aggregates data from multiple TVHeadend services into a single dashboard snapshot for the admin UI. Each section (diagnostics, activity, inputs, subscriptions, connections) is fetched independently so a failure in one does not break the others, and the service only composes already-owned data instead of reimplementing lower-level HTTP calls.

## Key Types

| Type | Role |
|------|------|
| `IDashboardService` | Interface for dashboard aggregation |
| `DashboardService` | Orchestrates calls to `IDiagnosticService`, `IStatusService`, `IInputMonitorService`, and `ISubscriptionService` |

## Data Flow

1. `DashboardController.GetDashboard` → `DashboardService.GetDashboardStatusAsync`
2. DashboardService calls each sub-service in sequence, catching errors per section
3. Returns `DashboardStatus` with all available data and any per-section error messages

## Dependencies

- `IDiagnosticService` — connection health, server version, compatibility score
- `IStatusService` — activity status, connection list
- `IInputMonitorService` — tuner/input status
- `ISubscriptionService` — active subscriptions

