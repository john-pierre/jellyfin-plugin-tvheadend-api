using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Dvr;

/// <summary>
/// Encapsulates TVHeadend DVR timer operations.
/// </summary>
public interface IDvrService
{
    /// <summary>Cancels a single timer.</summary>
    /// <param name="timerId">Timer ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    Task CancelTimerAsync(string timerId, CancellationToken cancellationToken);

    /// <summary>Cancels a series timer.</summary>
    /// <param name="timerId">Series timer ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken);

    /// <summary>Creates a single timer.</summary>
    /// <param name="info">Timer information.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken);

    /// <summary>Creates a series timer.</summary>
    /// <param name="info">Series timer information.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken);

    /// <summary>Updates a single timer.</summary>
    /// <param name="updatedTimer">Updated timer information.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken);

    /// <summary>Updates a series timer.</summary>
    /// <param name="info">Updated series timer information.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken);

    /// <summary>Gets all active timers.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All timer entries.</returns>
    Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken);

    /// <summary>Gets default values for a new series timer.</summary>
    /// <param name="program">Program to base defaults on.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Default series timer info.</returns>
    Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(ProgramInfo program, CancellationToken cancellationToken);

    /// <summary>Gets all series timers.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All series timer entries.</returns>
    Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken);

    /// <summary>Gets the UUID of a DVR recording profile by name.</summary>
    /// <param name="profileName">Profile name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The profile UUID.</returns>
    Task<string> GetRecordingProfileUuidAsync(string profileName, CancellationToken cancellationToken);
}
