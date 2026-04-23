// Per-channel streaming profile override.

namespace Jellyfin.Plugin.TvHeadendApi.Configuration;

/// <summary>
/// Overrides the streaming profile for a specific channel by its TVHeadend channel ID.
/// </summary>
public class ChannelProfileOverride
{
    /// <summary>
    /// Gets or sets the TVHeadend channel UUID this override applies to.
    /// </summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the playback mode to use for this channel.
    /// </summary>
    public PlaybackMode PlaybackMode { get; set; } = PlaybackMode.Auto;

    /// <summary>
    /// Gets or sets the TVHeadend profile name to use for this channel.
    /// </summary>
    public string TvHeadendProfileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional description explaining why this override exists.
    /// </summary>
    public string Description { get; set; } = string.Empty;
}
