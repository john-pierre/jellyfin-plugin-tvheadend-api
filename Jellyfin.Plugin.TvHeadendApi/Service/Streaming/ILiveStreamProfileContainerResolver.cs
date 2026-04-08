using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Streaming;

/// <summary>
/// Resolves the output container for the configured TVHeadend streaming profile.
/// </summary>
public interface ILiveStreamProfileContainerResolver
{
    /// <summary>
    /// Resolves the effective output container for the given plugin configuration.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolved container format.</returns>
    Task<string> ResolveContainerAsync(PluginConfiguration config, CancellationToken cancellationToken);
}
