using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Stream;

/// <summary>
/// Manages proactive mediainfo cache files that pre-populate codec and container
/// metadata so Jellyfin can skip the expensive FFprobe probe on live streams.
/// </summary>
public interface IMediaInfoCacheService
{
    /// <summary>
    /// Ensures the mediainfo cache file for the given channel is up-to-date with respect
    /// to the current streaming profile. Depending on configuration flags the method may
    /// create, validate, rewrite, or delete the cache file.
    /// </summary>
    /// <param name="channelId">TVHeadend channel UUID.</param>
    /// <param name="streamUrl">Fully-qualified stream URL (embedded in the cache for Path matching).</param>
    /// <param name="profileSnapshot">Resolved profile snapshot describing the expected codecs and container.</param>
    /// <param name="proactiveCacheEnabled">When <c>true</c>, missing or stale cache files are (re)written.</param>
    /// <param name="validationEnabled">When <c>true</c>, existing cache files are validated against the profile snapshot.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reconciliation outcome (hit/miss/mismatch/restored/unknown) for telemetry.</returns>
    Task<MediaInfoCacheStatus> EnsureMediaInfoCacheStateAsync(
        string channelId,
        string streamUrl,
        ProfileSnapshot profileSnapshot,
        bool proactiveCacheEnabled,
        bool validationEnabled,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns a snapshot of the in-process cache counters (hits/misses/mismatches/invalidations)
    /// accumulated since plugin start, for the dashboard.
    /// </summary>
    /// <returns>The counter snapshot.</returns>
    MediaInfoCacheCounters GetCounters();

    /// <summary>
    /// Records a cache hit caused by the media source build reuse window: the recently
    /// built artifacts (including the reconciled cache state) were reused, so the cache
    /// was effectively used without a fresh reconciliation.
    /// </summary>
    void RecordStreamBuildReuseHit();

    /// <summary>
    /// Builds the deterministic cache file name that Jellyfin uses for a given media source.
    /// </summary>
    /// <param name="providerTypeOrHash">Full type name or pre-computed hex-32 hash of the provider.</param>
    /// <param name="itemTypeName">Item type name (e.g. <c>LiveTvChannel</c>).</param>
    /// <param name="itemIdN">Internal item ID in "N" (no-dash) format.</param>
    /// <param name="sourceId">Media source ID (typically the channel UUID).</param>
    /// <returns>The cache file name including the <c>.json</c> extension.</returns>
    string BuildMediainfoCacheFileName(string providerTypeOrHash, string itemTypeName, string itemIdN, string? sourceId);

    /// <summary>
    /// Resolves the internal channel ID and builds the deterministic cache file name
    /// for a live TV media source.
    /// </summary>
    /// <param name="channelId">TVHeadend channel UUID (used to compute the internal Jellyfin item ID).</param>
    /// <param name="sourceId">Media source ID (typically the channel UUID).</param>
    /// <returns>The cache file name including the <c>.json</c> extension.</returns>
    string BuildChannelCacheFileName(string channelId, string? sourceId);

    /// <summary>
    /// Warms the mediainfo cache for all known channels by resolving their streaming
    /// profiles and writing cache files. Already-cached channels are skipped.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result summarising how many channels were warmed, skipped, or failed.</returns>
    Task<CacheWarmupResult> WarmAllChannelCachesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Warms the mediainfo cache for all known channels, reporting per-channel progress
    /// via the supplied <paramref name="progress"/> callback.
    /// </summary>
    /// <param name="progress">Receives a <see cref="CacheWarmupProgress"/> update for every channel.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result summarising how many channels were warmed, skipped, or failed.</returns>
    Task<CacheWarmupResult> WarmAllChannelCachesAsync(IProgress<CacheWarmupProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes all mediainfo cache files from the cache directory.
    /// </summary>
    /// <returns>The number of cache files deleted.</returns>
    Task<int> InvalidateAllCachesAsync();

    /// <summary>
    /// Deletes the mediainfo cache file for a single channel.
    /// </summary>
    /// <param name="channelId">TVHeadend channel UUID.</param>
    /// <returns><c>true</c> if a cache file was found and deleted; otherwise <c>false</c>.</returns>
    Task<bool> InvalidateChannelCacheAsync(string channelId);
}
