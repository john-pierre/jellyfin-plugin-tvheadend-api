using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Stream;

/// <summary>
/// Handles lifecycle operations that are required by Jellyfin but not supported by TVHeadend.
/// </summary>
public interface ILifecycleService
{
    /// <summary>
    /// Handles a close-stream request from Jellyfin.
    /// </summary>
    /// <param name="id">Live stream id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    Task CloseLiveStreamAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Handles a tuner-reset request from Jellyfin.
    /// </summary>
    /// <param name="id">Tuner id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    Task ResetTunerAsync(string id, CancellationToken cancellationToken);
}
