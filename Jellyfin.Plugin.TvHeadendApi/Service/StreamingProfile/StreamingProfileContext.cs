// Input context for streaming profile resolution — describes the current playback request.

namespace Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;

/// <summary>
/// Describes the playback request context used by the streaming profile resolver
/// to determine the effective TVHeadend profile and playback mode.
/// </summary>
public sealed record StreamingProfileContext
{
    /// <summary>
    /// Gets the TVHeadend channel UUID being tuned.
    /// </summary>
    public string? ChannelId { get; init; }

    /// <summary>
    /// Gets the channel group (TVHeadend tag) the channel belongs to, if known.
    /// </summary>
    public string? ChannelGroup { get; init; }

    /// <summary>
    /// Gets the Jellyfin user ID requesting playback.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Gets the Jellyfin client application name (e.g. "Jellyfin Web", "Swiftfin", "Jellyfin Android").
    /// </summary>
    public string? ClientName { get; init; }

    /// <summary>
    /// Gets the Jellyfin device name (e.g. "iPhone 15", "Living Room Shield").
    /// </summary>
    public string? DeviceName { get; init; }
}

