using System;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Comet;

/// <summary>
/// Represents the latest disk-space snapshot pushed by TVHeadend.
/// </summary>
public sealed class DiskSpaceUpdate
{
    /// <summary>
    /// Gets or sets the UTC timestamp when the update was buffered.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the total disk space in bytes.
    /// </summary>
    public long TotalDiskSpace { get; set; }

    /// <summary>
    /// Gets or sets the used disk space in bytes.
    /// </summary>
    public long UsedDiskSpace { get; set; }

    /// <summary>
    /// Gets or sets the free disk space in bytes.
    /// </summary>
    public long FreeDiskSpace { get; set; }
}
