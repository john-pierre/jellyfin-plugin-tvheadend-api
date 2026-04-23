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
    private readonly IUrlBuilder _urlBuilder;
    private readonly ITvHeadendHealthService _healthService;

    /// <summary>
    /// Initializes a new instance of the <see cref="InputMonitorService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="apiClient">TVHeadend API client.</param>
    /// <param name="urlBuilder">TVHeadend URL builder.</param>
    /// <param name="healthService">TVHeadend health tracking service.</param>
    public InputMonitorService(ILogger<InputMonitorService> logger, IApiClient apiClient, IUrlBuilder urlBuilder, ITvHeadendHealthService healthService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _urlBuilder = urlBuilder ?? throw new ArgumentNullException(nameof(urlBuilder));
        _healthService = healthService ?? throw new ArgumentNullException(nameof(healthService));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InputStatusEntry>> GetInputStatusAsync(CancellationToken cancellationToken)
    {
        if (_healthService.ShouldBlockRequest())
        {
            _logger.LogDebug("InputMonitorService: circuit open, returning empty inputs");
            return [];
        }

        var config = _apiClient.GetCurrentConfiguration();
        if (config == null)
        {
            _logger.LogWarning("Plugin configuration is not available.");
            return [];
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(OperationTimeouts.Metadata);

        using var httpClient = _apiClient.CreateApiHttpClient(config);
        var url = _urlBuilder.BuildApiUrl(config, "api/status/inputs");
        var json = await _apiClient.GetStringAsync(httpClient, url, cts.Token).ConfigureAwait(false);
        var grid = JsonSerializer.Deserialize<InputGridResponse>(json, JsonDefaults.Api);
        return grid?.Entries ?? [];
    }
}
