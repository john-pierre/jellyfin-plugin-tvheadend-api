using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Subscription;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Subscription;

/// <summary>
/// Provides access to TVHeadend active streaming subscriptions.
/// </summary>
public interface ISubscriptionService
{
    /// <summary>
    /// Gets the list of active streaming subscriptions.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Active subscription entries.</returns>
    Task<IReadOnlyList<SubscriptionEntry>> GetActiveSubscriptionsAsync(CancellationToken cancellationToken);
}
