using System;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Statistics;

/// <summary>
/// A unified log entry persisted to SQLite. Covers both plugin-originated
/// and TVHeadend-originated log messages in a single table.
/// </summary>
public sealed class PluginLogEntry
{
    /// <summary>Gets or sets the database row ID (primary key).</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets the UTC timestamp of the log event.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the log source.
    /// Values: <c>plugin</c>, <c>tvheadend</c>.
    /// </summary>
    public string Source { get; set; } = "plugin";

    /// <summary>
    /// Gets or sets the detailed log type.
    /// Examples: <c>trace</c>, <c>debug</c>, <c>information</c>, <c>warning</c>,
    /// <c>error</c>, <c>critical</c>, <c>tvheadend_debug</c>, <c>tvheadend_info</c>,
    /// <c>tvheadend_notice</c>, <c>tvheadend_warning</c>, <c>tvheadend_error</c>.
    /// </summary>
    public string LogType { get; set; } = "information";

    /// <summary>
    /// Gets or sets the normalized log level.
    /// Mapped values: <c>Trace</c>, <c>Debug</c>, <c>Information</c>,
    /// <c>Warning</c>, <c>Error</c>, <c>Critical</c>.
    /// </summary>
    public string Level { get; set; } = "Information";

    /// <summary>Gets or sets the logger category (e.g. class name or TVHeadend subsystem).</summary>
    public string? Category { get; set; }

    /// <summary>Gets or sets the log message text (without timestamp prefix for TVHeadend logs).</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the exception details, if any.</summary>
    public string? Exception { get; set; }

    /// <summary>Gets or sets an optional event ID.</summary>
    public string? EventId { get; set; }

    /// <summary>Gets or sets a correlation ID for request tracing.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Gets or sets the channel ID if the log is channel-scoped.</summary>
    public string? ChannelId { get; set; }

    /// <summary>Gets or sets the original raw line (TVHeadend only) for deduplication.</summary>
    public string? RawSource { get; set; }

    /// <summary>Gets or sets the SHA-256 hash of the original line + timestamp for deduplication.</summary>
    public string? RawLineHash { get; set; }

    /// <summary>Gets or sets the UTC timestamp when a TVHeadend log was imported.</summary>
    public DateTime? ImportedAtUtc { get; set; }
}
