using System;
using System.Linq;
using Jellyfin.Plugin.TvHeadendApi.Api.Model;
using Jellyfin.Plugin.TvHeadendApi.Service;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Logging;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TvHeadendApi.Api.Endpoint;

/// <summary>
/// Dashboard logs endpoint — returns combined plugin and TVHeadend log entries
/// with server-side filtering, sorting, and searching.
/// </summary>
[ApiController]
[Route("TvHeadendApi/Dashboard/Logs")]
[Authorize(Policy = Policies.RequiresElevation)]
public class DashboardLogsController : ControllerBase
{
    private readonly IPluginLogQueryService _logService;
    private readonly ConfigurationProvider _configProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="DashboardLogsController"/> class.
    /// </summary>
    /// <param name="logService">The plugin log query service.</param>
    /// <param name="configProvider">The plugin configuration provider.</param>
    public DashboardLogsController(
        IPluginLogQueryService logService,
        ConfigurationProvider configProvider)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    /// <summary>
    /// Returns combined plugin and TVHeadend log entries with filtering and sorting.
    /// </summary>
    /// <param name="source">Filter by source: plugin, tvheadend, or null for all.</param>
    /// <param name="level">Filter by level: Trace, Debug, Information, Warning, Error, Critical.</param>
    /// <param name="type">Filter by log type.</param>
    /// <param name="search">Search text in message and category.</param>
    /// <param name="limit">Maximum entries to return. Default: from config (500).</param>
    /// <param name="sortField">Sort field: created_at_utc, source, level, type, category.</param>
    /// <param name="sortDirection">Sort direction: asc or desc.</param>
    /// <returns>The filtered, sorted log entries.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(DashboardLogsResponse), 200)]
    public ActionResult<DashboardLogsResponse> GetDashboardLogs(
        [FromQuery] string? source = null,
        [FromQuery] string? level = null,
        [FromQuery] string? type = null,
        [FromQuery] string? search = null,
        [FromQuery] int? limit = null,
        [FromQuery] string sortField = "created_at_utc",
        [FromQuery] string sortDirection = "desc")
    {
        var config = _configProvider.Configuration;
        var effectiveLimit = limit ?? config?.MaxDashboardLogEntries ?? 500;
        effectiveLimit = Math.Clamp(effectiveLimit, 1, 5000);

        var entries = _logService.QueryLogs(
            source: source,
            level: level,
            logType: type,
            search: search,
            limit: effectiveLimit,
            sortField: sortField,
            sortDirection: sortDirection);

        var totalCount = _logService.GetTotalCount(source, level, type, search);

        var response = new DashboardLogsResponse
        {
            Entries = entries.Select(e => new DashboardLogEntryDto
            {
                Id = e.Id,
                CreatedAtUtc = e.CreatedAtUtc.ToString("O"),
                Source = e.Source,
                Level = e.Level,
                Type = e.LogType,
                Category = e.Category,
                Message = e.Message,
                Exception = e.Exception,
                CorrelationId = e.CorrelationId,
            }).ToList(),
            TotalCount = totalCount,
            Limit = effectiveLimit,
            LastUpdatedUtc = DateTime.UtcNow.ToString("O"),
            FiltersApplied = new DashboardLogsFiltersApplied
            {
                Source = source,
                Level = level,
                Type = type,
                Search = search,
                SortField = sortField,
                SortDirection = sortDirection,
            },
        };

        return Ok(response);
    }
}
