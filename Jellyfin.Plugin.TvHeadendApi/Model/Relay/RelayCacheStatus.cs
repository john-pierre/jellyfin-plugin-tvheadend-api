// Cache status classification for relay image requests.

namespace Jellyfin.Plugin.TvHeadendApi.Model.Relay;

/// <summary>
/// Describes the cache outcome for an image relay request.
/// Only states the on-disk image cache actually produces are listed —
/// it performs no conditional revalidation and no negative caching.
/// </summary>
public enum RelayCacheStatus
{
    /// <summary>Caching is not applicable (e.g. stream relay).</summary>
    NotApplicable,

    /// <summary>Response served from cache.</summary>
    Hit,

    /// <summary>Cache miss — fetched from upstream.</summary>
    Miss,
}
