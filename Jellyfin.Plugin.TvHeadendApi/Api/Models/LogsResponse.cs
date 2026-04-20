using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Api.Models;

/// <summary>
/// Response model for the logs endpoint, containing recent log messages and disk space info.
/// </summary>
public class LogsResponse
{
    /// <summary>
    /// Gets or sets recent log messages from TVHeadend.
    /// </summary>
    public IReadOnlyList<LogEntryDto> LogEntries { get; set; } = new List<LogEntryDto>();

    /// <summary>
    /// Gets or sets most recent disk space update, if available.
    /// </summary>
    public DiskSpaceDto? DiskSpace { get; set; }
}
