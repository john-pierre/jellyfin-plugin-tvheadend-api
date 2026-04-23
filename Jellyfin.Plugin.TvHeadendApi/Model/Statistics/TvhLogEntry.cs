using System;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Statistics;

/// <summary>
/// Represents a TVHeadend log message persisted to SQLite for historical dashboard viewing.
/// </summary>
public sealed class TvhLogEntry
{
    /// <summary>Gets or sets the database ID (primary key).</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the UTC timestamp when the log message was received.</summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>Gets or sets the log text from TVHeadend.</summary>
    public string Text { get; set; } = string.Empty;
}
