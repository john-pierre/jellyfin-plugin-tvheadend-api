// Defines the playback mode that determines how Live TV streams are delivered to clients.

namespace Jellyfin.Plugin.TvHeadendApi.Configuration;

/// <summary>
/// Determines how a Live TV stream is delivered from TVHeadend to the Jellyfin client.
/// </summary>
public enum PlaybackMode
{
    /// <summary>
    /// The plugin selects the best profile automatically based on configured rules,
    /// client capabilities, and fallback logic.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Prefer a raw or non-transcoding TVHeadend profile (e.g. "pass").
    /// The stream is delivered as-is from TVHeadend without server-side re-encoding.
    /// Best for clients that support the source codec natively.
    /// </summary>
    PassThrough = 1,

    /// <summary>
    /// Force or prefer a TVHeadend transcoding profile.
    /// TVHeadend re-encodes the stream before delivering it.
    /// Use when clients cannot handle the source format or when bandwidth is limited.
    /// </summary>
    TvHeadendTranscode = 2,

    /// <summary>
    /// Deliver the stream in a format suitable for Jellyfin-side transcoding.
    /// The TVHeadend profile should provide a probing-friendly container (e.g. "matroska")
    /// so Jellyfin's FFmpeg can re-encode as needed for the target client.
    /// </summary>
    JellyfinTranscode = 3,
}
