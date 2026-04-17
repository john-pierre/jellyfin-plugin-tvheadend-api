using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service;
using Jellyfin.Plugin.TvHeadendApi.Service.Dvr;
using Jellyfin.Plugin.TvHeadendApi.Service.Guide;
using Jellyfin.Plugin.TvHeadendApi.Service.Stream;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class OrchestratorServiceTests
{
    [Fact]
    public void Constructor_WithNullGuideService_Throws()
    {
        var dvr = new Mock<IDvrService>();
        var source = new Mock<IMediaSourceService>();
        var lifecycle = new Mock<ILifecycleService>();

        Assert.Throws<ArgumentNullException>(() => new OrchestratorService(null!, dvr.Object, source.Object, lifecycle.Object, NullLogger<OrchestratorService>.Instance));
    }

    [Fact]
    public void Constructor_WithNullDvrService_Throws()
    {
        var guide = new Mock<IGuideService>();
        var source = new Mock<IMediaSourceService>();
        var lifecycle = new Mock<ILifecycleService>();

        Assert.Throws<ArgumentNullException>(() => new OrchestratorService(guide.Object, null!, source.Object, lifecycle.Object, NullLogger<OrchestratorService>.Instance));
    }

    [Fact]
    public async Task GetChannelsAsync_DelegatesToGuideService()
    {
        var expected = new List<ChannelInfo> { new() { Name = "One" } };
        var guide = new Mock<IGuideService>();
        var dvr = new Mock<IDvrService>();
        var source = new Mock<IMediaSourceService>();
        var lifecycle = new Mock<ILifecycleService>();
        guide.Setup(x => x.GetChannelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var sut = new OrchestratorService(guide.Object, dvr.Object, source.Object, lifecycle.Object, NullLogger<OrchestratorService>.Instance);
        var result = await sut.GetChannelsAsync(CancellationToken.None);

        Assert.Same(expected, result);
        guide.Verify(x => x.GetChannelsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetProgramsAsync_DelegatesToGuideServiceWithParameters()
    {
        var expected = new List<ProgramInfo> { new() { Name = "Program" } };
        var guide = new Mock<IGuideService>();
        var dvr = new Mock<IDvrService>();
        var source = new Mock<IMediaSourceService>();
        var lifecycle = new Mock<ILifecycleService>();
        guide.Setup(x => x.GetProgramsAsync("ch-1", It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var sut = new OrchestratorService(guide.Object, dvr.Object, source.Object, lifecycle.Object, NullLogger<OrchestratorService>.Instance);
        var result = await sut.GetProgramsAsync("ch-1", DateTime.UtcNow, DateTime.UtcNow.AddHours(1), CancellationToken.None);

        Assert.Same(expected, result);
        guide.Verify(x => x.GetProgramsAsync("ch-1", It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetChannelStream_DelegatesToStreamMediaSourceService_AndIgnoresStreamId()
    {
        var expected = new MediaSourceInfo { Id = "ch-1", Path = "http://example/stream" };
        var guide = new Mock<IGuideService>();
        var dvr = new Mock<IDvrService>();
        var source = new Mock<IMediaSourceService>();
        var lifecycle = new Mock<ILifecycleService>();
        source.Setup(x => x.GetChannelStreamAsync("ch-1", It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var sut = new OrchestratorService(guide.Object, dvr.Object, source.Object, lifecycle.Object, NullLogger<OrchestratorService>.Instance);
        var result = await sut.GetChannelStream("ch-1", "ignored-stream-id", CancellationToken.None);

        Assert.Same(expected, result);
        source.Verify(x => x.GetChannelStreamAsync("ch-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelTimerAsync_DelegatesToDvrService()
    {
        var guide = new Mock<IGuideService>();
        var dvr = new Mock<IDvrService>();
        var source = new Mock<IMediaSourceService>();
        var lifecycle = new Mock<ILifecycleService>();

        var sut = new OrchestratorService(guide.Object, dvr.Object, source.Object, lifecycle.Object, NullLogger<OrchestratorService>.Instance);
        await sut.CancelTimerAsync("timer-1", CancellationToken.None);

        dvr.Verify(x => x.CancelTimerAsync("timer-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CloseLiveStream_DelegatesToLifecycleService()
    {
        var guide = new Mock<IGuideService>();
        var dvr = new Mock<IDvrService>();
        var source = new Mock<IMediaSourceService>();
        var lifecycle = new Mock<ILifecycleService>();

        var sut = new OrchestratorService(guide.Object, dvr.Object, source.Object, lifecycle.Object, NullLogger<OrchestratorService>.Instance);
        await sut.CloseLiveStream("live-1", CancellationToken.None);

        lifecycle.Verify(x => x.CloseLiveStreamAsync("live-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Name_ReturnsExpectedProviderName()
    {
        var sut = new OrchestratorService(
            new Mock<IGuideService>().Object,
            new Mock<IDvrService>().Object,
            new Mock<IMediaSourceService>().Object,
            new Mock<ILifecycleService>().Object,
            NullLogger<OrchestratorService>.Instance);

        Assert.Equal("TvHeadendApi", sut.Name);
    }

    [Fact]
    public void HomePageUrl_ReturnsExpectedUrl()
    {
        var sut = new OrchestratorService(
            new Mock<IGuideService>().Object,
            new Mock<IDvrService>().Object,
            new Mock<IMediaSourceService>().Object,
            new Mock<ILifecycleService>().Object,
            NullLogger<OrchestratorService>.Instance);

        Assert.Equal("https://tvheadend.org", sut.HomePageUrl);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var sut = new OrchestratorService(
            new Mock<IGuideService>().Object,
            new Mock<IDvrService>().Object,
            new Mock<IMediaSourceService>().Object,
            new Mock<ILifecycleService>().Object,
            NullLogger<OrchestratorService>.Instance);

        sut.Dispose();
    }

    [Fact]
    public async Task CreateTimerAsync_DelegatesToDvrService()
    {
        var info = new TimerInfo();
        var guide = new Mock<IGuideService>();
        var dvr = new Mock<IDvrService>();
        var source = new Mock<IMediaSourceService>();
        var lifecycle = new Mock<ILifecycleService>();
        var sut = new OrchestratorService(guide.Object, dvr.Object, source.Object, lifecycle.Object, NullLogger<OrchestratorService>.Instance);

        await sut.CreateTimerAsync(info, CancellationToken.None);

        dvr.Verify(x => x.CreateTimerAsync(info, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetTimersAsync_DelegatesToDvrService()
    {
        var expected = new List<TimerInfo> { new() };
        var guide = new Mock<IGuideService>();
        var dvr = new Mock<IDvrService>();
        var source = new Mock<IMediaSourceService>();
        var lifecycle = new Mock<ILifecycleService>();
        dvr.Setup(x => x.GetTimersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var sut = new OrchestratorService(guide.Object, dvr.Object, source.Object, lifecycle.Object, NullLogger<OrchestratorService>.Instance);

        var result = await sut.GetTimersAsync(CancellationToken.None);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task GetSeriesTimersAsync_DelegatesToDvrService()
    {
        var expected = new List<SeriesTimerInfo> { new() };
        var guide = new Mock<IGuideService>();
        var dvr = new Mock<IDvrService>();
        var source = new Mock<IMediaSourceService>();
        var lifecycle = new Mock<ILifecycleService>();
        dvr.Setup(x => x.GetSeriesTimersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var sut = new OrchestratorService(guide.Object, dvr.Object, source.Object, lifecycle.Object, NullLogger<OrchestratorService>.Instance);

        var result = await sut.GetSeriesTimersAsync(CancellationToken.None);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task GetChannelStreamMediaSources_DelegatesToMediaSourceService()
    {
        var expected = new List<MediaSourceInfo> { new() };
        var guide = new Mock<IGuideService>();
        var dvr = new Mock<IDvrService>();
        var source = new Mock<IMediaSourceService>();
        var lifecycle = new Mock<ILifecycleService>();
        source.Setup(x => x.GetChannelStreamMediaSourcesAsync("ch-2", It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var sut = new OrchestratorService(guide.Object, dvr.Object, source.Object, lifecycle.Object, NullLogger<OrchestratorService>.Instance);

        var result = await sut.GetChannelStreamMediaSources("ch-2", CancellationToken.None);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task ResetTuner_DelegatesToLifecycleService()
    {
        var guide = new Mock<IGuideService>();
        var dvr = new Mock<IDvrService>();
        var source = new Mock<IMediaSourceService>();
        var lifecycle = new Mock<ILifecycleService>();
        var sut = new OrchestratorService(guide.Object, dvr.Object, source.Object, lifecycle.Object, NullLogger<OrchestratorService>.Instance);

        await sut.ResetTuner("tuner-1", CancellationToken.None);

        lifecycle.Verify(x => x.ResetTunerAsync("tuner-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelSeriesTimerAsync_DelegatesToDvrService()
    {
        var guide = new Mock<IGuideService>();
        var dvr = new Mock<IDvrService>();
        var source = new Mock<IMediaSourceService>();
        var lifecycle = new Mock<ILifecycleService>();
        var sut = new OrchestratorService(guide.Object, dvr.Object, source.Object, lifecycle.Object, NullLogger<OrchestratorService>.Instance);

        await sut.CancelSeriesTimerAsync("series-1", CancellationToken.None);

        dvr.Verify(x => x.CancelSeriesTimerAsync("series-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateSeriesTimerAsync_DelegatesToDvrService()
    {
        var info = new SeriesTimerInfo();
        var guide = new Mock<IGuideService>();
        var dvr = new Mock<IDvrService>();
        var source = new Mock<IMediaSourceService>();
        var lifecycle = new Mock<ILifecycleService>();
        var sut = new OrchestratorService(guide.Object, dvr.Object, source.Object, lifecycle.Object, NullLogger<OrchestratorService>.Instance);

        await sut.CreateSeriesTimerAsync(info, CancellationToken.None);

        dvr.Verify(x => x.CreateSeriesTimerAsync(info, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateTimerAsync_DelegatesToDvrService()
    {
        var info = new TimerInfo();
        var guide = new Mock<IGuideService>();
        var dvr = new Mock<IDvrService>();
        var source = new Mock<IMediaSourceService>();
        var lifecycle = new Mock<ILifecycleService>();
        var sut = new OrchestratorService(guide.Object, dvr.Object, source.Object, lifecycle.Object, NullLogger<OrchestratorService>.Instance);

        await sut.UpdateTimerAsync(info, CancellationToken.None);

        dvr.Verify(x => x.UpdateTimerAsync(info, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateSeriesTimerAsync_DelegatesToDvrService()
    {
        var info = new SeriesTimerInfo();
        var guide = new Mock<IGuideService>();
        var dvr = new Mock<IDvrService>();
        var source = new Mock<IMediaSourceService>();
        var lifecycle = new Mock<ILifecycleService>();
        var sut = new OrchestratorService(guide.Object, dvr.Object, source.Object, lifecycle.Object, NullLogger<OrchestratorService>.Instance);

        await sut.UpdateSeriesTimerAsync(info, CancellationToken.None);

        dvr.Verify(x => x.UpdateSeriesTimerAsync(info, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetNewTimerDefaultsAsync_DelegatesToDvrService()
    {
        var expected = new SeriesTimerInfo();
        var program = new ProgramInfo();
        var guide = new Mock<IGuideService>();
        var dvr = new Mock<IDvrService>();
        var source = new Mock<IMediaSourceService>();
        var lifecycle = new Mock<ILifecycleService>();
        dvr.Setup(x => x.GetNewTimerDefaultsAsync(program, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var sut = new OrchestratorService(guide.Object, dvr.Object, source.Object, lifecycle.Object, NullLogger<OrchestratorService>.Instance);

        var result = await sut.GetNewTimerDefaultsAsync(CancellationToken.None, program);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task GetContentTypesAsync_DelegatesToGuideService()
    {
        var expected = new Dictionary<int, string> { [16] = "Movie/Drama" };
        var guide = new Mock<IGuideService>();
        var dvr = new Mock<IDvrService>();
        var source = new Mock<IMediaSourceService>();
        var lifecycle = new Mock<ILifecycleService>();
        guide.Setup(x => x.GetContentTypesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var sut = new OrchestratorService(guide.Object, dvr.Object, source.Object, lifecycle.Object, NullLogger<OrchestratorService>.Instance);

        var result = await sut.GetContentTypesAsync(CancellationToken.None);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task GetChannelTagsAsync_DelegatesToGuideService()
    {
        var expected = new Dictionary<string, string> { ["tag"] = "News" };
        var guide = new Mock<IGuideService>();
        var dvr = new Mock<IDvrService>();
        var source = new Mock<IMediaSourceService>();
        var lifecycle = new Mock<ILifecycleService>();
        guide.Setup(x => x.GetChannelTagsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var sut = new OrchestratorService(guide.Object, dvr.Object, source.Object, lifecycle.Object, NullLogger<OrchestratorService>.Instance);

        var result = await sut.GetChannelTagsAsync(CancellationToken.None);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task GetRecordingProfileUuidAsync_DelegatesToDvrService()
    {
        var guide = new Mock<IGuideService>();
        var dvr = new Mock<IDvrService>();
        var source = new Mock<IMediaSourceService>();
        var lifecycle = new Mock<ILifecycleService>();
        dvr.Setup(x => x.GetRecordingProfileUuidAsync("default", It.IsAny<CancellationToken>())).ReturnsAsync("uuid-123");
        var sut = new OrchestratorService(guide.Object, dvr.Object, source.Object, lifecycle.Object, NullLogger<OrchestratorService>.Instance);

        var result = await sut.GetRecordingProfileUuidAsync("default", CancellationToken.None);

        Assert.Equal("uuid-123", result);
    }

    [Fact]
    public async Task CreateTimer_WithExistingId_ReturnsThatId()
    {
        var info = new TimerInfo { Id = "existing-id" };
        var guide = new Mock<IGuideService>();
        var dvr = new Mock<IDvrService>();
        var source = new Mock<IMediaSourceService>();
        var lifecycle = new Mock<ILifecycleService>();
        var sut = new OrchestratorService(guide.Object, dvr.Object, source.Object, lifecycle.Object, NullLogger<OrchestratorService>.Instance);

        var result = await sut.CreateTimer(info, CancellationToken.None);

        Assert.Equal("existing-id", result);
        dvr.Verify(x => x.CreateTimerAsync(info, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateTimer_WithNullId_ReturnsGeneratedGuid()
    {
        var info = new TimerInfo();
        var guide = new Mock<IGuideService>();
        var dvr = new Mock<IDvrService>();
        var source = new Mock<IMediaSourceService>();
        var lifecycle = new Mock<ILifecycleService>();
        var sut = new OrchestratorService(guide.Object, dvr.Object, source.Object, lifecycle.Object, NullLogger<OrchestratorService>.Instance);

        var result = await sut.CreateTimer(info, CancellationToken.None);

        Assert.NotEmpty(result);
        Assert.Equal(32, result.Length); // Guid.ToString("N") is 32 chars
    }

    [Fact]
    public async Task CreateTimer_WithWhitespaceId_ReturnsGeneratedGuid()
    {
        var info = new TimerInfo { Id = "   " };
        var sut = new OrchestratorService(
            new Mock<IGuideService>().Object, new Mock<IDvrService>().Object,
            new Mock<IMediaSourceService>().Object, new Mock<ILifecycleService>().Object,
            NullLogger<OrchestratorService>.Instance);

        var result = await sut.CreateTimer(info, CancellationToken.None);

        Assert.Equal(32, result.Length);
    }

    [Fact]
    public async Task CreateSeriesTimer_WithExistingId_ReturnsThatId()
    {
        var info = new SeriesTimerInfo { Id = "series-id" };
        var dvr = new Mock<IDvrService>();
        var sut = new OrchestratorService(
            new Mock<IGuideService>().Object, dvr.Object,
            new Mock<IMediaSourceService>().Object, new Mock<ILifecycleService>().Object,
            NullLogger<OrchestratorService>.Instance);

        var result = await sut.CreateSeriesTimer(info, CancellationToken.None);

        Assert.Equal("series-id", result);
        dvr.Verify(x => x.CreateSeriesTimerAsync(info, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateSeriesTimer_WithNullId_ReturnsGeneratedGuid()
    {
        var info = new SeriesTimerInfo();
        var sut = new OrchestratorService(
            new Mock<IGuideService>().Object, new Mock<IDvrService>().Object,
            new Mock<IMediaSourceService>().Object, new Mock<ILifecycleService>().Object,
            NullLogger<OrchestratorService>.Instance);

        var result = await sut.CreateSeriesTimer(info, CancellationToken.None);

        Assert.NotEmpty(result);
        Assert.Equal(32, result.Length);
    }

    [Fact]
    public void Constructor_WithNullMediaSourceService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new OrchestratorService(
            new Mock<IGuideService>().Object, new Mock<IDvrService>().Object,
            null!, new Mock<ILifecycleService>().Object, NullLogger<OrchestratorService>.Instance));
    }

    [Fact]
    public void Constructor_WithNullLifecycleService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new OrchestratorService(
            new Mock<IGuideService>().Object, new Mock<IDvrService>().Object,
            new Mock<IMediaSourceService>().Object, null!, NullLogger<OrchestratorService>.Instance));
    }

    [Fact]
    public void Constructor_WithNullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new OrchestratorService(
            new Mock<IGuideService>().Object, new Mock<IDvrService>().Object,
            new Mock<IMediaSourceService>().Object, new Mock<ILifecycleService>().Object, null!));
    }
}
