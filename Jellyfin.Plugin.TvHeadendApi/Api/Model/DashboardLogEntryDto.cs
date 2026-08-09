using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Api.Model;

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
