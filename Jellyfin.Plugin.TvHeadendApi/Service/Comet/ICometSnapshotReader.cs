using System;
using System.Collections.Generic;
using Jellyfin.Plugin.TvHeadendApi.Model.Statistics;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Comet;

/// <summary>
/// Exposes buffered Comet snapshots for controllers and dashboard consumers.
/// </summary>
public interface ICometSnapshotReader
{
    /// <summary>
    /// Gets the most recent log messages from the in-memory buffer.
    /// </summary>
    /// <param name="count">Maximum number of entries to return.</param>
    /// <returns>The requested log entries in chronological order.</returns>
    IReadOnlyList<LogMessage> GetRecentLogs(int count = 100);

    /// <summary>
    /// Gets historical TVHeadend log entries from SQLite persistence.
    /// </summary>
    /// <param name="count">Maximum number of entries. Default: 500.</param>
    /// <param name="sinceUtc">Optional: only return entries after this timestamp.</param>
    /// <returns>Log entries in reverse chronological order.</returns>
    IReadOnlyList<TvhLogEntry> GetLogHistory(int count = 500, DateTime? sinceUtc = null);

    /// <summary>
    /// Gets the latest buffered disk-space notification.
    /// </summary>
    /// <returns>The most recent disk-space update, or <c>null</c> when none is available.</returns>
    DiskSpaceUpdate? GetLastDiskSpaceUpdate();
}
