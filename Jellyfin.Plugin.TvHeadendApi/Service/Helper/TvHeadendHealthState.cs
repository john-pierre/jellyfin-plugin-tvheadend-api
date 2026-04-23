// Immutable snapshot of TVHeadend upstream health for dashboard and service consumption.

using System;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Helper;

/// <summary>
/// Represents the overall health status of the TVHeadend upstream connection.
/// </summary>
public enum HealthStatus
{
    /// <summary>No health data available yet.</summary>
    Unknown,

    /// <summary>TVHeadend is reachable and responding normally.</summary>
    Healthy,

    /// <summary>TVHeadend is responding but with elevated latency or intermittent errors.</summary>
    Degraded,

    /// <summary>TVHeadend is not reachable.</summary>
    Unreachable,

    /// <summary>Authentication to TVHeadend failed.</summary>
    AuthFailed,

    /// <summary>TVHeadend requests are timing out.</summary>
    Timeout,

    /// <summary>Circuit breaker is open — upstream calls are blocked.</summary>
    CircuitOpen,

    /// <summary>Recovering from failure — half-open circuit breaker trial in progress.</summary>
    Recovering,
}

/// <summary>
/// Represents the circuit breaker state.
/// </summary>
public enum CircuitState
{
    /// <summary>Normal operation — all requests pass through.</summary>
    Closed,

    /// <summary>Failure threshold exceeded — requests are blocked.</summary>
    Open,

    /// <summary>Break duration elapsed — limited trial requests allowed.</summary>
    HalfOpen,
}

/// <summary>
/// Immutable snapshot of TVHeadend health for dashboard display and service decision-making.
/// </summary>
public sealed class TvHeadendHealthSnapshot
{
    /// <summary>Gets the overall health status.</summary>
    public HealthStatus Status { get; init; } = HealthStatus.Unknown;

    /// <summary>Gets the circuit breaker state.</summary>
    public CircuitState CircuitState { get; init; } = CircuitState.Closed;

    /// <summary>Gets a value indicating whether degraded mode is active.</summary>
    public bool IsDegradedModeActive { get; init; }

    /// <summary>Gets the timestamp of the last successful TVHeadend contact.</summary>
    public DateTimeOffset? LastSuccessUtc { get; init; }

    /// <summary>Gets the timestamp of the last failure.</summary>
    public DateTimeOffset? LastFailureUtc { get; init; }

    /// <summary>Gets the last failure reason.</summary>
    public FailureReason LastFailureReason { get; init; } = FailureReason.None;

    /// <summary>Gets the number of consecutive failures.</summary>
    public int ConsecutiveFailures { get; init; }

    /// <summary>Gets the last measured response time in milliseconds.</summary>
    public int? LastResponseTimeMs { get; init; }

    /// <summary>Gets the estimated next recovery attempt time (when circuit is open).</summary>
    public DateTimeOffset? NextRetryUtc { get; init; }

    /// <summary>Gets the time this snapshot was created.</summary>
    public DateTimeOffset SnapshotUtc { get; init; } = DateTimeOffset.UtcNow;
}
