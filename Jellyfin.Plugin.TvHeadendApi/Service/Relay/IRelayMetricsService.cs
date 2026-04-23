// Interface for relay metrics collection and dashboard aggregation.

using Jellyfin.Plugin.TvHeadendApi.Model.Relay;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// Collects relay request metrics and provides aggregated dashboard data.
/// </summary>
public interface IRelayMetricsService
{
    /// <summary>
    /// Gets the current number of active relay streams (live in-memory state).
    /// </summary>
    int ActiveStreams { get; }

    /// <summary>
    /// Persists a completed relay request metric asynchronously (non-blocking).
    /// Safe to call from the relay hot path.
    /// </summary>
    /// <param name="metric">The metric to persist.</param>
    void RecordMetric(RelayRequestMetric metric);

    /// <summary>
    /// Builds an aggregated summary of relay metrics for the dashboard.
    /// </summary>
    /// <param name="hours">Number of hours to look back. 0 = all time.</param>
    /// <returns>Aggregated metrics summary.</returns>
    RelayMetricsSummary GetSummary(int hours);
}
