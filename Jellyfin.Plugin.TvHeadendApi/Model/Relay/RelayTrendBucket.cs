using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Relay;

/// <summary>
/// A single hourly trend bucket for charts.
/// </summary>
public sealed class RelayTrendBucket
{
    /// <summary>Gets or sets the bucket hour (UTC).</summary>
    public DateTime HourUtc { get; set; }

    /// <summary>Gets or sets total requests in this hour.</summary>
    public long Requests { get; set; }

    /// <summary>Gets or sets the number of stream relay requests in this bucket.</summary>
    public long StreamRequests { get; set; }

    /// <summary>Gets or sets the peak concurrent stream count observed in this bucket.</summary>
    public int PeakConcurrentStreams { get; set; }

    /// <summary>Gets or sets failed requests in this hour.</summary>
    public long Failures { get; set; }

    /// <summary>Gets or sets average startup latency in ms.</summary>
    public double? AvgStartupLatencyMs { get; set; }

    /// <summary>Gets or sets total bytes transferred.</summary>
    public long BytesTransferred { get; set; }

    /// <summary>Gets or sets cache hit ratio (0–100).</summary>
    public double? CacheHitRatio { get; set; }
}
