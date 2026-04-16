using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.Dvr;
using Jellyfin.Plugin.TvHeadendApi.Service.Guide;
using Jellyfin.Plugin.TvHeadendApi.Service.Stream;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;

namespace Jellyfin.Plugin.TvHeadendApi.Service;

/// <summary>
/// Orchestrates LiveTV operations by delegating to focused services.
/// </summary>
public sealed class OrchestratorService : ILiveTvService, ISupportsNewTimerIds, IDisposable
{
    private readonly IGuideService _guideService;
    private readonly IDvrService _dvrService;
    private readonly IMediaSourceService _streamMediaSourceService;
    private readonly ILifecycleService _streamLifecycleService;

    /// <summary>
    /// Initializes a new instance of the <see cref="OrchestratorService"/> class.
    /// </summary>
    /// <param name="guideService">Guide/EPG service.</param>
    /// <param name="dvrService">DVR/timer service.</param>
    /// <param name="streamMediaSourceService">Stream media source service.</param>
    /// <param name="streamLifecycleService">Stream lifecycle service.</param>
    public OrchestratorService(
        IGuideService guideService,
        IDvrService dvrService,
        IMediaSourceService streamMediaSourceService,
        ILifecycleService streamLifecycleService)
    {
        _guideService = guideService ?? throw new ArgumentNullException(nameof(guideService));
        _dvrService = dvrService ?? throw new ArgumentNullException(nameof(dvrService));
        _streamMediaSourceService = streamMediaSourceService ?? throw new ArgumentNullException(nameof(streamMediaSourceService));
        _streamLifecycleService = streamLifecycleService ?? throw new ArgumentNullException(nameof(streamLifecycleService));
    }

    /// <inheritdoc />
    public string Name => "TvHeadendApi";

    /// <inheritdoc />
    public string HomePageUrl => "https://tvheadend.org";

    /// <inheritdoc />
    public void Dispose()
    {
    }

    /// <inheritdoc />
    public Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
        => _guideService.GetChannelsAsync(cancellationToken);

    /// <inheritdoc />
    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
        => _dvrService.CancelTimerAsync(timerId, cancellationToken);

    /// <inheritdoc />
    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
        => _dvrService.CancelSeriesTimerAsync(timerId, cancellationToken);

    /// <inheritdoc />
    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
        => _dvrService.CreateTimerAsync(info, cancellationToken);

    /// <inheritdoc />
    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        => _dvrService.CreateSeriesTimerAsync(info, cancellationToken);

    /// <inheritdoc />
    public Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken)
        => _dvrService.UpdateTimerAsync(updatedTimer, cancellationToken);

    /// <inheritdoc />
    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        => _dvrService.UpdateSeriesTimerAsync(info, cancellationToken);

    /// <inheritdoc />
    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
        => _dvrService.GetTimersAsync(cancellationToken);

    /// <inheritdoc />
    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo program)
        => _dvrService.GetNewTimerDefaultsAsync(program, cancellationToken);

    /// <inheritdoc />
    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
        => _dvrService.GetSeriesTimersAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
        => _guideService.GetProgramsAsync(channelId, startDateUtc, endDateUtc, cancellationToken);

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
        => _streamMediaSourceService.GetChannelStreamAsync(channelId, cancellationToken);

    /// <inheritdoc />
    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
        => _streamMediaSourceService.GetChannelStreamMediaSourcesAsync(channelId, cancellationToken);

    /// <inheritdoc />
    public Task CloseLiveStream(string id, CancellationToken cancellationToken)
        => _streamLifecycleService.CloseLiveStreamAsync(id, cancellationToken);

    /// <inheritdoc />
    public Task ResetTuner(string id, CancellationToken cancellationToken)
        => _streamLifecycleService.ResetTunerAsync(id, cancellationToken);

    /// <summary>
    /// Gets the EPG content type mapping from TVHeadend.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Content type dictionary.</returns>
    public Task<Dictionary<int, string>> GetContentTypesAsync(CancellationToken cancellationToken)
        => _guideService.GetContentTypesAsync(cancellationToken);

    /// <summary>
    /// Gets the channel tag mapping from TVHeadend.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Channel tag dictionary.</returns>
    public Task<Dictionary<string, string>> GetChannelTagsAsync(CancellationToken cancellationToken)
        => _guideService.GetChannelTagsAsync(cancellationToken);

    /// <summary>
    /// Gets the UUID of a DVR recording profile by name.
    /// </summary>
    /// <param name="profileName">Profile name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The profile UUID.</returns>
    public Task<string> GetRecordingProfileUuidAsync(string profileName, CancellationToken cancellationToken)
        => _dvrService.GetRecordingProfileUuidAsync(profileName, cancellationToken);

    /// <summary>
    /// Creates a single timer and returns its ID.
    /// </summary>
    /// <param name="info">Timer information.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The timer ID.</returns>
    public async Task<string> CreateTimer(TimerInfo info, CancellationToken cancellationToken)
    {
        await _dvrService.CreateTimerAsync(info, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(info.Id) ? Guid.NewGuid().ToString("N") : info.Id;
    }

    /// <summary>
    /// Creates a series timer and returns its ID.
    /// </summary>
    /// <param name="info">Series timer information.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The series timer ID.</returns>
    public async Task<string> CreateSeriesTimer(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        await _dvrService.CreateSeriesTimerAsync(info, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(info.Id) ? Guid.NewGuid().ToString("N") : info.Id;
    }
}
