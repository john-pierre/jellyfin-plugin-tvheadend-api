using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Subscription;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Subscription;

/// <summary>
/// Retrieves TVHeadend active streaming subscriptions via the HTTP API.
/// </summary>
internal sealed class SubscriptionService : ISubscriptionService
{
    private readonly ILogger<SubscriptionService> _logger;
    private readonly IApiClient _apiClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubscriptionService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="apiClient">TVHeadend API client.</param>
    public SubscriptionService(ILogger<SubscriptionService> logger, IApiClient apiClient)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
    }

    /// <summary>
    /// Gets the list of active streaming subscriptions from TVHeadend.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Active subscription entries.</returns>
    public async Task<IReadOnlyList<SubscriptionEntry>> GetActiveSubscriptionsAsync(CancellationToken cancellationToken)
    {
        var config = _apiClient.GetCurrentConfiguration();
        if (config == null)
        {
            _logger.LogWarning("Plugin configuration is not available.");
            return [];
        }

        using var httpClient = _apiClient.BuildHttpClient(config);
        var url = _apiClient.BuildUrl(config, "api/status/subscriptions");
        var json = await _apiClient.GetStringAsync(httpClient, url, cancellationToken).ConfigureAwait(false);
        var grid = JsonSerializer.Deserialize<SubscriptionGridResponse>(json, JsonDefaults.Api);
        return grid?.Entries ?? [];
    }
}
