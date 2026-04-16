using System.Collections.Generic;
using Jellyfin.Plugin.TvHeadendApi.Model.Statistics;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Statistics;

/// <summary>
/// Tracks and provides live TV viewing statistics.
/// </summary>
public interface IStatisticsService
{
    /// <summary>
    /// Gets all recorded sessions (unfiltered).
    /// </summary>
    IReadOnlyList<ViewingSession> AllSessions { get; }

    /// <summary>
    /// Gets viewing sessions filtered by the given number of days.
    /// </summary>
    /// <param name="days">Number of days to look back. 0 = all.</param>
    /// <returns>Aggregated statistics result.</returns>
    ViewingStatisticsResult GetStatistics(int days);

    /// <summary>
    /// Clears all recorded viewing statistics.
    /// </summary>
    void ClearStatistics();
}
