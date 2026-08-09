using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Profile;

/// <summary>
/// Resolves and caches the effective output container from TVHeadend streaming profiles.
/// </summary>
internal sealed class ProfileContainerResolver : IProfileContainerResolver, IDisposable
{
    private readonly SemaphoreSlim _profileContainerLock = new(1, 1);
    private readonly ILogger<ProfileContainerResolver> _logger;
    private readonly IApiClient _tvheadendApiClient;
    private readonly IUrlBuilder _tvheadendUrlBuilder;
    private readonly IProfileResolver _streamProfileResolver;

    private readonly ConcurrentDictionary<string, ProfileCacheEntry> _profileCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="ProfileContainerResolver"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="tvheadendApiClient">TVHeadend API client.</param>
    /// <param name="tvheadendUrlBuilder">TVHeadend URL builder.</param>
    /// <param name="streamProfileResolver">TVHeadend stream profile resolver.</param>
    public ProfileContainerResolver(
        ILogger<ProfileContainerResolver> logger,
        IApiClient tvheadendApiClient,
        IUrlBuilder tvheadendUrlBuilder,
        IProfileResolver streamProfileResolver)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tvheadendApiClient = tvheadendApiClient ?? throw new ArgumentNullException(nameof(tvheadendApiClient));
        _tvheadendUrlBuilder = tvheadendUrlBuilder ?? throw new ArgumentNullException(nameof(tvheadendUrlBuilder));
        _streamProfileResolver = streamProfileResolver ?? throw new ArgumentNullException(nameof(streamProfileResolver));
    }

    /// <summary>
    /// Gets or sets how long a guessed fallback snapshot stays cached. Kept short on purpose:
    /// the resilience circuit breaker opens for 30 seconds after 5 failures, so a brief
    /// TVHeadend outage would otherwise pin the "mpegts" fallback container for the full profile
    /// cache TTL (5 minutes by default) and hand clients a wrong container long after the
    /// backend recovered. Internal for tests.
    /// </summary>
    internal TimeSpan FallbackCacheTtl { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Releases resources used by this resolver.
    /// </summary>
    public void Dispose()
    {
        _profileContainerLock.Dispose();
    }

    /// <inheritdoc />
    public Task<string> ResolveContainerAsync(PluginConfiguration config, CancellationToken cancellationToken)
        => ResolveContainerAsync(config, null, cancellationToken);

    /// <inheritdoc />
    public async Task<string> ResolveContainerAsync(PluginConfiguration config, string? effectiveProfileName, CancellationToken cancellationToken)
    {
        var snapshot = await ResolveProfileSnapshotAsync(config, effectiveProfileName, cancellationToken).ConfigureAwait(false);
        return snapshot.Container;
    }

    /// <inheritdoc />
    public Task<ProfileSnapshot> ResolveProfileSnapshotAsync(PluginConfiguration config, CancellationToken cancellationToken)
        => ResolveProfileSnapshotAsync(config, null, cancellationToken);

    /// <inheritdoc />
    public async Task<ProfileSnapshot> ResolveProfileSnapshotAsync(PluginConfiguration config, string? effectiveProfileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Resolve the requested profile (per-channel/client rule result) or fall back to the global one.
        string profileName = string.IsNullOrWhiteSpace(effectiveProfileName) ? config.StreamingProfile : effectiveProfileName!;
        var cacheTtl = TimeSpan.FromMinutes(config.ProfileCacheTtlMinutes > 0 ? config.ProfileCacheTtlMinutes : 5);

        if (_profileCache.TryGetValue(profileName, out var cached)
            && DateTime.UtcNow - cached.Timestamp < EffectiveTtl(cached, cacheTtl))
        {
            return cached.Snapshot;
        }

        await _profileContainerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_profileCache.TryGetValue(profileName, out var cached2)
                && DateTime.UtcNow - cached2.Timestamp < EffectiveTtl(cached2, cacheTtl))
            {
                return cached2.Snapshot;
            }

            var (snapshot, isFallback) = await DetectProfileSnapshotAsync(config, profileName, cancellationToken).ConfigureAwait(false);
            _profileCache[profileName] = new ProfileCacheEntry(DateTime.UtcNow, profileName, snapshot, isFallback);
            return snapshot;
        }
        finally
        {
            _profileContainerLock.Release();
        }
    }

    /// <inheritdoc />
    public void InvalidateCache()
    {
        _profileCache.Clear();
    }

    /// <summary>
    /// Detects the snapshot for a profile, reporting whether the result is a real TVHeadend
    /// answer or a guessed fallback. Fallbacks must not be cached for the full success TTL:
    /// a brief outage would otherwise pin a wrong container — and therefore a wrong
    /// <c>MediaSourceInfo.Container</c> — for minutes and push clients off Direct Play.
    /// </summary>
    private async Task<(ProfileSnapshot Snapshot, bool IsFallback)> DetectProfileSnapshotAsync(PluginConfiguration config, string profileName, CancellationToken cancellationToken)
    {
        const string fallbackContainer = "mpegts";

        if (string.IsNullOrWhiteSpace(profileName))
        {
            _logger.LogWarning("No streaming profile configured. Defaulting container to '{Container}'.", fallbackContainer);
            return (BuildFallbackSnapshot(profileName, fallbackContainer), true);
        }

        try
        {
            using var httpClient = _tvheadendApiClient.CreateApiHttpClient(config);
            var baseUrl = _tvheadendUrlBuilder.GetBaseUrl(config);
            var webRoot = _tvheadendUrlBuilder.GetWebRoot(config);

            var resolved = await _streamProfileResolver
                .ResolveProfileByNameAsync(httpClient, baseUrl, webRoot, profileName, cancellationToken)
                .ConfigureAwait(false);
            if (resolved == null)
            {
                _logger.LogWarning(
                    "Streaming profile '{ProfileName}' not found in TVHeadend. Defaulting container to '{Container}'.",
                    profileName,
                    fallbackContainer);
                return (BuildFallbackSnapshot(profileName, fallbackContainer), true);
            }

            if (resolved.ProfileClass.Contains("transcode", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(resolved.Container))
                {
                    _logger.LogWarning(
                        "Transcode profile '{ProfileName}' has no container set (raw='{Raw}'). Defaulting to '{Container}'.",
                        profileName,
                        resolved.RawContainer,
                        fallbackContainer);
                    return (BuildSnapshot(resolved, fallbackContainer), false);
                }

                _logger.LogInformation(
                    "Streaming profile '{ProfileName}' (class={ProfileClass}) uses container '{Container}'.",
                    profileName,
                    resolved.ProfileClass,
                    resolved.Container);
                return (BuildSnapshot(resolved, resolved.Container), false);
            }

            _logger.LogInformation(
                "Streaming profile '{ProfileName}' (class={ProfileClass}) -> container '{Container}' (derived from class).",
                profileName,
                resolved.ProfileClass,
                resolved.Container);
            return (BuildSnapshot(resolved, resolved.Container), false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to detect container for streaming profile '{ProfileName}'. Defaulting to '{Container}'.",
                profileName,
                fallbackContainer);
            return (BuildFallbackSnapshot(profileName, fallbackContainer), true);
        }
    }

    private static ProfileSnapshot BuildSnapshot(ResolvedProfile resolved, string container)
    {
        return new ProfileSnapshot(
            resolved.Name,
            resolved.Key,
            resolved.ProfileClass,
            string.IsNullOrWhiteSpace(container) ? "mpegts" : container,
            resolved.ProVideoCodec,
            resolved.ProAudioCodec,
            resolved.ResolvedVideoCodec,
            resolved.ResolvedAudioCodec,
            resolved.ProfileDeinterlace ?? resolved.VideoCodecDeinterlace);
    }

    /// <summary>
    /// Returns how long a cache entry stays valid. Guessed fallbacks expire quickly so the next
    /// successful lookup replaces them, instead of a 30-second backend blip pinning a wrong
    /// container for the whole profile cache TTL.
    /// </summary>
    private TimeSpan EffectiveTtl(ProfileCacheEntry entry, TimeSpan successTtl)
    {
        return entry.IsFallback ? FallbackCacheTtl : successTtl;
    }

    private static ProfileSnapshot BuildFallbackSnapshot(string? profileName, string fallbackContainer)
    {
        return new ProfileSnapshot(
            profileName ?? string.Empty,
            string.Empty,
            string.Empty,
            fallbackContainer,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            null);
    }

    private sealed record ProfileCacheEntry(DateTime Timestamp, string ProfileName, ProfileSnapshot Snapshot, bool IsFallback);
}
