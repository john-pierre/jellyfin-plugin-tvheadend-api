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
    /// Resolves the effective output container for the given plugin configuration.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolved container format.</returns>
    Task<string> ResolveContainerAsync(PluginConfiguration config, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a snapshot of the configured TVHeadend streaming profile including codec metadata.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolved profile snapshot.</returns>
    Task<ProfileSnapshot> ResolveProfileSnapshotAsync(PluginConfiguration config, CancellationToken cancellationToken);
}
