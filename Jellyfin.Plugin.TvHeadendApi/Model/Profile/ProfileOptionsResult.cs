using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Profile;

/// <summary>
/// Bundles available streaming and DVR profile names for configuration UI dropdowns.
/// </summary>
public sealed class ProfileOptionsResult
{
    /// <summary>
    /// Gets available TVHeadend streaming profile names.
    /// </summary>
    public IReadOnlyList<string> StreamingProfiles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Gets available TVHeadend DVR profile names.
    /// </summary>
    public IReadOnlyList<string> RecordingProfiles { get; init; } = Array.Empty<string>();
}
