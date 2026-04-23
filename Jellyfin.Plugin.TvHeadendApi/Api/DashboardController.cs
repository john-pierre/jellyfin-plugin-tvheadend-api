using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Dashboard;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Dashboard;
using Jellyfin.Plugin.TvHeadendApi.Service.Relay;
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
    private readonly IRelayMetricsService _relayMetricsService;

    /// <summary>
    /// Initializes a new instance of the <see cref="DashboardController"/> class.
    /// </summary>
    /// <param name="dashboardService">The dashboard aggregation service.</param>
    /// <param name="relayMetricsService">The relay metrics service.</param>
    public DashboardController(IDashboardService dashboardService, IRelayMetricsService relayMetricsService)
    {
        _dashboardService = dashboardService ?? throw new ArgumentNullException(nameof(dashboardService));
        _relayMetricsService = relayMetricsService ?? throw new ArgumentNullException(nameof(relayMetricsService));
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

    /// <summary>
    /// Returns aggregated relay observability metrics for the dashboard.
    /// </summary>
    /// <param name="hours">Hours to look back: 1, 24, 168 (7d), 720 (30d), 0 (all).</param>
    /// <returns>Aggregated relay metrics summary.</returns>
    [HttpGet("RelayMetrics")]
    public ActionResult<RelayMetricsSummary> GetRelayMetrics([FromQuery] int hours = 24)
    {
        return Ok(_relayMetricsService.GetSummary(hours));
    }
}
