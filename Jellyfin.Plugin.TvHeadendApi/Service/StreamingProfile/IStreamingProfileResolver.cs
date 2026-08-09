// Resolves the effective TVHeadend streaming profile for a given playback context.

namespace Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;

/// <summary>
/// Resolves the effective TVHeadend streaming profile and playback mode
/// for a given playback context using the configured resolution hierarchy.
/// </summary>
public interface IStreamingProfileResolver
{
    /// <summary>
    /// Resolves the effective streaming profile for the given context.
    /// <para>
    /// Resolution order (first match wins):
    /// 1. Per-channel override
    /// 2. Per-channel-group override
    /// 3. Client/device rule (sorted by priority)
    /// 4. User rule (sorted by priority)
    /// 5. Global default mode/profile
    /// 6. Legacy <see cref="Jellyfin.Plugin.TvHeadendApi.Configuration.PluginConfiguration.StreamingProfile"/> fallback
    /// 7. Safe hardcoded fallback ("pass").
    /// </para>
    /// </summary>
    /// <param name="context">The playback request context.</param>
    /// <returns>The resolution result with the effective profile, mode, and debug reasons.</returns>
    StreamingProfileResolutionResult Resolve(StreamingProfileContext context);
}
