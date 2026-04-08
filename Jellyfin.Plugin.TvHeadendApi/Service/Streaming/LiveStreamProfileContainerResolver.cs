using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Streaming;

/// <summary>
/// Resolves and caches the effective output container from TVHeadend streaming profiles.
/// </summary>
internal sealed class LiveStreamProfileContainerResolver : ILiveStreamProfileContainerResolver, IDisposable
{
    private static readonly TimeSpan ProfileContainerCacheTtl = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _profileContainerLock = new(1, 1);
    private readonly ILogger<LiveStreamProfileContainerResolver> _logger;
    private readonly ITvheadendApiClient _tvheadendApiClient;
    private readonly ITvheadendStreamProfileResolver _streamProfileResolver;

    private ContainerCacheEntry? _profileContainerCache;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveStreamProfileContainerResolver"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="tvheadendApiClient">TVHeadend API client.</param>
    /// <param name="streamProfileResolver">TVHeadend stream profile resolver.</param>
    public LiveStreamProfileContainerResolver(
        ILogger<LiveStreamProfileContainerResolver> logger,
        ITvheadendApiClient tvheadendApiClient,
        ITvheadendStreamProfileResolver streamProfileResolver)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tvheadendApiClient = tvheadendApiClient ?? throw new ArgumentNullException(nameof(tvheadendApiClient));
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
        var profileName = config.StreamingProfile;

        if (_profileContainerCache is { } cached
            && string.Equals(cached.ProfileName, profileName, StringComparison.OrdinalIgnoreCase)
            && DateTime.UtcNow - cached.Timestamp < ProfileContainerCacheTtl)
        {
            return cached.Container;
        }

        await _profileContainerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_profileContainerCache is { } cached2
                && string.Equals(cached2.ProfileName, profileName, StringComparison.OrdinalIgnoreCase)
                && DateTime.UtcNow - cached2.Timestamp < ProfileContainerCacheTtl)
            {
                return cached2.Container;
            }

            var container = await DetectProfileContainerAsync(config, profileName, cancellationToken).ConfigureAwait(false);
            _profileContainerCache = new ContainerCacheEntry(DateTime.UtcNow, profileName, container);
            return container;
        }
        finally
        {
            _profileContainerLock.Release();
        }
    }

    private async Task<string> DetectProfileContainerAsync(PluginConfiguration config, string profileName, CancellationToken cancellationToken)
    {
        const string fallbackContainer = "mpegts";

        if (string.IsNullOrWhiteSpace(profileName))
        {
            _logger.LogWarning("No streaming profile configured. Defaulting container to '{Container}'.", fallbackContainer);
            return fallbackContainer;
        }

        try
        {
            using var httpClient = _tvheadendApiClient.CreateHttpClient(config);
            var baseUrl = _tvheadendApiClient.GetBaseUrl(config);
            var webRoot = _tvheadendApiClient.GetWebRoot(config);

            var profiles = await _streamProfileResolver.GetProfilesAsync(httpClient, baseUrl, webRoot, cancellationToken).ConfigureAwait(false);
            var profileReference = profiles.FirstOrDefault(p => string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
            if (profileReference == null)
            {
                _logger.LogWarning(
                    "Streaming profile '{ProfileName}' not found in TVHeadend. Defaulting container to '{Container}'.",
                    profileName,
                    fallbackContainer);
                return fallbackContainer;
            }

            var profileDetails = await _streamProfileResolver
                .GetProfileDetailsByUuidAsync(httpClient, baseUrl, webRoot, profileReference.Key, profileReference.Name, cancellationToken)
                .ConfigureAwait(false);
            if (profileDetails == null)
            {
                _logger.LogWarning(
                    "TVHeadend returned empty data for profile UUID '{Uuid}'. Defaulting container to '{Container}'.",
                    profileReference.Key,
                    fallbackContainer);
                return fallbackContainer;
            }

            if (profileDetails.ProfileClass.Contains("transcode", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(profileDetails.Container))
                {
                    _logger.LogWarning(
                        "Transcode profile '{ProfileName}' has no container set (raw='{Raw}'). Defaulting to '{Container}'.",
                        profileName,
                        profileDetails.RawContainer,
                        fallbackContainer);
                    return fallbackContainer;
                }

                _logger.LogInformation(
                    "Streaming profile '{ProfileName}' (class={ProfileClass}) uses container '{Container}'.",
                    profileName,
                    profileDetails.ProfileClass,
                    profileDetails.Container);
                return profileDetails.Container;
            }

            _logger.LogInformation(
                "Streaming profile '{ProfileName}' (class={ProfileClass}) -> container '{Container}' (derived from class).",
                profileName,
                profileDetails.ProfileClass,
                profileDetails.Container);
            return profileDetails.Container;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to detect container for streaming profile '{ProfileName}'. Defaulting to '{Container}'.",
                profileName,
                fallbackContainer);
            return fallbackContainer;
        }
    }

    private sealed record ContainerCacheEntry(DateTime Timestamp, string ProfileName, string Container);
}
