using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service;
using Jellyfin.Plugin.TvHeadendApi.Service.Dvr;
using Jellyfin.Plugin.TvHeadendApi.Service.Guide;
using Jellyfin.Plugin.TvHeadendApi.Service.Streaming;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class LiveTvServiceTests
{
    [Fact]
    public void Constructor_WithNullGuideService_Throws()
    {
        var dvr = new Mock<ITvheadendDvrService>();
        var source = new Mock<ILiveStreamSourceService>();
        var lifecycle = new Mock<ILiveStreamLifecycleService>();

        Assert.Throws<ArgumentNullException>(() => new LiveTvService(null!, dvr.Object, source.Object, lifecycle.Object));
    }

    [Fact]
    public void Constructor_WithNullDvrService_Throws()
    {
        var guide = new Mock<ILiveTvGuideService>();
        var source = new Mock<ILiveStreamSourceService>();
        var lifecycle = new Mock<ILiveStreamLifecycleService>();

        Assert.Throws<ArgumentNullException>(() => new LiveTvService(guide.Object, null!, source.Object, lifecycle.Object));
    }

    [Fact]
    public async Task GetChannelsAsync_DelegatesToGuideService()
    {
        var expected = new List<ChannelInfo> { new() { Name = "One" } };
        var guide = new Mock<ILiveTvGuideService>();
        var dvr = new Mock<ITvheadendDvrService>();
        var source = new Mock<ILiveStreamSourceService>();
        var lifecycle = new Mock<ILiveStreamLifecycleService>();
        guide.Setup(x => x.GetChannelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var sut = new LiveTvService(guide.Object, dvr.Object, source.Object, lifecycle.Object);
        var result = await sut.GetChannelsAsync(CancellationToken.None);

        Assert.Same(expected, result);
        guide.Verify(x => x.GetChannelsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetProgramsAsync_DelegatesToGuideServiceWithParameters()
    {
        var expected = new List<ProgramInfo> { new() { Name = "Program" } };
        var guide = new Mock<ILiveTvGuideService>();
        var dvr = new Mock<ITvheadendDvrService>();
        var source = new Mock<ILiveStreamSourceService>();
        var lifecycle = new Mock<ILiveStreamLifecycleService>();
        guide.Setup(x => x.GetProgramsAsync("ch-1", It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var sut = new LiveTvService(guide.Object, dvr.Object, source.Object, lifecycle.Object);
        var result = await sut.GetProgramsAsync("ch-1", DateTime.UtcNow, DateTime.UtcNow.AddHours(1), CancellationToken.None);

        Assert.Same(expected, result);
        guide.Verify(x => x.GetProgramsAsync("ch-1", It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetChannelStream_DelegatesToStreamSourceService_AndIgnoresStreamId()
    {
        var expected = new MediaSourceInfo { Id = "ch-1", Path = "http://example/stream" };
        var guide = new Mock<ILiveTvGuideService>();
        var dvr = new Mock<ITvheadendDvrService>();
        var source = new Mock<ILiveStreamSourceService>();
        var lifecycle = new Mock<ILiveStreamLifecycleService>();
        source.Setup(x => x.GetChannelStreamAsync("ch-1", It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var sut = new LiveTvService(guide.Object, dvr.Object, source.Object, lifecycle.Object);
        var result = await sut.GetChannelStream("ch-1", "ignored-stream-id", CancellationToken.None);

        Assert.Same(expected, result);
        source.Verify(x => x.GetChannelStreamAsync("ch-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelTimerAsync_DelegatesToDvrService()
    {
        var guide = new Mock<ILiveTvGuideService>();
        var dvr = new Mock<ITvheadendDvrService>();
        var source = new Mock<ILiveStreamSourceService>();
        var lifecycle = new Mock<ILiveStreamLifecycleService>();

        var sut = new LiveTvService(guide.Object, dvr.Object, source.Object, lifecycle.Object);
        await sut.CancelTimerAsync("timer-1", CancellationToken.None);

        dvr.Verify(x => x.CancelTimerAsync("timer-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CloseLiveStream_DelegatesToLifecycleService()
    {
        var guide = new Mock<ILiveTvGuideService>();
        var dvr = new Mock<ITvheadendDvrService>();
        var source = new Mock<ILiveStreamSourceService>();
        var lifecycle = new Mock<ILiveStreamLifecycleService>();

        var sut = new LiveTvService(guide.Object, dvr.Object, source.Object, lifecycle.Object);
        await sut.CloseLiveStream("live-1", CancellationToken.None);

        lifecycle.Verify(x => x.CloseLiveStreamAsync("live-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Name_ReturnsExpectedProviderName()
    {
        var sut = new LiveTvService(
            new Mock<ILiveTvGuideService>().Object,
            new Mock<ITvheadendDvrService>().Object,
            new Mock<ILiveStreamSourceService>().Object,
            new Mock<ILiveStreamLifecycleService>().Object);

        Assert.Equal("TvHeadendApi", sut.Name);
    }

    [Fact]
    public void HomePageUrl_ReturnsExpectedUrl()
    {
        var sut = new LiveTvService(
            new Mock<ILiveTvGuideService>().Object,
            new Mock<ITvheadendDvrService>().Object,
            new Mock<ILiveStreamSourceService>().Object,
            new Mock<ILiveStreamLifecycleService>().Object);

        Assert.Equal("https://tvheadend.org", sut.HomePageUrl);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var sut = new LiveTvService(
            new Mock<ILiveTvGuideService>().Object,
            new Mock<ITvheadendDvrService>().Object,
            new Mock<ILiveStreamSourceService>().Object,
            new Mock<ILiveStreamLifecycleService>().Object);

        sut.Dispose();
    }

    [Fact]
    public async Task CreateTimerAsync_DelegatesToDvrService()
    {
        var info = new TimerInfo();
        var guide = new Mock<ILiveTvGuideService>();
        var dvr = new Mock<ITvheadendDvrService>();
        var source = new Mock<ILiveStreamSourceService>();
        var lifecycle = new Mock<ILiveStreamLifecycleService>();
        var sut = new LiveTvService(guide.Object, dvr.Object, source.Object, lifecycle.Object);

        await sut.CreateTimerAsync(info, CancellationToken.None);

        dvr.Verify(x => x.CreateTimerAsync(info, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetTimersAsync_DelegatesToDvrService()
    {
        var expected = new List<TimerInfo> { new() };
        var guide = new Mock<ILiveTvGuideService>();
        var dvr = new Mock<ITvheadendDvrService>();
        var source = new Mock<ILiveStreamSourceService>();
        var lifecycle = new Mock<ILiveStreamLifecycleService>();
        dvr.Setup(x => x.GetTimersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var sut = new LiveTvService(guide.Object, dvr.Object, source.Object, lifecycle.Object);

        var result = await sut.GetTimersAsync(CancellationToken.None);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task GetSeriesTimersAsync_DelegatesToDvrService()
    {
        var expected = new List<SeriesTimerInfo> { new() };
        var guide = new Mock<ILiveTvGuideService>();
        var dvr = new Mock<ITvheadendDvrService>();
        var source = new Mock<ILiveStreamSourceService>();
        var lifecycle = new Mock<ILiveStreamLifecycleService>();
        dvr.Setup(x => x.GetSeriesTimersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var sut = new LiveTvService(guide.Object, dvr.Object, source.Object, lifecycle.Object);

        var result = await sut.GetSeriesTimersAsync(CancellationToken.None);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task GetChannelStreamMediaSources_DelegatesToSourceService()
    {
        var expected = new List<MediaSourceInfo> { new() };
        var guide = new Mock<ILiveTvGuideService>();
        var dvr = new Mock<ITvheadendDvrService>();
        var source = new Mock<ILiveStreamSourceService>();
        var lifecycle = new Mock<ILiveStreamLifecycleService>();
        source.Setup(x => x.GetChannelStreamMediaSourcesAsync("ch-2", It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var sut = new LiveTvService(guide.Object, dvr.Object, source.Object, lifecycle.Object);

        var result = await sut.GetChannelStreamMediaSources("ch-2", CancellationToken.None);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task ResetTuner_DelegatesToLifecycleService()
    {
        var guide = new Mock<ILiveTvGuideService>();
        var dvr = new Mock<ITvheadendDvrService>();
        var source = new Mock<ILiveStreamSourceService>();
        var lifecycle = new Mock<ILiveStreamLifecycleService>();
        var sut = new LiveTvService(guide.Object, dvr.Object, source.Object, lifecycle.Object);

        await sut.ResetTuner("tuner-1", CancellationToken.None);

        lifecycle.Verify(x => x.ResetTunerAsync("tuner-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelSeriesTimerAsync_DelegatesToDvrService()
    {
        var guide = new Mock<ILiveTvGuideService>();
        var dvr = new Mock<ITvheadendDvrService>();
        var source = new Mock<ILiveStreamSourceService>();
        var lifecycle = new Mock<ILiveStreamLifecycleService>();
        var sut = new LiveTvService(guide.Object, dvr.Object, source.Object, lifecycle.Object);

        await sut.CancelSeriesTimerAsync("series-1", CancellationToken.None);

        dvr.Verify(x => x.CancelSeriesTimerAsync("series-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateSeriesTimerAsync_DelegatesToDvrService()
    {
        var info = new SeriesTimerInfo();
        var guide = new Mock<ILiveTvGuideService>();
        var dvr = new Mock<ITvheadendDvrService>();
        var source = new Mock<ILiveStreamSourceService>();
        var lifecycle = new Mock<ILiveStreamLifecycleService>();
        var sut = new LiveTvService(guide.Object, dvr.Object, source.Object, lifecycle.Object);

        await sut.CreateSeriesTimerAsync(info, CancellationToken.None);

        dvr.Verify(x => x.CreateSeriesTimerAsync(info, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateTimerAsync_DelegatesToDvrService()
    {
        var info = new TimerInfo();
        var guide = new Mock<ILiveTvGuideService>();
        var dvr = new Mock<ITvheadendDvrService>();
        var source = new Mock<ILiveStreamSourceService>();
        var lifecycle = new Mock<ILiveStreamLifecycleService>();
        var sut = new LiveTvService(guide.Object, dvr.Object, source.Object, lifecycle.Object);

        await sut.UpdateTimerAsync(info, CancellationToken.None);

        dvr.Verify(x => x.UpdateTimerAsync(info, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateSeriesTimerAsync_DelegatesToDvrService()
    {
        var info = new SeriesTimerInfo();
        var guide = new Mock<ILiveTvGuideService>();
        var dvr = new Mock<ITvheadendDvrService>();
        var source = new Mock<ILiveStreamSourceService>();
        var lifecycle = new Mock<ILiveStreamLifecycleService>();
        var sut = new LiveTvService(guide.Object, dvr.Object, source.Object, lifecycle.Object);

        await sut.UpdateSeriesTimerAsync(info, CancellationToken.None);

        dvr.Verify(x => x.UpdateSeriesTimerAsync(info, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetNewTimerDefaultsAsync_DelegatesToDvrService()
    {
        var expected = new SeriesTimerInfo();
        var program = new ProgramInfo();
        var guide = new Mock<ILiveTvGuideService>();
        var dvr = new Mock<ITvheadendDvrService>();
        var source = new Mock<ILiveStreamSourceService>();
        var lifecycle = new Mock<ILiveStreamLifecycleService>();
        dvr.Setup(x => x.GetNewTimerDefaultsAsync(program, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var sut = new LiveTvService(guide.Object, dvr.Object, source.Object, lifecycle.Object);

        var result = await sut.GetNewTimerDefaultsAsync(CancellationToken.None, program);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task GetContentTypesAsync_DelegatesToGuideService()
    {
        var expected = new Dictionary<int, string> { [16] = "Movie/Drama" };
        var guide = new Mock<ILiveTvGuideService>();
        var dvr = new Mock<ITvheadendDvrService>();
        var source = new Mock<ILiveStreamSourceService>();
        var lifecycle = new Mock<ILiveStreamLifecycleService>();
        guide.Setup(x => x.GetContentTypesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var sut = new LiveTvService(guide.Object, dvr.Object, source.Object, lifecycle.Object);

        var result = await sut.GetContentTypesAsync(CancellationToken.None);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task GetChannelTagsAsync_DelegatesToGuideService()
    {
        var expected = new Dictionary<string, string> { ["tag"] = "News" };
        var guide = new Mock<ILiveTvGuideService>();
        var dvr = new Mock<ITvheadendDvrService>();
        var source = new Mock<ILiveStreamSourceService>();
        var lifecycle = new Mock<ILiveStreamLifecycleService>();
        guide.Setup(x => x.GetChannelTagsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var sut = new LiveTvService(guide.Object, dvr.Object, source.Object, lifecycle.Object);

        var result = await sut.GetChannelTagsAsync(CancellationToken.None);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task GetRecordingProfileUuidAsync_DelegatesToDvrService()
    {
        var guide = new Mock<ILiveTvGuideService>();
        var dvr = new Mock<ITvheadendDvrService>();
        var source = new Mock<ILiveStreamSourceService>();
        var lifecycle = new Mock<ILiveStreamLifecycleService>();
        dvr.Setup(x => x.GetRecordingProfileUuidAsync("default", It.IsAny<CancellationToken>())).ReturnsAsync("uuid-123");
        var sut = new LiveTvService(guide.Object, dvr.Object, source.Object, lifecycle.Object);

        var result = await sut.GetRecordingProfileUuidAsync("default", CancellationToken.None);

        Assert.Equal("uuid-123", result);
    }
}
