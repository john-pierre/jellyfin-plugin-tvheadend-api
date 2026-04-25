// Discovers TVHeadend streaming profiles with TTL-based caching.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;

/// <summary>
/// Fetches available TVHeadend streaming profiles via the existing <see cref="IProfileResolver"/>
/// and caches them with a configurable TTL derived from <see cref="PluginConfiguration.ProfileCacheTtlMinutes"/>.
/// </summary>
internal sealed class ProfileDiscoveryService : IProfileDiscoveryService, IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<ProfileDiscoveryService> _logger;
    private readonly IProfileResolver _profileResolver;
    private readonly IApiClient _apiClient;
    private readonly IUrlBuilder _urlBuilder;
    private readonly ConfigurationProvider _configProvider;

    private volatile DiscoveryCache? _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProfileDiscoveryService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="profileResolver">TVHeadend profile resolver.</param>
    /// <param name="apiClient">TVHeadend API client.</param>
    /// <param name="urlBuilder">TVHeadend URL builder.</param>
    /// <param name="configProvider">Plugin configuration provider.</param>
    public ProfileDiscoveryService(
        ILogger<ProfileDiscoveryService> logger,
        IProfileResolver profileResolver,
        IApiClient apiClient,
        IUrlBuilder urlBuilder,
        ConfigurationProvider configProvider)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _profileResolver = profileResolver ?? throw new ArgumentNullException(nameof(profileResolver));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _urlBuilder = urlBuilder ?? throw new ArgumentNullException(nameof(urlBuilder));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredProfile>> GetAvailableProfilesAsync(CancellationToken cancellationToken)
    {
        var config = _configProvider.Configuration;
        if (config == null)
        {
            return Array.Empty<DiscoveredProfile>();
        }

        var ttl = TimeSpan.FromMinutes(config.ProfileCacheTtlMinutes > 0 ? config.ProfileCacheTtlMinutes : 5);

        if (_cache is { } cached && DateTime.UtcNow - cached.Timestamp < ttl)
        {
            return cached.Profiles;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache is { } cached2 && DateTime.UtcNow - cached2.Timestamp < ttl)
            {
                return cached2.Profiles;
            }

            var profiles = await FetchProfilesAsync(config, cancellationToken).ConfigureAwait(false);
            _cache = new DiscoveryCache(DateTime.UtcNow, profiles);
            return profiles;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ValidateConfiguredProfilesAsync(CancellationToken cancellationToken)
    {
        var config = _configProvider.Configuration;
        if (config == null)
        {
            return new[] { "Plugin configuration is not available." };
        }

        var discovered = await GetAvailableProfilesAsync(cancellationToken).ConfigureAwait(false);
        if (discovered.Count == 0)
        {
            return new[] { "No profiles discovered from TVHeadend. Cannot validate configured profile names." };
        }

        var knownNames = new HashSet<string>(
            discovered.Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase);

        var warnings = new List<string>();
        var settings = config.StreamingProfileSettings;

        ValidateProfileName(settings.DefaultTvHeadendProfile, "DefaultTvHeadendProfile", knownNames, warnings);
        ValidateProfileName(settings.PassThroughProfile, "PassThroughProfile", knownNames, warnings);
        ValidateProfileName(settings.JellyfinTranscodeProfile, "JellyfinTranscodeProfile", knownNames, warnings);
        ValidateProfileName(settings.FallbackTvHeadendProfile, "FallbackTvHeadendProfile", knownNames, warnings);
        ValidateProfileName(config.StreamingProfile, "StreamingProfile (legacy)", knownNames, warnings);

        for (var i = 0; i < settings.ChannelOverrides.Count; i++)
        {
            var o = settings.ChannelOverrides[i];
            ValidateProfileName(o.TvHeadendProfileName, $"ChannelOverrides[{i}].TvHeadendProfileName", knownNames, warnings);
        }

        for (var i = 0; i < settings.ChannelGroupOverrides.Count; i++)
        {
            var o = settings.ChannelGroupOverrides[i];
            ValidateProfileName(o.TvHeadendProfileName, $"ChannelGroupOverrides[{i}].TvHeadendProfileName", knownNames, warnings);
        }

        for (var i = 0; i < settings.ClientRules.Count; i++)
        {
            var r = settings.ClientRules[i];
            ValidateProfileName(r.TvHeadendProfileName, $"ClientRules[{i}].TvHeadendProfileName ({r.RuleName})", knownNames, warnings);
        }

        for (var i = 0; i < settings.UserRules.Count; i++)
        {
            var r = settings.UserRules[i];
            ValidateProfileName(r.TvHeadendProfileName, $"UserRules[{i}].TvHeadendProfileName ({r.RuleName})", knownNames, warnings);
        }

        return warnings;
    }

    /// <inheritdoc />
    public void InvalidateCache()
    {
        _cache = null;
    }

    /// <inheritdoc cref="IDisposable.Dispose"/>
    public void Dispose()
    {
        _lock.Dispose();
    }

    private async Task<IReadOnlyList<DiscoveredProfile>> FetchProfilesAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = _apiClient.CreateApiHttpClient(config);
            var baseUrl = _urlBuilder.GetBaseUrl(config);
            var webRoot = _urlBuilder.GetWebRoot(config);
            var profiles = await _profileResolver.GetProfilesAsync(httpClient, baseUrl, webRoot, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Discovered {Count} TVHeadend streaming profiles.", profiles.Count);
            return profiles.Select(p => new DiscoveredProfile(p.Key, p.Name)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to discover TVHeadend streaming profiles.");
            return Array.Empty<DiscoveredProfile>();
        }
    }

    private static void ValidateProfileName(string? profileName, string fieldName, HashSet<string> knownNames, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return;
        }

        if (!knownNames.Contains(profileName))
        {
            warnings.Add($"Profile '{profileName}' (configured in {fieldName}) was not found in TVHeadend.");
        }
    }

    private sealed record DiscoveryCache(DateTime Timestamp, IReadOnlyList<DiscoveredProfile> Profiles);
}
