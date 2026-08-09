// Immutable snapshot of TVHeadend upstream health for dashboard and service consumption.

using System;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Health;

/// <summary>
/// Immutable snapshot of TVHeadend health for dashboard display and service decision-making.
/// </summary>
public sealed class HealthSnapshot
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

    /// <summary>Gets the circuit breaker metrics accumulated since plugin start.</summary>
    public CircuitBreakerMetrics Breaker { get; init; } = new();
}
