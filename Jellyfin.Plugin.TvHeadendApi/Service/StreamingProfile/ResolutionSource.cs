// Identifies which level in the resolution hierarchy produced the effective profile.

namespace Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;

/// <summary>
/// Identifies which level in the profile resolution hierarchy produced the effective result.
/// </summary>
public enum ResolutionSource
{
    /// <summary>
    /// An explicit per-channel override was matched.
    /// </summary>
    ChannelOverride = 0,

    /// <summary>
    /// A per-channel-group override was matched.
    /// </summary>
    ChannelGroupOverride = 1,

    /// <summary>
    /// A client or device rule was matched.
    /// </summary>
    ClientRule = 2,

    /// <summary>
    /// A user-identity rule was matched.
    /// </summary>
    UserRule = 3,

    /// <summary>
    /// The global default playback mode and profile were used.
    /// </summary>
    GlobalDefault = 4,

    /// <summary>
    /// The legacy <see cref="Configuration.PluginConfiguration.StreamingProfile"/> was used
    /// because no streaming profile settings were configured.
    /// </summary>
    LegacyFallback = 5,

    /// <summary>
    /// The hardcoded safe fallback profile was used because all other sources failed.
    /// </summary>
    SafeFallback = 6,
}
