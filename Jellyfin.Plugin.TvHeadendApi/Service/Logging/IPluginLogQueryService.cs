using System.Collections.Generic;
using Jellyfin.Plugin.TvHeadendApi.Model.Statistic;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Logging;

/// <summary>
/// Public contract for querying persisted plugin and TVHeadend log entries.
/// </summary>
public interface IPluginLogQueryService
{
    /// <summary>
    /// Queries persisted log entries for the dashboard.
    /// </summary>
    /// <param name="source">Filter by source (plugin, tvheadend) or null for all.</param>
    /// <param name="level">Filter by level or null for all.</param>
    /// <param name="logType">Filter by log type or null for all.</param>
    /// <param name="search">Search text in message/category or null.</param>
    /// <param name="limit">Maximum entries to return.</param>
    /// <param name="sortField">Sort field name.</param>
    /// <param name="sortDirection">Sort direction (asc or desc).</param>
    /// <returns>The matching log entries.</returns>
    IReadOnlyList<PluginLogEntry> QueryLogs(
        string? source = null,
        string? level = null,
        string? logType = null,
        string? search = null,
        int limit = 500,
        string sortField = "created_at_utc",
        string sortDirection = "desc");

    /// <summary>
    /// Gets the total count matching the given filters.
    /// </summary>
    /// <param name="source">Filter by source or null for all.</param>
    /// <param name="level">Filter by level or null for all.</param>
    /// <param name="logType">Filter by log type or null for all.</param>
    /// <param name="search">Search text or null.</param>
    /// <returns>The total matching entry count.</returns>
    int GetTotalCount(
        string? source = null,
        string? level = null,
        string? logType = null,
        string? search = null);

    /// <summary>
    /// Deletes all persisted log entries.
    /// </summary>
    /// <returns>The number of deleted rows.</returns>
    int ClearAll();
}
