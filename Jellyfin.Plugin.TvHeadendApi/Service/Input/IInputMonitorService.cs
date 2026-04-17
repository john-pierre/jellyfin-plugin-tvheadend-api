using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Input;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Input;

/// <summary>
/// Provides access to TVHeadend TV input/tuner status information.
/// </summary>
public interface IInputMonitorService
{
    /// <summary>
    /// Gets the current status of all TV inputs (tuners/adapters).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Input status entries for each active tuner.</returns>
    Task<IReadOnlyList<InputStatusEntry>> GetInputStatusAsync(CancellationToken cancellationToken);
}
