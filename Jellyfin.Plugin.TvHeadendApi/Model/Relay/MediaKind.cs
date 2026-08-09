// Media kind classification for relay metrics.

namespace Jellyfin.Plugin.TvHeadendApi.Model.Relay;

/// <summary>
/// Classifies the kind of media being relayed.
/// </summary>
public enum MediaKind
{
    /// <summary>Channel logo image.</summary>
    Logo,

    /// <summary>EPG program image.</summary>
    ProgramImage,

    /// <summary>Live TV stream.</summary>
    LiveTvStream,

    /// <summary>Recording thumbnail.</summary>
    RecordingImage,

    /// <summary>Unknown or unclassified media.</summary>
    Unknown,
}
