using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Diagnostic;

/// <summary>
/// Comprehensive diagnostic result model returned by the Diagnose endpoint.
/// Provides connection status, compatibility scoring, and detailed per-category checks.
/// </summary>
public sealed class DiagnoseResult
{
    /// <summary>
    /// Gets or sets the overall status: "OK", "WARNING", or "ERROR".
    /// </summary>
    public string OverallStatus { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the compatibility score (0–100).
    /// </summary>
    public int CompatibilityScore { get; set; } = 100;

    /// <summary>
    /// Gets or sets the connection status string.
    /// </summary>
    public string Connection { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the TVHeadend server version.
    /// </summary>
    public string ServerVersion { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the network latency to TVHeadend in milliseconds.
    /// </summary>
    public int LatencyMs { get; set; }

    /// <summary>
    /// Gets or sets the number of channels.
    /// </summary>
    public int ChannelCount { get; set; }

    /// <summary>
    /// Gets or sets the number of DVR entries.
    /// </summary>
    public int DvrEntryCount { get; set; }

    /// <summary>
    /// Gets or sets the stream details cache status description.
    /// </summary>
    public string CacheStatus { get; set; } = string.Empty;

    /// <summary>
    /// Gets the list of available streaming profiles.
    /// </summary>
    public IList<string> AvailableStreamingProfiles { get; } = new List<string>();

    /// <summary>
    /// Gets the list of available DVR/recording profiles.
    /// </summary>
    public IList<string> AvailableRecordingProfiles { get; } = new List<string>();

    /// <summary>
    /// Gets the current plugin settings summary lines.
    /// </summary>
    public IList<string> PluginSettings { get; } = new List<string>();

    /// <summary>
    /// Gets any warnings detected.
    /// </summary>
    public IList<string> Warnings { get; } = new List<string>();

    /// <summary>
    /// Gets configuration recommendations.
    /// </summary>
    public IList<string> Recommendations { get; } = new List<string>();

    /// <summary>
    /// Gets the list of detailed diagnostic checks.
    /// </summary>
    public IList<DiagnoseCheck> Checks { get; } = new List<DiagnoseCheck>();
}
