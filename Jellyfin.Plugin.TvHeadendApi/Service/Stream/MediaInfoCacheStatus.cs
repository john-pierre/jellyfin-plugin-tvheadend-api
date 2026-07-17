// Outcome classification for a mediainfo cache reconciliation during stream setup.

namespace Jellyfin.Plugin.TvHeadendApi.Service.Stream;

/// <summary>
/// Describes the outcome of reconciling the mediainfo cache for a channel during a
/// media source build. Persisted (lower-cased) on the relay token and the request
/// metric so startup latency can be correlated with warm/cold cache starts.
/// </summary>
public enum MediaInfoCacheStatus
{
    /// <summary>The outcome could not be determined (error during reconciliation).</summary>
    Unknown,

    /// <summary>An existing cache file matched the effective profile — warm start.</summary>
    Hit,

    /// <summary>No cache file existed — cold start (a synthetic file may have been written).</summary>
    Miss,

    /// <summary>A cache file existed but did not match the effective profile.</summary>
    Mismatch,

    /// <summary>The cache file was restored from the per-profile store.</summary>
    Restored,
}
