using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TvHeadendApi.Api.Models;
using Jellyfin.Plugin.TvHeadendApi.Service.Comet;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TvHeadendApi.Api.Endpoints;

/// <summary>
/// Endpoints for TVHeadend logs and disk space monitoring.
/// </summary>
[ApiController]
[Route("TvHeadendApi/Logs")]
[Authorize(Policy = Policies.RequiresElevation)]
public class LogsController : ControllerBase
{
    private readonly ICometSnapshotReader _cometSnapshotReader;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogsController"/> class.
    /// </summary>
    /// <param name="cometSnapshotReader">The buffered Comet snapshot source.</param>
    public LogsController(ICometSnapshotReader cometSnapshotReader)
    {
        _cometSnapshotReader = cometSnapshotReader ?? throw new ArgumentNullException(nameof(cometSnapshotReader));
    }

    private static int ClampCount(int count)
    {
        return Math.Clamp(count, 1, 500);
    }

    /// <summary>
    /// Gets recent TVHeadend logs and disk space information.
    /// </summary>
    /// <param name="count">Number of recent log entries to return (default: 100, max: 500).</param>
    /// <returns>Recent logs and disk space update.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(LogsResponse), 200)]
    public ActionResult<LogsResponse> GetLogs([FromQuery] int count = 100)
    {
        var logMessages = _cometSnapshotReader.GetRecentLogs(ClampCount(count));
        var diskSpace = _cometSnapshotReader.GetLastDiskSpaceUpdate();

        var response = new LogsResponse
        {
            LogEntries = logMessages.Select(log => new LogEntryDto
            {
                Timestamp = log.Timestamp.ToString("O"),
                Message = log.Text
            }).ToList(),
            DiskSpace = diskSpace != null ? new DiskSpaceDto
            {
                Timestamp = diskSpace.Timestamp.ToString("O"),
                Total = diskSpace.TotalDiskSpace,
                Used = diskSpace.UsedDiskSpace,
                Free = diskSpace.FreeDiskSpace
            }
            : null
        };

        return Ok(response);
    }

    /// <summary>
    /// Gets only the most recent disk space information.
    /// </summary>
    /// <returns>The disk space information if available, otherwise NotFound.</returns>
    [HttpGet("diskspace")]
    [ProducesResponseType(typeof(DiskSpaceDto), 200)]
    public ActionResult<DiskSpaceDto> GetDiskSpace()
    {
        var diskSpace = _cometSnapshotReader.GetLastDiskSpaceUpdate();
        if (diskSpace == null)
        {
            return NotFound("No disk space information available yet");
        }

        return Ok(new DiskSpaceDto
        {
            Timestamp = diskSpace.Timestamp.ToString("O"),
            Total = diskSpace.TotalDiskSpace,
            Used = diskSpace.UsedDiskSpace,
            Free = diskSpace.FreeDiskSpace
        });
    }

    /// <summary>
    /// Gets only recent log entries.
    /// </summary>
    /// <param name="count">Maximum number of log entries to retrieve. Default is 50, max is 500.</param>
    /// <returns>List of log entries.</returns>
    [HttpGet("entries")]
    [ProducesResponseType(typeof(List<LogEntryDto>), 200)]
    public ActionResult<List<LogEntryDto>> GetLogEntries([FromQuery] int count = 50)
    {
        var logMessages = _cometSnapshotReader.GetRecentLogs(ClampCount(count));
        var entries = logMessages.Select(log => new LogEntryDto
        {
            Timestamp = log.Timestamp.ToString("O"),
            Message = log.Text
        }).ToList();

        return Ok(entries);
    }
}
