using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Common;

/// <summary>
/// Shared base for services that read a TVHeadend status grid endpoint
/// (<c>api/status/*</c>) and return its entries. Owns the common circuit-breaker check,
/// configuration guard, timeout handling, HTTP call, and grid deserialization so derived
/// services only supply the endpoint path and DTO types.
/// </summary>
/// <typeparam name="TGrid">Grid response DTO type.</typeparam>
/// <typeparam name="TEntry">Entry DTO type contained in the grid.</typeparam>
internal abstract class StatusGridService<TGrid, TEntry>
    where TGrid : class
{
    private readonly ILogger _logger;
    private readonly IApiClient _apiClient;
    private readonly IUrlBuilder _urlBuilder;
    private readonly IHealthService _healthService;

    /// <summary>
    /// Initializes a new instance of the <see cref="StatusGridService{TGrid, TEntry}"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="apiClient">TVHeadend API client.</param>
    /// <param name="urlBuilder">TVHeadend URL builder.</param>
    /// <param name="healthService">TVHeadend health tracking service.</param>
    protected StatusGridService(ILogger logger, IApiClient apiClient, IUrlBuilder urlBuilder, IHealthService healthService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _urlBuilder = urlBuilder ?? throw new ArgumentNullException(nameof(urlBuilder));
        _healthService = healthService ?? throw new ArgumentNullException(nameof(healthService));
    }

    /// <summary>
    /// Gets the service name used in log messages.
    /// </summary>
    protected abstract string ServiceName { get; }

    /// <summary>
    /// Gets the TVHeadend API endpoint path, e.g. <c>api/status/inputs</c>.
    /// </summary>
    protected abstract string EndpointPath { get; }

    /// <summary>
    /// Selects the entry list from the deserialized grid response.
    /// </summary>
    /// <param name="grid">The deserialized grid response.</param>
    /// <returns>The entries contained in the grid, or <c>null</c> when absent.</returns>
    protected abstract IReadOnlyList<TEntry>? SelectEntries(TGrid grid);

    /// <summary>
    /// Fetches the status grid endpoint and returns its entries.
    /// Returns an empty list when the circuit breaker is open or configuration is missing.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The grid entries, or an empty list.</returns>
    protected async Task<IReadOnlyList<TEntry>> FetchEntriesAsync(CancellationToken cancellationToken)
    {
        if (_healthService.ShouldBlockRequest())
        {
            _logger.LogDebug("{ServiceName}: circuit open, returning empty result", ServiceName);
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
        var url = _urlBuilder.BuildApiUrl(config, EndpointPath);
        var json = await _apiClient.GetStringAsync(httpClient, url, cts.Token).ConfigureAwait(false);
        var grid = JsonSerializer.Deserialize<TGrid>(json, JsonDefaults.Api);
        if (grid == null)
        {
            return [];
        }

        return SelectEntries(grid) ?? [];
    }
}
