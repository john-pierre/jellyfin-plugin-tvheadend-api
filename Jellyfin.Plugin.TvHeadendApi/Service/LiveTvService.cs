using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.Dvr;
using Jellyfin.Plugin.TvHeadendApi.Service.Guide;
using Jellyfin.Plugin.TvHeadendApi.Service.Streaming;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;

#pragma warning disable CS1591

namespace Jellyfin.Plugin.TvHeadendApi.Service;

/// <summary>
/// Orchestrates LiveTV operations by delegating to focused services.
/// </summary>
public sealed class LiveTvService : ILiveTvService, IDisposable
{
    private readonly ILiveTvGuideService _guideService;
    private readonly ITvheadendDvrService _dvrService;
    private readonly ILiveStreamSourceService _streamSourceService;
    private readonly ILiveStreamLifecycleService _streamLifecycleService;

    public LiveTvService(
        ILiveTvGuideService guideService,
        ITvheadendDvrService dvrService,
        ILiveStreamSourceService streamSourceService,
        ILiveStreamLifecycleService streamLifecycleService)
    {
        _guideService = guideService ?? throw new ArgumentNullException(nameof(guideService));
        _dvrService = dvrService ?? throw new ArgumentNullException(nameof(dvrService));
        _streamSourceService = streamSourceService ?? throw new ArgumentNullException(nameof(streamSourceService));
        _streamLifecycleService = streamLifecycleService ?? throw new ArgumentNullException(nameof(streamLifecycleService));
    }

    public string Name => "TvHeadendApi";

    public string HomePageUrl => "https://tvheadend.org";

    public void Dispose()
    {
    }

    public Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
        => _guideService.GetChannelsAsync(cancellationToken);

    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
        => _dvrService.CancelTimerAsync(timerId, cancellationToken);

    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
        => _dvrService.CancelSeriesTimerAsync(timerId, cancellationToken);

    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
        => _dvrService.CreateTimerAsync(info, cancellationToken);

    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        => _dvrService.CreateSeriesTimerAsync(info, cancellationToken);

    public Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken)
        => _dvrService.UpdateTimerAsync(updatedTimer, cancellationToken);

    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        => _dvrService.UpdateSeriesTimerAsync(info, cancellationToken);

    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
        => _dvrService.GetTimersAsync(cancellationToken);

    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo program)
        => _dvrService.GetNewTimerDefaultsAsync(program, cancellationToken);

    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
        => _dvrService.GetSeriesTimersAsync(cancellationToken);

    public Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
        => _guideService.GetProgramsAsync(channelId, startDateUtc, endDateUtc, cancellationToken);

    public Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
        => _streamSourceService.GetChannelStreamAsync(channelId, cancellationToken);

    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
        => _streamSourceService.GetChannelStreamMediaSourcesAsync(channelId, cancellationToken);

    public Task CloseLiveStream(string id, CancellationToken cancellationToken)
        => _streamLifecycleService.CloseLiveStreamAsync(id, cancellationToken);

    public Task ResetTuner(string id, CancellationToken cancellationToken)
        => _streamLifecycleService.ResetTunerAsync(id, cancellationToken);

    // Public helper methods used by plugin components and diagnostics.
    public Task<Dictionary<int, string>> GetContentTypesAsync(CancellationToken cancellationToken)
        => _guideService.GetContentTypesAsync(cancellationToken);

    public Task<Dictionary<string, string>> GetChannelTagsAsync(CancellationToken cancellationToken)
        => _guideService.GetChannelTagsAsync(cancellationToken);

    public Task<string> GetRecordingProfileUuidAsync(string profileName, CancellationToken cancellationToken)
        => _dvrService.GetRecordingProfileUuidAsync(profileName, cancellationToken);
}

#pragma warning restore CS1591
