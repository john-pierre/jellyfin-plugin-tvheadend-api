// Circuit breaker observability counters exposed through the Health endpoint.

using System;
using System.Collections.Generic;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Health;

/// <summary>
/// Circuit breaker observability counters, accumulated in memory since plugin start.
/// Answers "does the breaker trip too eagerly?" with data: how often it opened, why, for how
/// long, how many requests it rejected, and how its half-open trials resolved. All fields are
/// additive to the Health endpoint payload (backward compatible).
/// </summary>
public sealed class CircuitBreakerMetrics
{
    /// <summary>Gets the effective consecutive-failure threshold that opens the circuit.</summary>
    public int Threshold { get; init; }

    /// <summary>Gets the effective open (cool-down) duration in seconds.</summary>
    public int OpenDurationSeconds { get; init; }

    /// <summary>Gets how many times the circuit transitioned Closed → Open.</summary>
    public long TimesOpened { get; init; }

    /// <summary>Gets when the circuit last opened, or <c>null</c> if it never opened.</summary>
    public DateTimeOffset? LastOpenedAtUtc { get; init; }

    /// <summary>Gets the failure reason that tripped the most recent open.</summary>
    public FailureReason LastOpenReason { get; init; } = FailureReason.None;

    /// <summary>Gets the duration of the most recently RECOVERED open episode in milliseconds.</summary>
    public long LastOpenDurationMs { get; init; }

    /// <summary>Gets the cumulative open time in milliseconds (includes the current episode while open).</summary>
    public long TotalOpenDurationMs { get; init; }

    /// <summary>Gets how many requests were rejected (fast-failed) while the circuit was open.</summary>
    public long RejectedWhileOpen { get; init; }

    /// <summary>Gets how many half-open trial requests were admitted.</summary>
    public long HalfOpenTrials { get; init; }

    /// <summary>Gets how many half-open trials succeeded (closed the circuit).</summary>
    public long HalfOpenTrialSuccesses { get; init; }

    /// <summary>Gets how many half-open trials failed (re-opened the circuit).</summary>
    public long HalfOpenTrialFailures { get; init; }

    /// <summary>
    /// Gets the number of BREAKER-RELEVANT failures (final, logical request failures). Only
    /// these advance the consecutive-failure counter toward the threshold.
    /// </summary>
    public long CircuitFailures { get; init; }

    /// <summary>
    /// Gets the number of ALL reported failures including informational ones that deliberately
    /// do NOT count toward the breaker (mid-retry attempts, per-channel stream errors where the
    /// server itself responded).
    /// </summary>
    public long ReportedFailures { get; init; }

    /// <summary>Gets per-reason failure counts (all reported failures, keyed by <see cref="FailureReason"/> name).</summary>
    public IReadOnlyDictionary<string, long> FailureCountsByReason { get; init; }
        = new Dictionary<string, long>();
}
