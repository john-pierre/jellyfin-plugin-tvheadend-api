// Cache status classification for relay image requests.

namespace Jellyfin.Plugin.TvHeadendApi.Model.Relay;

/// <summary>
/// Describes the cache outcome for an image relay request.
/// </summary>
public enum RelayCacheStatus
{
    /// <summary>Caching is not applicable (e.g. stream relay).</summary>
    NotApplicable,

    /// <summary>Response served from cache.</summary>
    Hit,

    /// <summary>Cache miss — fetched from upstream.</summary>
    Miss,

    /// <summary>Cache revalidated with upstream (conditional request).</summary>
    Revalidated,

    /// <summary>Negative cache hit (cached upstream error/404).</summary>
    NegativeHit,
}
