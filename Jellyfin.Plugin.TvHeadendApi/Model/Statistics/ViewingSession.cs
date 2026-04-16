using System;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Statistics;

/// <summary>
/// Represents a single TV viewing session captured from Jellyfin playback events.
/// </summary>
public sealed class ViewingSession
{
    /// <summary>
    /// Gets or sets the Jellyfin username.
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the device name (e.g. "Living Room TV").
    /// </summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the client application name (e.g. "Jellyfin Web").
    /// </summary>
    public string ClientName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the channel name.
    /// </summary>
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the channel ID.
    /// </summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the play method (DirectPlay, DirectStream, Transcode).
    /// </summary>
    public string PlayMethod { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the UTC start time.
    /// </summary>
    public DateTime StartTimeUtc { get; set; }

    /// <summary>
    /// Gets or sets the UTC end time. Null if still active.
    /// </summary>
    public DateTime? EndTimeUtc { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin play session ID for correlating start/stop events.
    /// </summary>
    public string PlaySessionId { get; set; } = string.Empty;

    /// <summary>
    /// Gets the duration in minutes, or null if the session is still active.
    /// </summary>
    public double? DurationMinutes => EndTimeUtc.HasValue
        ? (EndTimeUtc.Value - StartTimeUtc).TotalMinutes
        : null;
}
