using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Relay;

/// <summary>
/// A channel that experienced stream relay failures, for the dashboard "problem channels" view.
/// </summary>
public sealed class ProblemChannel
{
    /// <summary>Gets or sets the channel display name (falls back to the UUID if not yet known).</summary>
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>Gets or sets the TVHeadend channel UUID.</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of failed stream requests for this channel in the range.</summary>
    public long FailedRequests { get; set; }

    /// <summary>Gets or sets the total stream requests for this channel in the range.</summary>
    public long TotalRequests { get; set; }

    /// <summary>Gets or sets the most recent failure reason for this channel.</summary>
    public string LastFailureReason { get; set; } = string.Empty;
}
