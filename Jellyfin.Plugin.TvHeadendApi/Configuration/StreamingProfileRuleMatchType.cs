// Defines the match types available for streaming profile rules.

namespace Jellyfin.Plugin.TvHeadendApi.Configuration;

/// <summary>
/// Specifies how a streaming profile rule matches against the playback context.
/// </summary>
public enum StreamingProfileRuleMatchType
{
    /// <summary>
    /// Matches when the client name equals the match value exactly (case-insensitive).
    /// </summary>
    ClientNameExact = 0,

    /// <summary>
    /// Matches when the client name contains the match value (case-insensitive).
    /// </summary>
    ClientNameContains = 1,

    /// <summary>
    /// Matches when the device name equals the match value exactly (case-insensitive).
    /// </summary>
    DeviceNameExact = 2,

    /// <summary>
    /// Matches when the device name contains the match value (case-insensitive).
    /// </summary>
    DeviceNameContains = 3,

    /// <summary>
    /// Matches when the Jellyfin user ID equals the match value exactly.
    /// </summary>
    UserIdExact = 4,
}
