// Top-level configuration section for streaming profile selection.

using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Configuration;

/// <summary>
/// Configuration section controlling how the plugin selects TVHeadend streaming profiles.
/// Supports a global default, per-channel overrides, per-channel-group overrides,
/// and rule-based selection by client, device, or user.
/// </summary>
public class StreamingProfileSettings
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StreamingProfileSettings"/> class
    /// with safe defaults that preserve existing behavior.
    /// </summary>
    public StreamingProfileSettings()
    {
        DefaultPlaybackMode = PlaybackMode.Auto;
        DefaultTvHeadendProfile = string.Empty;
        PassThroughProfile = "pass";
        JellyfinTranscodeProfile = "matroska";
        FallbackTvHeadendProfile = "pass";
        ChannelOverrides = new List<ChannelProfileOverride>();
        ChannelGroupOverrides = new List<ChannelGroupProfileOverride>();
        ClientRules = new List<StreamingProfileRule>();
        UserRules = new List<StreamingProfileRule>();
        EnableResolutionDiagnostics = false;
    }

    /// <summary>
    /// Gets or sets the global default playback mode.
    /// Used when no channel, group, client, or user override matches.
    /// </summary>
    public PlaybackMode DefaultPlaybackMode { get; set; }

    /// <summary>
    /// Gets or sets the default TVHeadend profile name.
    /// When empty, the legacy <see cref="PluginConfiguration.StreamingProfile"/> is used.
    /// </summary>
    public string DefaultTvHeadendProfile { get; set; }

    /// <summary>
    /// Gets or sets the TVHeadend profile name used for <see cref="PlaybackMode.PassThrough"/> mode.
    /// Typically "pass" or a similar non-transcoding profile.
    /// </summary>
    public string PassThroughProfile { get; set; }

    /// <summary>
    /// Gets or sets the TVHeadend profile name used for <see cref="PlaybackMode.JellyfinTranscode"/> mode.
    /// Should provide a probing-friendly container such as "matroska".
    /// </summary>
    public string JellyfinTranscodeProfile { get; set; }

    /// <summary>
    /// Gets or sets the ultimate fallback TVHeadend profile name.
    /// Used when the resolved profile is invalid or unavailable.
    /// </summary>
    public string FallbackTvHeadendProfile { get; set; }

    /// <summary>
    /// Gets or sets the per-channel profile overrides.
    /// Highest precedence in the resolution hierarchy.
    /// </summary>
    public List<ChannelProfileOverride> ChannelOverrides { get; set; }

    /// <summary>
    /// Gets or sets the per-channel-group profile overrides.
    /// Second highest precedence after channel overrides.
    /// </summary>
    public List<ChannelGroupProfileOverride> ChannelGroupOverrides { get; set; }

    /// <summary>
    /// Gets or sets rules that match by client name or device name.
    /// Evaluated in <see cref="StreamingProfileRule.Priority"/> order (lowest first).
    /// </summary>
    public List<StreamingProfileRule> ClientRules { get; set; }

    /// <summary>
    /// Gets or sets rules that match by user identity.
    /// Evaluated after client rules in the resolution hierarchy.
    /// </summary>
    public List<StreamingProfileRule> UserRules { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether profile resolution diagnostics
    /// are included in log output and diagnostic API responses.
    /// </summary>
    public bool EnableResolutionDiagnostics { get; set; }
}
