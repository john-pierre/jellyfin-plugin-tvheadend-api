using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Status;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Status;

/// <summary>
/// Retrieves TVHeadend server status and connection information via the HTTP API.
/// </summary>
internal sealed class StatusService : IStatusService
{
    private readonly ILogger<StatusService> _logger;
    private readonly IApiClient _apiClient;
    private readonly IUrlBuilder _urlBuilder;

    /// <summary>
    /// Initializes a new instance of the <see cref="StatusService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="apiClient">TVHeadend API client.</param>
    /// <param name="urlBuilder">TVHeadend URL builder.</param>
    public StatusService(ILogger<StatusService> logger, IApiClient apiClient, IUrlBuilder urlBuilder)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _urlBuilder = urlBuilder ?? throw new ArgumentNullException(nameof(urlBuilder));
    }

    /// <summary>
    /// Gets the current server activity status including connection and subscription counts.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The activity status summary, or <c>null</c> if the configuration is unavailable.</returns>
    public async Task<ActivityStatus?> GetActivityStatusAsync(CancellationToken cancellationToken)
    {
        var config = _apiClient.GetCurrentConfiguration();
        if (config == null)
        {
            _logger.LogWarning("Plugin configuration is not available.");
            return null;
        }

        using var httpClient = _apiClient.CreateApiHttpClient(config);
        var url = _urlBuilder.BuildApiUrl(config, "api/status/activity");
        var json = await _apiClient.GetStringAsync(httpClient, url, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ActivityStatus>(json, JsonDefaults.Api);
    }

    /// <summary>
    /// Gets the list of active client connections from TVHeadend.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Active connections.</returns>
    public async Task<IReadOnlyList<ConnectionEntry>> GetConnectionsAsync(CancellationToken cancellationToken)
    {
        var config = _apiClient.GetCurrentConfiguration();
        if (config == null)
        {
            _logger.LogWarning("Plugin configuration is not available.");
            return [];
        }

        using var httpClient = _apiClient.CreateApiHttpClient(config);
        var url = _urlBuilder.BuildApiUrl(config, "api/status/connections");
        var json = await _apiClient.GetStringAsync(httpClient, url, cancellationToken).ConfigureAwait(false);
        var grid = JsonSerializer.Deserialize<ConnectionGridResponse>(json, JsonDefaults.Api);
        return grid?.Entries ?? [];
    }
}
