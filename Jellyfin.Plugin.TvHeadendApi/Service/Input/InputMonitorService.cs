using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Input;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Input;

/// <summary>
/// Retrieves TVHeadend TV input/tuner status via the HTTP API.
/// </summary>
internal sealed class InputMonitorService : IInputMonitorService
{
    private readonly ILogger<InputMonitorService> _logger;
    private readonly IApiClient _apiClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="InputMonitorService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="apiClient">TVHeadend API client.</param>
    public InputMonitorService(ILogger<InputMonitorService> logger, IApiClient apiClient)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InputStatusEntry>> GetInputStatusAsync(CancellationToken cancellationToken)
    {
        var config = _apiClient.GetCurrentConfiguration();
        if (config == null)
        {
            _logger.LogWarning("Plugin configuration is not available.");
            return [];
        }

        using var httpClient = _apiClient.BuildHttpClient(config);
        var url = _apiClient.BuildUrl(config, "api/status/inputs");
        var json = await _apiClient.GetStringAsync(httpClient, url, cancellationToken).ConfigureAwait(false);
        var grid = JsonSerializer.Deserialize<InputGridResponse>(json, JsonDefaults.Api);
        return grid?.Entries ?? [];
    }
}
