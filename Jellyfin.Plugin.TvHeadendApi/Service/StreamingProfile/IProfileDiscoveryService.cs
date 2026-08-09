// Discovers and caches available TVHeadend streaming profiles.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;

/// <summary>
/// Discovers available TVHeadend streaming profiles and caches them for validation and UI display.
/// </summary>
public interface IProfileDiscoveryService
{
    /// <summary>
    /// Returns the cached list of available TVHeadend streaming profile names.
    /// Refreshes from TVHeadend if the cache has expired.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Available discovered profiles.</returns>
    Task<IReadOnlyList<DiscoveredProfile>> GetAvailableProfilesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Validates configured profile names against discovered profiles.
    /// Returns a list of warning messages for unknown or missing profiles.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation warnings (empty if all profiles are valid).</returns>
    Task<IReadOnlyList<string>> ValidateConfiguredProfilesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Forces a cache refresh from TVHeadend on the next access.
    /// </summary>
    void InvalidateCache();
}
