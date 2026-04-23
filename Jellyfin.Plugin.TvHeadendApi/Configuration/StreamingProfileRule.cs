// A rule that matches playback context (client, device, user) to a streaming profile override.

namespace Jellyfin.Plugin.TvHeadendApi.Configuration;

/// <summary>
/// A configurable rule that maps a client, device, or user to a specific playback mode and TVHeadend profile.
/// Rules are evaluated in priority order; the first enabled match wins.
/// </summary>
public class StreamingProfileRule
{
    /// <summary>
    /// Gets or sets a value indicating whether this rule is active.
    /// Disabled rules are skipped during resolution.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a short name identifying this rule (e.g. "Swiftfin iOS compatibility").
    /// Used in diagnostics output to explain why a profile was chosen.
    /// </summary>
    public string RuleName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional human-readable description of the rule's purpose.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the evaluation priority. Lower values are evaluated first.
    /// When multiple rules match, the one with the lowest priority value wins.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Gets or sets the type of match to perform against the playback context.
    /// </summary>
    public StreamingProfileRuleMatchType MatchType { get; set; }

    /// <summary>
    /// Gets or sets the value to match against (e.g. client name, device name, or user ID).
    /// Interpretation depends on <see cref="MatchType"/>.
    /// </summary>
    public string MatchValue { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the playback mode to use when this rule matches.
    /// </summary>
    public PlaybackMode PlaybackMode { get; set; } = PlaybackMode.Auto;

    /// <summary>
    /// Gets or sets the TVHeadend profile name to use when this rule matches.
    /// When empty and <see cref="PlaybackMode"/> is <see cref="Configuration.PlaybackMode.PassThrough"/>,
    /// the global default pass-through profile is used.
    /// </summary>
    public string TvHeadendProfileName { get; set; } = string.Empty;
}
