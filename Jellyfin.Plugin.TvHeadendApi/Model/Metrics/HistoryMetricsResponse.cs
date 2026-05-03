// Dashboard API response for historical metrics aggregation.

using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Metrics;

/// <summary>
/// Historical metrics aggregation returned by GET /metrics/history.
/// Aggregates completed sessions for the current day.
/// </summary>
#pragma warning disable CA2227 // Collection properties should be read only — DTO for JSON serialization
public sealed class HistoryMetricsResponse
{
    /// <summary>Gets or sets total streams today.</summary>
    public int StreamsToday { get; set; }

    /// <summary>Gets or sets completed streams today.</summary>
    public int CompletedStreamsToday { get; set; }

    /// <summary>Gets or sets normal disconnects today.</summary>
    public int NormalDisconnectsToday { get; set; }

    /// <summary>Gets or sets average startup latency in ms.</summary>
    public double AvgStartupLatencyMs { get; set; }

    /// <summary>Gets or sets 95th percentile startup latency in ms.</summary>
    public double P95StartupLatencyMs { get; set; }

    /// <summary>Gets or sets top channels by session count.</summary>
    public Dictionary<string, int> TopChannels { get; set; } = new();

    /// <summary>Gets or sets top clients by session count.</summary>
    public Dictionary<string, int> TopClients { get; set; } = new();

    /// <summary>Gets or sets total bytes transferred today.</summary>
    public long TotalBytesToday { get; set; }

    /// <summary>Gets or sets average watch duration in ms.</summary>
    public double AverageWatchDurationMs { get; set; }

    /// <summary>Gets or sets failures grouped by reason.</summary>
    public Dictionary<string, int> FailuresByReason { get; set; } = new();

    /// <summary>Gets or sets failures grouped by upstream/downstream category.</summary>
    public Dictionary<string, int> FailuresByCategory { get; set; } = new();
}
#pragma warning restore CA2227

