namespace Jellyfin.Plugin.TvHeadendApi.Api.Models;

/// <summary>
/// Represents a single log entry returned by the log endpoints.
/// </summary>
public class LogEntryDto
{
    /// <summary>
    /// Gets or sets the ISO 8601 timestamp of the log message.
    /// </summary>
    public string Timestamp { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the log message text.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}
