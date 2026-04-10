using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Dvr;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Dvr;

/// <summary>
/// Handles creation, update and lookup of TVHeadend DVR timers.
/// </summary>
internal sealed partial class DvrService : IDvrService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<DvrService> _logger;
    private readonly IApiClient _tvheadendApiClient;
    private readonly IUrlBuilder _tvheadendUrlBuilder;

    public DvrService(
        ILogger<DvrService> logger,
        IApiClient tvheadendApiClient,
        IUrlBuilder tvheadendUrlBuilder)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tvheadendApiClient = tvheadendApiClient ?? throw new ArgumentNullException(nameof(tvheadendApiClient));
        _tvheadendUrlBuilder = tvheadendUrlBuilder ?? throw new ArgumentNullException(nameof(tvheadendUrlBuilder));
    }

    public async Task<string> GetRecordingProfileUuidAsync(string profileName, CancellationToken cancellationToken)
    {
        // profileName may be empty string: TVHeadend's built-in default profile has an empty name ("").
        ArgumentNullException.ThrowIfNull(profileName);

        var config = GetConfig();
        var url = _tvheadendUrlBuilder.BuildUrlWithHeaderAuth(config, "api/dvr/config/grid");
        using var httpClient = _tvheadendApiClient.BuildHttpClient(config);
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var result = await JsonSerializer.DeserializeAsync<DvrConfigGridResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);

        // When profileName is empty, match the TVHeadend default profile (empty name).
        var matchingProfile = result?.Entries?.FirstOrDefault(profile =>
            string.Equals(profile.Name, profileName, StringComparison.OrdinalIgnoreCase));

        // Fall back to the first available profile when no explicit name matches.
        if (matchingProfile == null && result?.Entries?.Count > 0)
        {
            matchingProfile = result.Entries[0];
        }

        if (string.IsNullOrWhiteSpace(matchingProfile?.Uuid))
        {
            throw new InvalidOperationException($"No matching recording profile found for '{profileName}' in TVHeadend.");
        }

        return matchingProfile.Uuid;
    }

    private static bool IsTvhDvrEnabled() => Plugin.Instance?.Configuration.EnableTvhDvr ?? true;

    private void EnsureDvrEnabled()
    {
        if (!IsTvhDvrEnabled())
        {
            throw new NotSupportedException("TVHeadend DVR is disabled in the plugin configuration.");
        }
    }

    private PluginConfiguration GetConfig()
    {
        return _tvheadendApiClient.GetCurrentConfiguration()
            ?? throw new InvalidOperationException("Plugin configuration is not available.");
    }
}
