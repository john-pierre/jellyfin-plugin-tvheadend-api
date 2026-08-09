using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Logging;

/// <summary>
/// Result of parsing a single TVHeadend log line.
/// </summary>
public sealed class LogParseResult
{
    /// <summary>Gets or sets the parsed UTC timestamp (null when not parseable).</summary>
    public DateTime? ParsedTimestampUtc { get; set; }

    /// <summary>Gets or sets the normalized log level.</summary>
    public string Level { get; set; } = "Information";

    /// <summary>Gets or sets the detailed TVHeadend log type (e.g. tvheadend_warning).</summary>
    public string LogType { get; set; } = "tvheadend_info";

    /// <summary>Gets or sets the message with the timestamp prefix removed.</summary>
    public string MessageWithoutTimestamp { get; set; } = string.Empty;

    /// <summary>Gets or sets the detected category/subsystem (e.g. "mpegts", "linuxdvb").</summary>
    public string? Category { get; set; }

    /// <summary>Gets or sets a value indicating whether the timestamp was successfully parsed.</summary>
    public bool ParseSuccess { get; set; }

    /// <summary>Gets or sets the SHA-256 hash of the original line for deduplication.</summary>
    public string OriginalLineHash { get; set; } = string.Empty;
}
