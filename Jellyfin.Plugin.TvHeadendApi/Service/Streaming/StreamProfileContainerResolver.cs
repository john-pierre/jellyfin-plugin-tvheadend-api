using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;
using Microsoft.Extensions.Logging;
using static Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend.TvhProfileMappingHelper;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Streaming;

/// <summary>
/// Resolves and caches the effective output container from TVHeadend streaming profiles.
/// </summary>
internal sealed class StreamProfileContainerResolver : IStreamProfileContainerResolver, IDisposable
{
    private static readonly TimeSpan ProfileContainerCacheTtl = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _profileContainerLock = new(1, 1);
    private readonly ILogger<StreamProfileContainerResolver> _logger;
    private readonly ITvheadendApiClient _tvheadendApiClient;
    private readonly ITvheadendJsonReader _jsonReader;

    private ContainerCacheEntry? _profileContainerCache;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamProfileContainerResolver"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="tvheadendApiClient">TVHeadend API client.</param>
    /// <param name="jsonReader">TVHeadend JSON reader.</param>
    public StreamProfileContainerResolver(
        ILogger<StreamProfileContainerResolver> logger,
        ITvheadendApiClient tvheadendApiClient,
        ITvheadendJsonReader jsonReader)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tvheadendApiClient = tvheadendApiClient ?? throw new ArgumentNullException(nameof(tvheadendApiClient));
        _jsonReader = jsonReader ?? throw new ArgumentNullException(nameof(jsonReader));
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

            var listUrl = $"{baseUrl}{webRoot}api/profile/list";
            var listBody = await _tvheadendApiClient.GetStringAsync(httpClient, listUrl, cancellationToken).ConfigureAwait(false);

            string? profileUuid = null;
            using (var listDoc = JsonDocument.Parse(listBody))
            {
                if (listDoc.RootElement.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in entries.EnumerateArray())
                    {
                        var val = _jsonReader.GetStringProp(entry, "val");
                        if (string.Equals(val, profileName, StringComparison.OrdinalIgnoreCase))
                        {
                            profileUuid = _jsonReader.GetStringProp(entry, "key");
                            break;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(profileUuid))
            {
                _logger.LogWarning(
                    "Streaming profile '{ProfileName}' not found in TVHeadend. Defaulting container to '{Container}'.",
                    profileName,
                    fallbackContainer);
                return fallbackContainer;
            }

            var loadUrl = $"{baseUrl}{webRoot}api/idnode/load?uuid={Uri.EscapeDataString(profileUuid)}";
            var loadBody = await _tvheadendApiClient.GetStringAsync(httpClient, loadUrl, cancellationToken).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(loadBody);
            if (!doc.RootElement.TryGetProperty("entries", out var profileEntries) || profileEntries.GetArrayLength() == 0)
            {
                _logger.LogWarning(
                    "TVHeadend returned empty data for profile UUID '{Uuid}'. Defaulting container to '{Container}'.",
                    profileUuid,
                    fallbackContainer);
                return fallbackContainer;
            }

            var profileEntry = profileEntries[0];
            var profileClass = _jsonReader.GetStringPropOrParam(profileEntry, "class") ?? string.Empty;

            if (profileClass.Contains("transcode", StringComparison.OrdinalIgnoreCase))
            {
                var rawContainer = _jsonReader.GetStringPropOrParam(profileEntry, "container")
                    ?? _jsonReader.GetIntPropOrParam(profileEntry, "container")?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    ?? string.Empty;

                var mappedContainer = MapContainer(rawContainer);
                if (string.IsNullOrWhiteSpace(mappedContainer))
                {
                    _logger.LogWarning(
                        "Transcode profile '{ProfileName}' has no container set (raw='{Raw}'). Defaulting to '{Container}'.",
                        profileName,
                        rawContainer,
                        fallbackContainer);
                    return fallbackContainer;
                }

                _logger.LogInformation(
                    "Streaming profile '{ProfileName}' (class={ProfileClass}) uses container '{Container}'.",
                    profileName,
                    profileClass,
                    mappedContainer);
                return mappedContainer;
            }

            var classContainer = MapProfileClassToContainer(profileClass);
            _logger.LogInformation(
                "Streaming profile '{ProfileName}' (class={ProfileClass}) -> container '{Container}' (derived from class).",
                profileName,
                profileClass,
                classContainer);
            return classContainer;
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
