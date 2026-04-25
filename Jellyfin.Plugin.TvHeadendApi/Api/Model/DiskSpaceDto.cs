namespace Jellyfin.Plugin.TvHeadendApi.Api.Model;

/// <summary>
/// Represents disk-space information returned by the log endpoints.
/// </summary>
public class DiskSpaceDto
{
    /// <summary>
    /// Gets or sets the ISO 8601 timestamp of the last update.
    /// </summary>
    public string Timestamp { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the total disk space in bytes.
    /// </summary>
    public long Total { get; set; }

    /// <summary>
    /// Gets or sets the used disk space in bytes.
    /// </summary>
    public long Used { get; set; }

    /// <summary>
    /// Gets or sets the free disk space in bytes.
    /// </summary>
    public long Free { get; set; }

    /// <summary>
    /// Gets the percentage of disk space used.
    /// </summary>
    public double PercentUsed => Total > 0 ? (Used / (double)Total) * 100 : 0;
}
