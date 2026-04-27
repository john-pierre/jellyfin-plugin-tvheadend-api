# Resilience Service

Retry, circuit breaker, failure classification, and timeout policies for TVHeadend HTTP communication.

## Detailed Description

`ResiliencePolicies` defines the retry and circuit breaker parameters: 3 retries with exponential back-off (base 0.5s), circuit opens after 5 consecutive failures for 30 seconds, retries on 5xx, 408, and 429 responses.

`ResilienceHandler` is a `DelegatingHandler` that implements the retry + circuit breaker logic directly (no external dependencies like Polly, to avoid assembly-loading issues in Jellyfin's plugin host). On each attempt it clones the request via `HttpRequestCloner` (since the original content stream may be consumed), retries on transient failures with exponential delay, and tracks consecutive failures. When the threshold is reached, the circuit opens and subsequent requests throw immediately until the break duration expires, at which point one half-open trial is allowed. Optionally reports success/failure to `IHealthService` for centralized health tracking.

`FailureClassifier` maps exceptions and HTTP status codes to `FailureReason` values. Inspects `HttpRequestException` inner exceptions for socket-level detail: `ConnectionRefused`, `DnsFailure`, `Timeout`. Falls back to message-based heuristics for cross-platform DNS errors. Status codes map 401/403 → `AuthFailed`, 408/504 → `Timeout`, 5xx → `Upstream5xx`, other 4xx → `Upstream4xx`.

`FailureReason` is an enum covering: `None`, `Timeout`, `AuthFailed`, `DnsFailure`, `ConnectionRefused`, `UpstreamUnavailable`, `Upstream4xx`, `Upstream5xx`, `InvalidResponse`, `CircuitOpen`, `Cancelled`, `UnexpectedException`, `Unknown`.

`OperationTimeouts` defines per-operation-type timeout defaults (Health 3s, Image 5s, Metadata 10s, StreamStartup 8s, BackgroundRefresh 15s) with user-configurable overrides from `PluginConfiguration`.

## Domain Context

- **Use Case:** Robust TVHeadend HTTP communication that survives transient failures without external resilience libraries
- **Module Type:** Infrastructure
- **Key Domain Entities:** ResilienceHandler, FailureReason, FailureClassifier, OperationType, OperationTimeouts

## Internal Dependencies

- **`Backend`** — `HttpRequestCloner` for request cloning on retry
- **`Health`** — `IHealthService` for optional centralized health reporting from the handler
