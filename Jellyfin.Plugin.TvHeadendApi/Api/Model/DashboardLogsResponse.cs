using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Api.Model;

/// <summary>
/// Response model for the dashboard logs endpoint. Contains paginated, filtered
/// log entries from both plugin and TVHeadend sources.
/// </summary>
public sealed class DashboardLogsResponse
{
    /// <summary>Gets or sets the log entries matching the query.</summary>
    public IReadOnlyList<DashboardLogEntryDto> Entries { get; set; } = Array.Empty<DashboardLogEntryDto>();

    /// <summary>Gets or sets the total count of matching entries (before limit).</summary>
    public int TotalCount { get; set; }

    /// <summary>Gets or sets the applied limit.</summary>
    public int Limit { get; set; }

    /// <summary>Gets or sets the UTC timestamp of the last data refresh.</summary>
    public string LastUpdatedUtc { get; set; } = string.Empty;

    /// <summary>Gets or sets which filters were applied.</summary>
    public DashboardLogsFiltersApplied FiltersApplied { get; set; } = new();
}
