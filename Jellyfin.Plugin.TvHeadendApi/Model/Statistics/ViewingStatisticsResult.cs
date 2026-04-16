using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Statistics;

/// <summary>
/// Aggregated viewing statistics returned by the statistics API endpoint.
/// </summary>
public sealed class ViewingStatisticsResult
{
    /// <summary>
    /// Gets the list of viewing sessions.
    /// </summary>
    public IReadOnlyList<ViewingSession> Sessions { get; init; } = [];

    /// <summary>
    /// Gets or sets the total number of sessions (before any limit was applied).
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// Gets or sets the number of currently active sessions.
    /// </summary>
    public int ActiveCount { get; set; }
}
