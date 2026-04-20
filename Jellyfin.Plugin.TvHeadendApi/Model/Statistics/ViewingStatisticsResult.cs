using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Statistics;

/// <summary>
/// Aggregated viewing statistics returned by the statistics API endpoint.
/// </summary>
public sealed class ViewingStatisticsResult
{
    /// <summary>
    /// Gets the list of completed viewing sessions.
    /// </summary>
    public IReadOnlyList<ViewingSession> Sessions { get; init; } = [];

    /// <summary>
    /// Gets the list of currently active viewing sessions.
    /// </summary>
    public IReadOnlyList<ViewingSession> ActiveSessions { get; init; } = [];

    /// <summary>
    /// Gets or sets the total number of sessions in the requested range, including active sessions.
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// Gets or sets the number of currently active sessions.
    /// </summary>
    public int ActiveCount { get; set; }
}
