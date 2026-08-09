// Dashboard service for streaming telemetry — composes live and historical metrics.

using System;
using Jellyfin.Plugin.TvHeadendApi.Model.Metrics;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Metrics;

/// <summary>
/// Composes real-time active session data with historical aggregations
/// for the streaming telemetry dashboard APIs.
/// </summary>
internal sealed class StreamingDashboardService : IStreamingDashboardService
{
    private readonly ActiveSessionStore _activeStore;
    private readonly MetricsAggregator _aggregator;
    private readonly ILogger<StreamingDashboardService> _logger;

    public StreamingDashboardService(
        ActiveSessionStore activeStore,
        MetricsAggregator aggregator,
        ILogger<StreamingDashboardService> logger)
    {
        _activeStore = activeStore ?? throw new ArgumentNullException(nameof(activeStore));
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Returns real-time live metrics including all active sessions.
    /// </summary>
    /// <returns>A <see cref="LiveMetricsResponse"/> with current state.</returns>
    public LiveMetricsResponse GetLiveMetrics()
    {
        var (startupFailures, upstreamFailures, downstreamFailures) = _aggregator.GetTodayFailureCounts();

        return new LiveMetricsResponse
        {
            ActiveStreamCount = _activeStore.Count,
            CurrentTotalBandwidth = _activeStore.GetTotalBandwidth(),
            ActiveClientsByType = _activeStore.GetClientDistribution(),
            ActiveChannels = _activeStore.GetActiveChannels(),
            SessionsStarting = _activeStore.CountByState(StreamLifecycleState.Starting),
            SessionsActive = _activeStore.CountByState(StreamLifecycleState.Active),
            StartupFailuresToday = startupFailures,
            UpstreamFailuresToday = upstreamFailures,
            DownstreamFailuresToday = downstreamFailures,
            ActiveSessions = _activeStore.GetAll(),
        };
    }

    /// <summary>
    /// Returns historical metrics aggregation for today.
    /// </summary>
    /// <returns>A <see cref="HistoryMetricsResponse"/> with aggregated data.</returns>
    public HistoryMetricsResponse GetHistoryMetrics()
    {
        return _aggregator.GetHistoryMetrics();
    }

    /// <summary>
    /// Returns detailed session information including event timeline.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>Session detail or null if not found.</returns>
    public SessionMetricsResponse? GetSessionDetail(string sessionId)
    {
        return _aggregator.GetSessionDetail(sessionId);
    }
}
