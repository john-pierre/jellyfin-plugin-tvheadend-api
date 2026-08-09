// Image source type classification for relay image metrics.

namespace Jellyfin.Plugin.TvHeadendApi.Model.Relay;

/// <summary>
/// Classifies the source type of a relayed image.
/// </summary>
public enum ImageSourceType
{
    /// <summary>Channel logo.</summary>
    ChannelLogo,

    /// <summary>EPG program image.</summary>
    EpgImage,

    /// <summary>Recording thumbnail.</summary>
    RecordingThumbnail,

    /// <summary>Unknown source.</summary>
    Unknown,
}
