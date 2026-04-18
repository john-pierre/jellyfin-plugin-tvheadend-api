# Helper Services

Provides the generic HTTP infrastructure layer for all TVHeadend API communication — no domain logic, only transport concerns.

## Detailed Description

The Helper module contains cross-cutting infrastructure: `ApiClient` manages typed HTTP calls with auth header injection; `UrlBuilder` constructs URLs with configurable auth modes (header, URL path, query parameter) and provides sensitive data masking; `GridFetcher` implements TVHeadend's paginated grid pattern (probe + parallel fetch); `IdNodeValueHelper` extracts typed values from TVHeadend's generic idnode parameter responses; `ResilienceHandler` provides retry with exponential back-off and circuit breaker as a DelegatingHandler; `PluginMetrics` exposes System.Diagnostics.Metrics instruments for observability.

## Domain Context

- **Use Case:** Reliable, observable HTTP communication with TVHeadend
- **Module Type:** Helper (infrastructure)
- **Key Domain Entities:** ApiClient, UrlBuilder, GridFetcher, IdNode, ResilienceHandler, PluginMetrics

## Internal Dependencies

- **`Configuration`** — Reads connection settings (URL, credentials, auth mode) from PluginConfiguration

