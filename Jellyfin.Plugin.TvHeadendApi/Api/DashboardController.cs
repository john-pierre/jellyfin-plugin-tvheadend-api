using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Dashboard;
using Jellyfin.Plugin.TvHeadendApi.Service.Dashboard;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TvHeadendApi.Api;

/// <summary>
/// API controller that provides the aggregated dashboard status for the TvHeadend admin page.
/// </summary>
[ApiController]
[Route("TvHeadendApi")]
[Authorize(Policy = Policies.RequiresElevation)]
public class DashboardController : ControllerBase
{
    private readonly IDashboardService _dashboardService;

    /// <summary>
    /// Initializes a new instance of the <see cref="DashboardController"/> class.
    /// </summary>
    /// <param name="dashboardService">The dashboard aggregation service.</param>
    public DashboardController(IDashboardService dashboardService)
    {
        _dashboardService = dashboardService ?? throw new ArgumentNullException(nameof(dashboardService));
    }

    /// <summary>
    /// Returns an aggregated dashboard status snapshot including connection health,
    /// tuner state, subscriptions, connections, and summary statistics.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The aggregated dashboard status.</returns>
    [HttpGet("Dashboard")]
    public async Task<ActionResult<DashboardStatus>> GetDashboard(CancellationToken cancellationToken)
    {
        return Ok(await _dashboardService.GetDashboardStatusAsync(cancellationToken).ConfigureAwait(false));
    }
}
