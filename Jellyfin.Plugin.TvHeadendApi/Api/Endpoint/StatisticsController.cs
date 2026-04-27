using System;
using Jellyfin.Plugin.TvHeadendApi.Model.Statistic;
using Jellyfin.Plugin.TvHeadendApi.Service.Statistic;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TvHeadendApi.Api.Endpoint;

/// <summary>
/// API controller for live TV viewing statistics.
/// </summary>
[ApiController]
[Route("TvHeadendApi")]
[Authorize(Policy = Policies.RequiresElevation)]
public class StatisticsController : ControllerBase
{
    private readonly IStatisticsService _statisticsService;

    /// <summary>
    /// Initializes a new instance of the <see cref="StatisticsController"/> class.
    /// </summary>
    /// <param name="statisticsService">Service that tracks live TV viewing statistics.</param>
    public StatisticsController(IStatisticsService statisticsService)
    {
        ArgumentNullException.ThrowIfNull(statisticsService);
        _statisticsService = statisticsService;
    }

    /// <summary>
    /// Returns live TV viewing statistics for the given time range.
    /// </summary>
    /// <param name="days">Number of days to look back. 0 = all history.</param>
    /// <returns>Viewing sessions with user, device, channel, and play method data.</returns>
    [HttpGet("Statistics")]
    public ActionResult<ViewingStatisticsResult> GetStatistics([FromQuery] int days = 30)
    {
        return Ok(_statisticsService.GetStatistics(days));
    }

    /// <summary>
    /// Clears all recorded viewing statistics.
    /// </summary>
    /// <returns>Success confirmation.</returns>
    [HttpDelete("Statistics")]
    public ActionResult ClearStatistics()
    {
        _statisticsService.ClearStatistics();
        return Ok(new { Success = true, Message = "Viewing statistics cleared." });
    }
}
