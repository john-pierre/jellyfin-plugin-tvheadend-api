using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Relay;

/// <summary>
/// A recent relay error for the dashboard error table.
/// </summary>
public sealed class RelayErrorEntry
{
    /// <summary>Gets or sets the UTC timestamp.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Gets or sets the relay type.</summary>
    public string RelayType { get; set; } = string.Empty;

    /// <summary>Gets or sets the failure reason.</summary>
    public string FailureReason { get; set; } = string.Empty;

    /// <summary>Gets or sets the upstream status code.</summary>
    public int? UpstreamStatusCode { get; set; }

    /// <summary>Gets or sets the total duration in ms.</summary>
    public double TotalDurationMs { get; set; }

    /// <summary>Gets or sets the channel ID.</summary>
    public string? ChannelId { get; set; }
}
