using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Status;
using Jellyfin.Plugin.TvHeadendApi.Model.Subscription;
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
    private readonly ITvHeadendHealthService _healthService;

    /// <summary>
    /// Initializes a new instance of the <see cref="StatusService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="apiClient">TVHeadend API client.</param>
    /// <param name="urlBuilder">TVHeadend URL builder.</param>
    /// <param name="healthService">TVHeadend health tracking service.</param>
    public StatusService(ILogger<StatusService> logger, IApiClient apiClient, IUrlBuilder urlBuilder, ITvHeadendHealthService healthService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _urlBuilder = urlBuilder ?? throw new ArgumentNullException(nameof(urlBuilder));
        _healthService = healthService ?? throw new ArgumentNullException(nameof(healthService));
    }

    /// <summary>
    /// Gets current activity counters synthesized from dedicated status endpoints.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The activity status summary, or <c>null</c> if the configuration is unavailable.</returns>
    public async Task<ActivityStatus?> GetActivityStatusAsync(CancellationToken cancellationToken)
    {
        if (_healthService.ShouldBlockRequest())
        {
            _logger.LogDebug("StatusService: circuit open, returning null activity status");
            return null;
        }

        var config = _apiClient.GetCurrentConfiguration();
        if (config == null)
        {
            _logger.LogWarning("Plugin configuration is not available.");
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(OperationTimeouts.Metadata);

        using var httpClient = _apiClient.CreateApiHttpClient(config);
        var subscriptionsUrl = _urlBuilder.BuildApiUrl(config, "api/status/subscriptions");
        var subscriptionsJson = await _apiClient.GetStringAsync(httpClient, subscriptionsUrl, cts.Token).ConfigureAwait(false);
        var subscriptionsGrid = JsonSerializer.Deserialize<SubscriptionGridResponse>(subscriptionsJson, JsonDefaults.Api);

        var connectionsUrl = _urlBuilder.BuildApiUrl(config, "api/status/connections");
        var connectionsJson = await _apiClient.GetStringAsync(httpClient, connectionsUrl, cts.Token).ConfigureAwait(false);
        var connectionsGrid = JsonSerializer.Deserialize<ConnectionGridResponse>(connectionsJson, JsonDefaults.Api);

        return new ActivityStatus
        {
            CurrentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            NextActivity = 0,
            SubscriptionCount = subscriptionsGrid?.Entries?.Count ?? 0,
            ConnectionCount = connectionsGrid?.Entries?.Count ?? 0,
        };
    }

    /// <summary>
    /// Gets the list of active client connections from TVHeadend.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Active connections.</returns>
    public async Task<IReadOnlyList<ConnectionEntry>> GetConnectionsAsync(CancellationToken cancellationToken)
    {
        if (_healthService.ShouldBlockRequest())
        {
            _logger.LogDebug("StatusService: circuit open, returning empty connections");
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
        var url = _urlBuilder.BuildApiUrl(config, "api/status/connections");
        var json = await _apiClient.GetStringAsync(httpClient, url, cts.Token).ConfigureAwait(false);
        var grid = JsonSerializer.Deserialize<ConnectionGridResponse>(json, JsonDefaults.Api);
        return grid?.Entries ?? [];
    }
}
