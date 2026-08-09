// Streaming telemetry API controller — provides live, historical, and session-detail metrics.

using Jellyfin.Plugin.TvHeadendApi.Model.Metrics;
using Jellyfin.Plugin.TvHeadendApi.Service.Metrics;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TvHeadendApi.Api;

/// <summary>
/// Streaming telemetry endpoints for real-time and historical relay metrics.
/// Provides visibility into active streams, startup quality, bandwidth, and failure classification.
/// </summary>
[ApiController]
[Route("TvHeadendApi/Metrics")]
[Authorize(Policy = Policies.RequiresElevation)]
public class MetricsController : ControllerBase
{
    private readonly IStreamingDashboardService _dashboardService;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetricsController"/> class.
    /// </summary>
    /// <param name="dashboardService">The streaming dashboard service.</param>
    public MetricsController(IStreamingDashboardService dashboardService)
    {
        _dashboardService = dashboardService ?? throw new System.ArgumentNullException(nameof(dashboardService));
    }

    /// <summary>
    /// Returns real-time metrics: active streams, bandwidth, clients, channels, failure counts.
    /// </summary>
    /// <returns>Live metrics snapshot.</returns>
    [HttpGet("Live")]
    [ProducesResponseType(typeof(LiveMetricsResponse), StatusCodes.Status200OK)]
    public ActionResult<LiveMetricsResponse> GetLiveMetrics()
    {
        return Ok(_dashboardService.GetLiveMetrics());
    }

    /// <summary>
    /// Returns historical metrics aggregation for today: totals, latencies, top channels/clients, failures.
    /// </summary>
    /// <returns>Historical metrics summary.</returns>
    [HttpGet("History")]
    [ProducesResponseType(typeof(HistoryMetricsResponse), StatusCodes.Status200OK)]
    public ActionResult<HistoryMetricsResponse> GetHistoryMetrics()
    {
        return Ok(_dashboardService.GetHistoryMetrics());
    }

    /// <summary>
    /// Returns full session detail including lifecycle timestamps, metrics, and event timeline.
    /// </summary>
    /// <param name="id">The session identifier.</param>
    /// <returns>Session detail or 404.</returns>
    [HttpGet("Session/{id}")]
    [ProducesResponseType(typeof(SessionMetricsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<SessionMetricsResponse> GetSessionDetail(string id)
    {
        var result = _dashboardService.GetSessionDetail(id);
        if (result == null)
        {
            return NotFound();
        }

        return Ok(result);
    }
}
