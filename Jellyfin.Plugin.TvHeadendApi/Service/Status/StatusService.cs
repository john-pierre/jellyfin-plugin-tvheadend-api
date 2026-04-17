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

    /// <summary>
    /// Initializes a new instance of the <see cref="StatusService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="apiClient">TVHeadend API client.</param>
    public StatusService(ILogger<StatusService> logger, IApiClient apiClient)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
    }

    /// <inheritdoc />
    public async Task<ActivityStatus?> GetActivityStatusAsync(CancellationToken cancellationToken)
    {
        var config = _apiClient.GetCurrentConfiguration();
        if (config == null)
        {
            _logger.LogWarning("Plugin configuration is not available.");
            return null;
        }

        using var httpClient = _apiClient.BuildHttpClient(config);
        var url = _apiClient.BuildUrl(config, "api/status/activity");
        var json = await _apiClient.GetStringAsync(httpClient, url, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ActivityStatus>(json, JsonDefaults.Api);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConnectionEntry>> GetConnectionsAsync(CancellationToken cancellationToken)
    {
        var config = _apiClient.GetCurrentConfiguration();
        if (config == null)
        {
            _logger.LogWarning("Plugin configuration is not available.");
            return [];
        }

        using var httpClient = _apiClient.BuildHttpClient(config);
        var url = _apiClient.BuildUrl(config, "api/status/connections");
        var json = await _apiClient.GetStringAsync(httpClient, url, cancellationToken).ConfigureAwait(false);
        var grid = JsonSerializer.Deserialize<ConnectionGridResponse>(json, JsonDefaults.Api);
        return grid?.Entries ?? [];
    }
}
