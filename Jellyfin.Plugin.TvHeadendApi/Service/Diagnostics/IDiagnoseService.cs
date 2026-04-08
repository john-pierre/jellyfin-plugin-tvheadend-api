using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Diagnostics;

/// <summary>
/// Provides diagnostic reports for the TVHeadend integration.
/// </summary>
public interface IDiagnoseService
{
    /// <summary>
    /// Executes the TVHeadend diagnostic workflow.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The completed diagnostic report.</returns>
    Task<DiagnoseResult> DiagnoseAsync(CancellationToken cancellationToken);
}
