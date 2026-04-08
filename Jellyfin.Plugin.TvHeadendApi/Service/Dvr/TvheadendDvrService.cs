using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model;
using Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Dvr;

/// <summary>
/// Handles creation, update and lookup of TVHeadend DVR timers.
/// </summary>
internal sealed partial class TvheadendDvrService : ITvheadendDvrService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<TvheadendDvrService> _logger;
    private readonly ITvheadendApiClient _tvheadendApiClient;
    private readonly ITvheadendUrlBuilder _tvheadendUrlBuilder;

    public TvheadendDvrService(
        ILogger<TvheadendDvrService> logger,
        ITvheadendApiClient tvheadendApiClient,
        ITvheadendUrlBuilder tvheadendUrlBuilder)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tvheadendApiClient = tvheadendApiClient ?? throw new ArgumentNullException(nameof(tvheadendApiClient));
        _tvheadendUrlBuilder = tvheadendUrlBuilder ?? throw new ArgumentNullException(nameof(tvheadendUrlBuilder));
    }

    public async Task<string> GetRecordingProfileUuidAsync(string profileName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);

        var config = GetConfig();
        var url = _tvheadendUrlBuilder.BuildUrl(config, "api/dvr/config/grid");
        using var httpClient = _tvheadendApiClient.CreateHttpClient(config);
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var result = await JsonSerializer.DeserializeAsync<TvhApiDvrConfigGridResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        var matchingProfile = result?.Entries?.FirstOrDefault(profile => string.Equals(profile.Name, profileName, StringComparison.OrdinalIgnoreCase));
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
