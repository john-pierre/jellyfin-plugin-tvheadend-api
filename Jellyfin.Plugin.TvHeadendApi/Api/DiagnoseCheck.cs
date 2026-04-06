namespace Jellyfin.Plugin.TvHeadendApi.Api;

/// <summary>
/// A single diagnostic check result with category, status, and optional recommendation.
/// </summary>
public class DiagnoseCheck
{
    /// <summary>Gets or sets the check category (e.g. "Connection", "Streaming", "Playback", "FFmpeg", "Recording").</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Gets or sets the check name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the check status: "OK", "WARNING", "ERROR", or "INFO".</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the check message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional recommendation to resolve the issue.</summary>
    public string Recommendation { get; set; } = string.Empty;
}
