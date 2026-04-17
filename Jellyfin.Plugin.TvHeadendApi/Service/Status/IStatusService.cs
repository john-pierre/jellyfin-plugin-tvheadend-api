using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Status;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Status;

/// <summary>
/// Provides access to TVHeadend server status and connection information.
/// </summary>
public interface IStatusService
{
    /// <summary>
    /// Gets the current server activity status including connection and subscription counts.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The activity status summary.</returns>
    Task<ActivityStatus?> GetActivityStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the list of active client connections.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Active connections.</returns>
    Task<IReadOnlyList<ConnectionEntry>> GetConnectionsAsync(CancellationToken cancellationToken);
}
