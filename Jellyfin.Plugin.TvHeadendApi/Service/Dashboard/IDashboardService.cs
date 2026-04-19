using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Dashboard;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Dashboard;

/// <summary>
/// Aggregates TVHeadend status data for the admin dashboard page.
/// </summary>
public interface IDashboardService
{
    /// <summary>
    /// Builds a full dashboard status snapshot by querying all relevant TVHeadend endpoints.
    /// Individual section failures are captured in error properties rather than throwing.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The aggregated dashboard status.</returns>
    Task<DashboardStatus> GetDashboardStatusAsync(CancellationToken cancellationToken);
}
