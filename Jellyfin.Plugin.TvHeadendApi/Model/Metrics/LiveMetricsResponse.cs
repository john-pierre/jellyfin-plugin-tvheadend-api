// Dashboard API response for live/real-time metrics.

using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Metrics;

/// <summary>
/// Real-time metrics snapshot returned by GET /metrics/live.
/// Contains only current state — no historical aggregation.
/// </summary>
public sealed class LiveMetricsResponse
{
    /// <summary>Gets or sets the number of currently active streams.</summary>
    public int ActiveStreamCount { get; set; }

    /// <summary>Gets or sets the current total bandwidth in bits/second across all streams.</summary>
    public double CurrentTotalBandwidth { get; set; }

    /// <summary>Gets or sets active clients grouped by client type/name.</summary>
    public Dictionary<string, int> ActiveClientsByType { get; set; } = new();

    /// <summary>Gets or sets list of channels currently being streamed.</summary>
    public List<string> ActiveChannels { get; set; } = new();

    /// <summary>Gets or sets the number of sessions in Starting state.</summary>
    public int SessionsStarting { get; set; }

    /// <summary>Gets or sets the number of sessions in Active state.</summary>
    public int SessionsActive { get; set; }

    /// <summary>Gets or sets startup failures count today.</summary>
    public int StartupFailuresToday { get; set; }

    /// <summary>Gets or sets upstream failures count today.</summary>
    public int UpstreamFailuresToday { get; set; }

    /// <summary>Gets or sets downstream failures count today.</summary>
    public int DownstreamFailuresToday { get; set; }

    /// <summary>Gets or sets the active sessions detail list.</summary>
    public List<ActiveStreamSession> ActiveSessions { get; set; } = new();
}
