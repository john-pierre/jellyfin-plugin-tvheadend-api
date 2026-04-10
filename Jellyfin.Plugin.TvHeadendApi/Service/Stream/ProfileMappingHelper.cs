namespace Jellyfin.Plugin.TvHeadendApi.Service.Stream;

/// <summary>
/// Provides TVHeadend profile and container mapping helpers shared across services.
/// </summary>
internal static class ProfileMappingHelper
{
    /// <summary>
    /// Maps TVHeadend container values (numeric enum or string) to FFmpeg container names.
    /// </summary>
    /// <param name="raw">Raw container value from TVHeadend profile data.</param>
    /// <returns>Normalized container name used by FFmpeg/Jellyfin.</returns>
    public static string MapContainer(string raw)
    {
        return raw.ToLowerInvariant() switch
        {
            "0" or "" or "not set" => string.Empty,
            "1" or "matroska" or "mkv" => "matroska",
            "2" or "mpegts" or "ts" => "mpegts",
            "3" or "mpegps" or "ps" => "mpegps",
            "9" => "mp4",
            "4" or "mp4" => "mp4",
            _ => raw.ToLowerInvariant()
        };
    }

    /// <summary>
    /// Derives the container format from a TVHeadend profile class for non-transcode profiles.
    /// </summary>
    /// <param name="profileClass">TVHeadend profile class string.</param>
    /// <returns>Derived output container name.</returns>
    public static string MapProfileClassToContainer(string profileClass)
    {
        return profileClass.ToLowerInvariant() switch
        {
            "profile-matroska" => "matroska",
            "profile-mpegts" or "profile-mpegts-pass" or "profile-mpegts-spawn" => "mpegts",
            "profile-htsp" => "mpegts",
            _ => "mpegts"
        };
    }
}
