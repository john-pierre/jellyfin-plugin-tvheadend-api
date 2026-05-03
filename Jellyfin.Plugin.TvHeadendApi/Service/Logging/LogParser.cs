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

/// <summary>
/// Parses TVHeadend log lines into structured components. Supports multiple
/// common TVHeadend log formats and extracts timestamp, level, category, and message.
/// </summary>
public static class LogParser
{
    // Format: "2026-04-24 13:20:15.123 [  WARNING] mpegts: ..."
    // Format: "2026-04-24 13:20:15 [  WARNING] mpegts: ..."
    private static readonly Regex TimestampLevelRegex = new(
        @"^(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d{1,6})?)\s+\[\s*(\w+)\s*\]\s*(.*)",
        RegexOptions.Compiled);

    // Format without brackets: "2026-04-24 13:20:15.123 WARNING: mpegts: ..."
    private static readonly Regex TimestampColonLevelRegex = new(
        @"^(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d{1,6})?)\s+(\w+):\s*(.*)",
        RegexOptions.Compiled);

    // Standalone bracket level: "[ WARNING] mpegts: ..."
    private static readonly Regex BracketLevelRegex = new(
        @"^\[\s*(\w+)\s*\]\s*(.*)",
        RegexOptions.Compiled);

    // Category detection: first word followed by colon, e.g. "mpegts: signal lost"
    private static readonly Regex CategoryRegex = new(
        @"^(\w[\w/.-]{0,63}):\s+(.*)",
        RegexOptions.Compiled);

    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-dd HH:mm:ss.FFFFFF",
        "yyyy-MM-dd HH:mm:ss.FFF",
        "yyyy-MM-dd HH:mm:ss.FF",
        "yyyy-MM-dd HH:mm:ss.F",
        "yyyy-MM-dd HH:mm:ss",
    ];

    // ── Content-based level reclassification ────────────────────────
    // TVHeadend emits many errors/warnings as INFO. These patterns detect
    // the actual severity from the message content.

    /// <summary>Patterns that indicate an error condition regardless of TVHeadend log level.</summary>
    private static readonly string[] ErrorPatterns =
    [
        "muxer reported errors",
        "continuity counter error",
        "corrupt input",
        "exceeds max",
        "codec frame size is not set",
        "unhandled error",
        "ERROR_BIT_COUNT",
        "unref short failure",
        "descramble failed",
        "TS desync",
        "table timeout",
        "unable to",
        "could not",
        "cannot ",
        "failed to",
        "cc error",
        "scrambled stream",
        "no free adapter",
        "no input source",
        "subscription error",
        "transport error",
        "read error",
        "write error",
        "pmt error",
        "invalid pid",
        "pat error",
        "stream error",
        "data timeout",
        "close timeout",
        "connection lost",
        "connection refused",
        "connection reset",
        "socket error",
        "broken pipe",
        "bad signal",
        "-- 400",
        "-- 401",
        "-- 403",
        "-- 404",
        "-- 405",
        "-- 408",
        "-- 429",
        "-- 500",
        "-- 502",
        "-- 503",
        "-- 504",
    ];

    /// <summary>Patterns that indicate a warning condition regardless of TVHeadend log level.</summary>
    private static readonly string[] WarningPatterns =
    [
        "no quality level set",
        "using default",
        "bitrate = 0",
        "Filtered out",
        "function not detected",
        "signal lost",
        "low snr",
        "weak signal",
        "service is encrypted",
        "PMT incomplete",
        "dropping frame",
        "skip frame",
        "buffer underflow",
        "buffer overflow",
        "queue full",
        "slow reader",
        "late frame",
        "discontinuity",
        "no data",
        "ts queue delay",
        "out of memory",
        "force close",
        "timeout waiting",
        "retry",
        "fallback",
        "recovering",
        "degraded",
        "tune failed",
        "scan no data",
        "no mux",
        "no service",
        "no transport",
        "lnb",
        "timeout",
    ];

    /// <summary>
    /// Parses a single TVHeadend log line into structured components.
    /// </summary>
    /// <param name="rawLine">The raw log line from TVHeadend.</param>
    /// <returns>A parsed result with timestamp, level, message, and category.</returns>
    public static LogParseResult Parse(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return new LogParseResult
            {
                MessageWithoutTimestamp = string.Empty,
                OriginalLineHash = ComputeHash(string.Empty),
            };
        }

        var result = new LogParseResult
        {
            OriginalLineHash = ComputeHash(rawLine),
        };

        string remainder;

        // Try: timestamp + [LEVEL] message
        var match = TimestampLevelRegex.Match(rawLine);
        if (match.Success)
        {
            result.ParsedTimestampUtc = TryParseTimestamp(match.Groups[1].Value);
            result.ParseSuccess = result.ParsedTimestampUtc.HasValue;
            MapLevel(result, match.Groups[2].Value);
            remainder = match.Groups[3].Value;
        }
        else
        {
            // Try: timestamp LEVEL: message
            match = TimestampColonLevelRegex.Match(rawLine);
            if (match.Success)
            {
                result.ParsedTimestampUtc = TryParseTimestamp(match.Groups[1].Value);
                result.ParseSuccess = result.ParsedTimestampUtc.HasValue;
                MapLevel(result, match.Groups[2].Value);
                remainder = match.Groups[3].Value;
            }
            else
            {
                // Try: [LEVEL] message (no timestamp)
                match = BracketLevelRegex.Match(rawLine);
                if (match.Success)
                {
                    MapLevel(result, match.Groups[1].Value);
                    remainder = match.Groups[2].Value;
                }
                else
                {
                    // Completely unparseable — store full line as message
                    remainder = rawLine;
                }
            }
        }

        // Extract category from remainder (e.g. "mpegts: signal lost")
        var catMatch = CategoryRegex.Match(remainder);
        if (catMatch.Success)
        {
            result.Category = catMatch.Groups[1].Value;
            result.MessageWithoutTimestamp = catMatch.Groups[2].Value.TrimEnd();
        }
        else
        {
            result.MessageWithoutTimestamp = remainder.TrimEnd();
        }

        // TVHeadend logs many errors/warnings as INFO — reclassify based on content.
        ReclassifyByContent(result, remainder);

        return result;
    }

    private static DateTime? TryParseTimestamp(string value)
    {
        if (DateTime.TryParseExact(
                value.Trim(),
                TimestampFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dt))
        {
            return dt;
        }

        return null;
    }

    private static void MapLevel(LogParseResult result, string rawLevel)
    {
        var upper = rawLevel.ToUpperInvariant();
        switch (upper)
        {
            case "TRACE":
            case "TRACE0":
                result.Level = "Trace";
                result.LogType = "tvheadend_debug";
                break;
            case "DEBUG":
            case "DBG":
                result.Level = "Debug";
                result.LogType = "tvheadend_debug";
                break;
            case "INFO":
            case "INFORMATION":
                result.Level = "Information";
                result.LogType = "tvheadend_info";
                break;
            case "NOTICE":
            case "NOT":
                result.Level = "Information";
                result.LogType = "tvheadend_notice";
                break;
            case "WARNING":
            case "WARN":
            case "WRN":
                result.Level = "Warning";
                result.LogType = "tvheadend_warning";
                break;
            case "ERROR":
            case "ERR":
            case "ALERT":
                result.Level = "Error";
                result.LogType = "tvheadend_error";
                break;
            case "CRITICAL":
            case "CRIT":
            case "EMERG":
            case "EMERGENCY":
                result.Level = "Critical";
                result.LogType = "tvheadend_error";
                break;
            default:
                result.Level = "Information";
                result.LogType = "tvheadend_info";
                break;
        }
    }

    /// <summary>
    /// Reclassifies the log level based on message content when TVHeadend reports INFO
    /// but the content indicates an error or warning condition.
    /// Only promotes (never demotes) the severity level.
    /// </summary>
    /// <param name="result">The parse result to potentially reclassify.</param>
    /// <param name="fullMessage">The full remainder text (category + message) for matching.</param>
    private static void ReclassifyByContent(LogParseResult result, string fullMessage)
    {
        // Only reclassify INFO-level entries — don't demote existing warnings/errors
        if (result.Level != "Information")
        {
            return;
        }

        var msg = fullMessage;

        // Check for error patterns first (higher severity takes priority)
        foreach (var pattern in ErrorPatterns)
        {
            if (msg.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                result.Level = "Error";
                result.LogType = "tvheadend_error";
                return;
            }
        }

        // Then check for warning patterns
        foreach (var pattern in WarningPatterns)
        {
            if (msg.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                result.Level = "Warning";
                result.LogType = "tvheadend_warning";
                return;
            }
        }
    }

    /// <summary>
    /// Computes a stable SHA-256 hash for the given input string.
    /// </summary>
    /// <param name="input">The string to hash.</param>
    /// <returns>The hexadecimal hash string.</returns>
    internal static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }
}
