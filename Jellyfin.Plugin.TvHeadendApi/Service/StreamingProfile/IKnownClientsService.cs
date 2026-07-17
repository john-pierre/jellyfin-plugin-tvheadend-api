// Aggregates Jellyfin users, client names, and device names for rule editors.

using Jellyfin.Plugin.TvHeadendApi.Model.Profile;

namespace Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;

/// <summary>
/// Aggregates the Jellyfin users, client application names, and device names known to this
/// server so the configuration UI can offer them as selectable match values for
/// streaming profile rules instead of free-text entry.
/// </summary>
public interface IKnownClientsService
{
    /// <summary>
    /// Collects registered users, plus client and device names from active sessions and
    /// the device registry. Names are deduplicated case-insensitively and sorted.
    /// Failing sources are skipped so a partial result is still returned.
    /// </summary>
    /// <returns>The known users, client names, and device names.</returns>
    KnownClientsResult GetKnownClients();
}
