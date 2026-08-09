using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Api.Model;

/// <summary>
/// Describes which filters were applied to the dashboard logs query.
/// </summary>
public sealed class DashboardLogsFiltersApplied
{
    /// <summary>Gets or sets the source filter (null = all).</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets the level filter (null = all).</summary>
    public string? Level { get; set; }

    /// <summary>Gets or sets the type filter (null = all).</summary>
    public string? Type { get; set; }

    /// <summary>Gets or sets the search text (null = none).</summary>
    public string? Search { get; set; }

    /// <summary>Gets or sets the sort field.</summary>
    public string SortField { get; set; } = "created_at_utc";

    /// <summary>Gets or sets the sort direction.</summary>
    public string SortDirection { get; set; } = "desc";
}
