using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Statistic;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Health;

/// <summary>
/// Interface for the TVHeadend health tracking service.
/// </summary>
public interface IHealthService
{
    /// <summary>Gets the current health snapshot.</summary>
    /// <returns>A snapshot of the current TVHeadend health state.</returns>
    HealthSnapshot GetSnapshot();

    /// <summary>Records a successful TVHeadend interaction.</summary>
    /// <param name="responseTimeMs">Response time in milliseconds.</param>
    void RecordSuccess(int? responseTimeMs = null);

    /// <summary>Records a failed TVHeadend interaction.</summary>
    /// <param name="reason">The classified failure reason.</param>
    void RecordFailure(FailureReason reason);

    /// <summary>Returns true if requests should be blocked (circuit open + not yet half-open).</summary>
    /// <returns><c>true</c> if requests should be blocked; otherwise <c>false</c>.</returns>
    bool ShouldBlockRequest();

    /// <summary>Performs a lightweight health check against TVHeadend.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated health snapshot.</returns>
    Task<HealthSnapshot> CheckHealthAsync(CancellationToken cancellationToken);

    /// <summary>Gets the recent health transition history from SQLite.</summary>
    /// <param name="count">Maximum number of entries to return.</param>
    /// <returns>The requested health transitions in reverse chronological order.</returns>
    IReadOnlyList<HealthTransition> GetHealthHistory(int count = 100);
}
