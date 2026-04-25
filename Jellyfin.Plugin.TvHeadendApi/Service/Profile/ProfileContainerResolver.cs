using System;
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

    private volatile ProfileCacheEntry? _profileCache;

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
    /// Releases resources used by this resolver.
    /// </summary>
    public void Dispose()
    {
        _profileContainerLock.Dispose();
    }

    /// <inheritdoc />
    public async Task<string> ResolveContainerAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        var snapshot = await ResolveProfileSnapshotAsync(config, cancellationToken).ConfigureAwait(false);
        return snapshot.Container;
    }

    /// <inheritdoc />
    public async Task<ProfileSnapshot> ResolveProfileSnapshotAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        var profileName = config.StreamingProfile;
        var cacheTtl = TimeSpan.FromMinutes(config.ProfileCacheTtlMinutes > 0 ? config.ProfileCacheTtlMinutes : 5);

        if (_profileCache is { } cached
            && string.Equals(cached.ProfileName, profileName, StringComparison.OrdinalIgnoreCase)
            && DateTime.UtcNow - cached.Timestamp < cacheTtl)
        {
            return cached.Snapshot;
        }

        await _profileContainerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_profileCache is { } cached2
                && string.Equals(cached2.ProfileName, profileName, StringComparison.OrdinalIgnoreCase)
                && DateTime.UtcNow - cached2.Timestamp < cacheTtl)
            {
                return cached2.Snapshot;
            }

            var snapshot = await DetectProfileSnapshotAsync(config, profileName, cancellationToken).ConfigureAwait(false);
            _profileCache = new ProfileCacheEntry(DateTime.UtcNow, profileName, snapshot);
            return snapshot;
        }
        finally
        {
            _profileContainerLock.Release();
        }
    }

    private async Task<ProfileSnapshot> DetectProfileSnapshotAsync(PluginConfiguration config, string profileName, CancellationToken cancellationToken)
    {
        const string fallbackContainer = "mpegts";

        if (string.IsNullOrWhiteSpace(profileName))
        {
            _logger.LogWarning("No streaming profile configured. Defaulting container to '{Container}'.", fallbackContainer);
            return BuildFallbackSnapshot(profileName, fallbackContainer);
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
                return BuildFallbackSnapshot(profileName, fallbackContainer);
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
                    return BuildSnapshot(resolved, fallbackContainer);
                }

                _logger.LogInformation(
                    "Streaming profile '{ProfileName}' (class={ProfileClass}) uses container '{Container}'.",
                    profileName,
                    resolved.ProfileClass,
                    resolved.Container);
                return BuildSnapshot(resolved, resolved.Container);
            }

            _logger.LogInformation(
                "Streaming profile '{ProfileName}' (class={ProfileClass}) -> container '{Container}' (derived from class).",
                profileName,
                resolved.ProfileClass,
                resolved.Container);
            return BuildSnapshot(resolved, resolved.Container);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to detect container for streaming profile '{ProfileName}'. Defaulting to '{Container}'.",
                profileName,
                fallbackContainer);
            return BuildFallbackSnapshot(profileName, fallbackContainer);
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

    private sealed record ProfileCacheEntry(DateTime Timestamp, string ProfileName, ProfileSnapshot Snapshot);
}
