using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Profile;

/// <summary>
/// Resolves the output container for the configured TVHeadend streaming profile.
/// </summary>
public interface IProfileContainerResolver
{
    /// <summary>
    /// Resolves the effective output container for the configured (global) streaming profile.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolved container format.</returns>
    Task<string> ResolveContainerAsync(PluginConfiguration config, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the effective output container for a specific streaming profile.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="effectiveProfileName">
    /// The streaming profile name to resolve (e.g. the per-channel/client rule result).
    /// When <c>null</c> or empty, falls back to <see cref="PluginConfiguration.StreamingProfile"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolved container format.</returns>
    Task<string> ResolveContainerAsync(PluginConfiguration config, string? effectiveProfileName, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a snapshot of the configured (global) TVHeadend streaming profile including codec metadata.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolved profile snapshot.</returns>
    Task<ProfileSnapshot> ResolveProfileSnapshotAsync(PluginConfiguration config, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a snapshot of a specific TVHeadend streaming profile including codec metadata.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="effectiveProfileName">
    /// The streaming profile name to resolve. When <c>null</c> or empty, falls back to
    /// <see cref="PluginConfiguration.StreamingProfile"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolved profile snapshot.</returns>
    Task<ProfileSnapshot> ResolveProfileSnapshotAsync(PluginConfiguration config, string? effectiveProfileName, CancellationToken cancellationToken);

    /// <summary>
    /// Drops all cached profile snapshots so the next resolution fetches fresh data from TVHeadend.
    /// Must be called whenever streaming or codec profiles are created or rewritten (e.g. by the
    /// managed-profile provisioning flow); otherwise stale snapshots are served until the TTL expires
    /// and the MediaInfo cache is rebuilt with outdated container/codec metadata.
    /// </summary>
    void InvalidateCache();
}
