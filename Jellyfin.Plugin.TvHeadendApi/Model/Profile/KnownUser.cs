using System;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Profile;

/// <summary>
/// A Jellyfin user reference (id and display name) offered as a selectable
/// match value for user-based streaming profile rules.
/// </summary>
public sealed class KnownUser
{
    /// <summary>
    /// Gets the Jellyfin user id in dashed GUID format.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Gets the Jellyfin user name.
    /// </summary>
    public string Name { get; init; } = string.Empty;
}
