// Interface for streaming telemetry dashboard queries.

using Jellyfin.Plugin.TvHeadendApi.Model.Metrics;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Metrics;

/// <summary>
/// Provides real-time and historical streaming telemetry for dashboard APIs.
/// </summary>
public interface IStreamingDashboardService
{
    /// <summary>Returns real-time live metrics including all active sessions.</summary>
    /// <returns>A <see cref="LiveMetricsResponse"/> with current state.</returns>
    LiveMetricsResponse GetLiveMetrics();

    /// <summary>Returns historical metrics aggregation for today.</summary>
    /// <returns>A <see cref="HistoryMetricsResponse"/> with aggregated data.</returns>
    HistoryMetricsResponse GetHistoryMetrics();

    /// <summary>Returns detailed session information including event timeline.</summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>Session detail or null if not found.</returns>
    SessionMetricsResponse? GetSessionDetail(string sessionId);
}
