// Output of the streaming profile resolution — the effective profile, mode, and debug reasons.

using System.Collections.Generic;
using Jellyfin.Plugin.TvHeadendApi.Configuration;

namespace Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;

/// <summary>
/// The result of streaming profile resolution, including the effective profile name,
/// playback mode, which hierarchy level produced the result, and debug reasons.
/// </summary>
public sealed class StreamingProfileResolutionResult
{
    /// <summary>
    /// Gets or sets the effective playback mode after resolution.
    /// </summary>
    public PlaybackMode EffectivePlaybackMode { get; set; }

    /// <summary>
    /// Gets or sets the effective TVHeadend profile name to use in the stream URL.
    /// </summary>
    public string EffectiveTvHeadendProfile { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets which level in the resolution hierarchy produced this result.
    /// </summary>
    public ResolutionSource Source { get; set; }

    /// <summary>
    /// Gets or sets the name of the matched rule, if a rule-based source was used.
    /// </summary>
    public string? MatchedRuleName { get; set; }

    /// <summary>
    /// Gets the ordered list of textual reasons explaining each resolution step.
    /// </summary>
    public List<string> Reasons { get; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether a fallback was used because
    /// the primary resolved profile was empty or unavailable.
    /// </summary>
    public bool UsedFallback { get; set; }
}

