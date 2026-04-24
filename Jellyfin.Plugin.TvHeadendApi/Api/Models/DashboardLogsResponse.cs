using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Api.Models;

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

/// <summary>
/// A single log entry in the dashboard response.
/// </summary>
public sealed class DashboardLogEntryDto
{
    /// <summary>Gets or sets the database ID.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets the ISO 8601 UTC timestamp.</summary>
    public string CreatedAtUtc { get; set; } = string.Empty;

    /// <summary>Gets or sets the log source (plugin or tvheadend).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets the normalized log level.</summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>Gets or sets the detailed log type.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the category / subsystem.</summary>
    public string? Category { get; set; }

    /// <summary>Gets or sets the log message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the exception text (null if none).</summary>
    public string? Exception { get; set; }

    /// <summary>Gets or sets the correlation ID, if present.</summary>
    public string? CorrelationId { get; set; }
}

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
