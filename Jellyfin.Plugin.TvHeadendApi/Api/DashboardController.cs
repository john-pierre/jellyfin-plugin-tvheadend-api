using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Dashboard;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Dashboard;
using Jellyfin.Plugin.TvHeadendApi.Service.Logging;
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
    private readonly IRelayTokenRepository _tokenRepository;
    private readonly IPluginLogQueryService _logService;

    /// <summary>
    /// Initializes a new instance of the <see cref="DashboardController"/> class.
    /// </summary>
    /// <param name="dashboardService">The dashboard aggregation service.</param>
    /// <param name="relayMetricsService">The relay metrics service.</param>
    /// <param name="tokenRepository">The relay token repository.</param>
    /// <param name="logService">The plugin log query service.</param>
    public DashboardController(
        IDashboardService dashboardService,
        IRelayMetricsService relayMetricsService,
        IRelayTokenRepository tokenRepository,
        IPluginLogQueryService logService)
    {
        _dashboardService = dashboardService ?? throw new ArgumentNullException(nameof(dashboardService));
        _relayMetricsService = relayMetricsService ?? throw new ArgumentNullException(nameof(relayMetricsService));
        _tokenRepository = tokenRepository ?? throw new ArgumentNullException(nameof(tokenRepository));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
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

    /// <summary>
    /// Returns relay token statistics: total, active (stream/image), expired, revoked counts.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Token statistics summary.</returns>
    [HttpGet("Dashboard/Tokens")]
    public async Task<ActionResult<RelayTokenStatistics>> GetTokenStatistics(CancellationToken cancellationToken)
    {
        return Ok(await _tokenRepository.GetTokenStatisticsAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Revokes all active relay tokens. Useful for security rotation or troubleshooting.
    /// Affected clients will need to re-acquire tokens on their next request.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of tokens revoked.</returns>
    [HttpPost("Dashboard/Tokens/RevokeAll")]
    public async Task<ActionResult<object>> RevokeAllTokens(CancellationToken cancellationToken)
    {
        var count = await _tokenRepository.RevokeAllAsync("Admin revoke-all via dashboard", cancellationToken).ConfigureAwait(false);
        return Ok(new { RevokedCount = count });
    }

    /// <summary>
    /// Deletes all persisted relay metrics.
    /// </summary>
    /// <returns>The number of deleted rows.</returns>
    [HttpDelete("RelayMetrics")]
    public ActionResult<object> ClearRelayMetrics()
    {
        var count = _relayMetricsService.ClearAll();
        return Ok(new { DeletedCount = count, Message = "Relay metrics cleared." });
    }

    /// <summary>
    /// Deletes all persisted plugin and TVHeadend log entries.
    /// </summary>
    /// <returns>The number of deleted rows.</returns>
    [HttpDelete("Dashboard/Logs")]
    public ActionResult<object> ClearLogs()
    {
        var count = _logService.ClearAll();
        return Ok(new { DeletedCount = count, Message = "Log entries cleared." });
    }
}
