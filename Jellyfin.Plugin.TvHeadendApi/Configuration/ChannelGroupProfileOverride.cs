// Per-channel-group streaming profile override.

namespace Jellyfin.Plugin.TvHeadendApi.Configuration;

/// <summary>
/// Overrides the streaming profile for all channels in a specific TVHeadend channel group (tag).
/// </summary>
public class ChannelGroupProfileOverride
{
    /// <summary>
    /// Gets or sets the channel group name (TVHeadend tag) this override applies to.
    /// Matched case-insensitively.
    /// </summary>
    public string ChannelGroup { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the playback mode to use for this channel group.
    /// </summary>
    public PlaybackMode PlaybackMode { get; set; } = PlaybackMode.Auto;

    /// <summary>
    /// Gets or sets the TVHeadend profile name to use for this channel group.
    /// </summary>
    public string TvHeadendProfileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional description explaining why this override exists.
    /// </summary>
    public string Description { get; set; } = string.Empty;
}
