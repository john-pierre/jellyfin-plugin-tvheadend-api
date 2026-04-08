using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;

#pragma warning disable CS1591

namespace Jellyfin.Plugin.TvHeadendApi.Service.Dvr;

/// <summary>
/// Encapsulates TVHeadend DVR timer operations.
/// </summary>
public interface ITvheadendDvrService
{
    Task CancelTimerAsync(string timerId, CancellationToken cancellationToken);

    Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken);

    Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken);

    Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken);

    Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken);

    Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken);

    Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken);

    Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(ProgramInfo program, CancellationToken cancellationToken);

    Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken);

    Task<string> GetRecordingProfileUuidAsync(string profileName, CancellationToken cancellationToken);
}

#pragma warning restore CS1591
