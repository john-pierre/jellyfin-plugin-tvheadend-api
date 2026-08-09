using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Subscription;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Common;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Subscription;

/// <summary>
/// Retrieves TVHeadend active streaming subscriptions via the HTTP API.
/// </summary>
internal sealed class SubscriptionService : StatusGridService<SubscriptionGridResponse, SubscriptionEntry>, ISubscriptionService
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SubscriptionService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="apiClient">TVHeadend API client.</param>
    /// <param name="urlBuilder">TVHeadend URL builder.</param>
    /// <param name="healthService">TVHeadend health tracking service.</param>
    public SubscriptionService(ILogger<SubscriptionService> logger, IApiClient apiClient, IUrlBuilder urlBuilder, IHealthService healthService)
        : base(logger, apiClient, urlBuilder, healthService)
    {
    }

    /// <inheritdoc />
    protected override string ServiceName => nameof(SubscriptionService);

    /// <inheritdoc />
    protected override string EndpointPath => "api/status/subscriptions";

    /// <summary>
    /// Gets the list of active streaming subscriptions from TVHeadend.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Active subscription entries.</returns>
    public Task<IReadOnlyList<SubscriptionEntry>> GetActiveSubscriptionsAsync(CancellationToken cancellationToken)
        => FetchEntriesAsync(cancellationToken);

    /// <inheritdoc />
    protected override IReadOnlyList<SubscriptionEntry>? SelectEntries(SubscriptionGridResponse grid) => grid.Entries;
}
