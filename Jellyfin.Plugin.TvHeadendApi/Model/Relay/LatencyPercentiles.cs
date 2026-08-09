using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Relay;

/// <summary>
/// Latency percentile values in milliseconds.
/// </summary>
public sealed class LatencyPercentiles
{
    /// <summary>Gets or sets the average in ms.</summary>
    public double Avg { get; set; }

    /// <summary>Gets or sets the median (p50) in ms.</summary>
    public double Median { get; set; }

    /// <summary>Gets or sets the 95th percentile in ms.</summary>
    public double P95 { get; set; }

    /// <summary>Gets or sets the 99th percentile in ms.</summary>
    public double P99 { get; set; }
}
