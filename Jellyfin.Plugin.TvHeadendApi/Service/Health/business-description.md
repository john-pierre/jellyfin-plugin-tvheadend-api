# Health Service

Central TVHeadend upstream health monitoring with circuit breaker, degraded mode tracking, and transition history.

## Detailed Description

`HealthService` is a thread-safe singleton that maintains the real-time health state of the TVHeadend connection. All services report success/failure through `RecordSuccess` and `RecordFailure` to build a consistent picture of upstream availability.

**Circuit breaker:** After N consecutive failures (default 5, configurable), the circuit opens for a configurable duration (default 30s). During the open period, `ShouldBlockRequest()` returns `true` and callers skip upstream requests entirely. When the break duration expires, the circuit enters half-open state — one trial request is allowed. A success closes the circuit; a failure re-opens it.

**Health status mapping:** Failure reasons are mapped to specific statuses: `AuthFailed`, `Timeout`, `Unreachable` (DNS/connection), `Degraded` (other), or `CircuitOpen`. A single success resets the state to `Healthy` and clears consecutive failures.

**Active health check:** `CheckHealthAsync` performs a lightweight GET to `api/serverinfo` with operation-type-specific timeouts, recording the result to update health state.

**Transition persistence:** Health state transitions are persisted to SQLite (fire-and-forget) for trend analysis. `GetHealthHistory` reads the transition log in reverse chronological order.

`HealthSnapshot` is an immutable snapshot exposing status, circuit state, degraded mode flag, last success/failure timestamps, consecutive failure count, last response time, and next retry estimate.

`HealthStatus` and `CircuitState` are enums covering the full lifecycle: Unknown → Healthy ↔ Degraded/Unreachable/AuthFailed/Timeout/CircuitOpen → Recovering → Healthy.

## Domain Context

- **Use Case:** Centralized upstream health gate — prevents cascading failures and provides dashboard health data
- **Module Type:** Service
- **Key Domain Entities:** HealthSnapshot, HealthStatus, CircuitState, HealthTransition

## Internal Dependencies

- **`Backend`** — `IApiClient` and `IUrlBuilder` for the active health check probe
- **`Resilience`** — `FailureReason`, `FailureClassifier`, `OperationTimeouts` for failure classification and timeout policies
- **`Configuration`** — `ConfigurationProvider` for user-configurable circuit breaker thresholds
