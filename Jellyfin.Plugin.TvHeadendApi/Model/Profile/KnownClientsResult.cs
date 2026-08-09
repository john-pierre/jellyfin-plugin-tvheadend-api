using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Profile;

/// <summary>
/// Bundles the Jellyfin users, client app names, and device names known to this server.
/// Used by the configuration UI to offer selectable values for streaming profile rules
/// instead of free-text entry.
/// </summary>
public sealed class KnownClientsResult
{
    /// <summary>
    /// Gets the registered Jellyfin users, sorted by name.
    /// </summary>
    public IReadOnlyList<KnownUser> Users { get; init; } = Array.Empty<KnownUser>();

    /// <summary>
    /// Gets the known client application names (from active sessions and registered devices),
    /// deduplicated and sorted.
    /// </summary>
    public IReadOnlyList<string> Clients { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Gets the known device names (from active sessions and registered devices),
    /// deduplicated and sorted.
    /// </summary>
    public IReadOnlyList<string> Devices { get; init; } = Array.Empty<string>();
}
