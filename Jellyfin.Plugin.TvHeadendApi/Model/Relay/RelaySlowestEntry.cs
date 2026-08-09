using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Relay;

/// <summary>
/// A slow relay request for the dashboard slowest-requests table.
/// </summary>
public sealed class RelaySlowestEntry
{
    /// <summary>Gets or sets the UTC timestamp.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Gets or sets the relay type.</summary>
    public string RelayType { get; set; } = string.Empty;

    /// <summary>Gets or sets total duration in ms. For streams this is the watch time, not a slowness signal.</summary>
    public double TotalDurationMs { get; set; }

    /// <summary>
    /// Gets or sets the startup latency in ms (time until first byte reached the client).
    /// This is the meaningful "slowness" for streams; <c>null</c> when no byte was ever delivered.
    /// </summary>
    public double? StartupLatencyMs { get; set; }

    /// <summary>Gets or sets bytes sent.</summary>
    public long BytesSent { get; set; }

    /// <summary>Gets or sets the final outcome.</summary>
    public string FinalOutcome { get; set; } = string.Empty;

    /// <summary>Gets or sets the channel ID.</summary>
    public string? ChannelId { get; set; }
}
