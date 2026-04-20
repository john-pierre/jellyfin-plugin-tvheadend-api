using System;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Comet;

/// <summary>
/// Represents a single log message received from TVHeadend.
/// </summary>
public sealed class LogMessage
{
    /// <summary>
    /// Gets or sets the UTC timestamp when the log message was buffered.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the log text.
    /// </summary>
    public string Text { get; set; } = string.Empty;
}
