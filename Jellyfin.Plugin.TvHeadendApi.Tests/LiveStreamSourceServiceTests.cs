using System;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Streaming;
using Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class LiveStreamSourceServiceTests
{
    [Fact]
    public void Constructor_WithNullLogger_Throws()
    {
        var library = new Mock<ILibraryManager>();
        var resolver = new Mock<ILiveStreamProfileContainerResolver>();
        var api = new Mock<ITvheadendApiClient>();
        var urlBuilder = new TvheadendUrlBuilder();

        Assert.Throws<ArgumentNullException>(() => new LiveStreamSourceService(null!, library.Object, resolver.Object, api.Object, urlBuilder));
    }

    [Fact]
    public async Task GetChannelStreamAsync_WithMissingConfig_ThrowsInvalidOperationException()
    {
        var library = new Mock<ILibraryManager>();
        var resolver = new Mock<ILiveStreamProfileContainerResolver>();
        var api = new Mock<ITvheadendApiClient>();
        var urlBuilder = new TvheadendUrlBuilder();
        api.Setup(x => x.GetCurrentConfiguration()).Returns((PluginConfiguration?)null);

        var sut = new LiveStreamSourceService(NullLogger<LiveStreamSourceService>.Instance, library.Object, resolver.Object, api.Object, urlBuilder);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.GetChannelStreamAsync("ch-1", CancellationToken.None));
    }

    [Fact]
    public async Task GetChannelStreamAsync_WithEmptyChannelId_ThrowsArgumentException()
    {
        var library = new Mock<ILibraryManager>();
        var resolver = new Mock<ILiveStreamProfileContainerResolver>();
        var api = new Mock<ITvheadendApiClient>();
        var urlBuilder = new TvheadendUrlBuilder();
        api.Setup(x => x.GetCurrentConfiguration()).Returns(new PluginConfiguration());

        var sut = new LiveStreamSourceService(NullLogger<LiveStreamSourceService>.Instance, library.Object, resolver.Object, api.Object, urlBuilder);

        await Assert.ThrowsAsync<ArgumentException>(() => sut.GetChannelStreamAsync(string.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task GetChannelStreamAsync_BuildsMediaSourceFromDependencies()
    {
        var config = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9981,
            StreamingProfile = "pass",
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = false,
            IsInfiniteStream = true,
            IgnoreDts = false,
            SupportsProbing = true,
            FallbackMaxStreamingBitrate = 3000000,
            AnalyzeDurationMs = 250,
            EnableMediaInfoCacheWrite = false,
            AllowAnonymousAccess = true,
        };

        var library = new Mock<ILibraryManager>();
        var resolver = new Mock<ILiveStreamProfileContainerResolver>();
        var api = new Mock<ITvheadendApiClient>();
        var urlBuilder = new TvheadendUrlBuilder();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        resolver.Setup(x => x.ResolveContainerAsync(config, It.IsAny<CancellationToken>())).ReturnsAsync("mpegts");
        resolver.Setup(x => x.ResolveProfileSnapshotAsync(config, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LiveStreamProfileSnapshot("pass", "uuid", "profile-mpegts", "mpegts", string.Empty, string.Empty, "h264", "aac", null));

        var sut = new LiveStreamSourceService(NullLogger<LiveStreamSourceService>.Instance, library.Object, resolver.Object, api.Object, urlBuilder);
        var mediaSource = await sut.GetChannelStreamAsync("ch-42", CancellationToken.None);

        Assert.Equal("ch-42", mediaSource.Id);
        Assert.Equal("mpegts", mediaSource.Container);
        Assert.Contains("stream/channel/ch-42?profile=pass", mediaSource.Path);
        Assert.True(mediaSource.SupportsDirectPlay);
        Assert.True(mediaSource.SupportsDirectStream);
    }

    [Fact]
    public async Task GetChannelStreamAsync_UsesAnalyzeDurationFallback200_WhenConfigValueIsZero()
    {
        var config = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9981,
            StreamingProfile = "pass",
            AnalyzeDurationMs = 0,
            EnableMediaInfoCacheWrite = false,
            AllowAnonymousAccess = true,
        };

        var library = new Mock<ILibraryManager>();
        var resolver = new Mock<ILiveStreamProfileContainerResolver>();
        var api = new Mock<ITvheadendApiClient>();
        var urlBuilder = new TvheadendUrlBuilder();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        resolver.Setup(x => x.ResolveContainerAsync(config, It.IsAny<CancellationToken>())).ReturnsAsync("mpegts");
        resolver.Setup(x => x.ResolveProfileSnapshotAsync(config, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LiveStreamProfileSnapshot("pass", "uuid", "profile-mpegts", "mpegts", string.Empty, string.Empty, "h264", "aac", null));

        var sut = new LiveStreamSourceService(NullLogger<LiveStreamSourceService>.Instance, library.Object, resolver.Object, api.Object, urlBuilder);
        var mediaSource = await sut.GetChannelStreamAsync("ch-1", CancellationToken.None);

        Assert.Equal(200, mediaSource.AnalyzeDurationMs);
    }

    [Fact]
    public async Task GetChannelStreamMediaSourcesAsync_ReturnsSingleMediaSourceEntry()
    {
        var config = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9981,
            StreamingProfile = "pass",
            EnableMediaInfoCacheWrite = false,
            AllowAnonymousAccess = true,
        };

        var library = new Mock<ILibraryManager>();
        library.Setup(x => x.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>())).Returns(Guid.NewGuid());
        var resolver = new Mock<ILiveStreamProfileContainerResolver>();
        var api = new Mock<ITvheadendApiClient>();
        var urlBuilder = new TvheadendUrlBuilder();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        resolver.Setup(x => x.ResolveContainerAsync(config, It.IsAny<CancellationToken>())).ReturnsAsync("mpegts");
        resolver.Setup(x => x.ResolveProfileSnapshotAsync(config, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LiveStreamProfileSnapshot("pass", "uuid", "profile-mpegts", "mpegts", string.Empty, string.Empty, "h264", "aac", null));

        var sut = new LiveStreamSourceService(NullLogger<LiveStreamSourceService>.Instance, library.Object, resolver.Object, api.Object, urlBuilder);
        var result = await sut.GetChannelStreamMediaSourcesAsync("ch-2", CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("ch-2", result[0].Id);
    }

    [Fact]
    public async Task GetChannelStreamAsync_WithBufferMs_SetsBuffer()
    {
        var config = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9981,
            StreamingProfile = "pass",
            BufferMs = 1234,
            EnableMediaInfoCacheWrite = false,
            AllowAnonymousAccess = true,
        };

        var library = new Mock<ILibraryManager>();
        var resolver = new Mock<ILiveStreamProfileContainerResolver>();
        var api = new Mock<ITvheadendApiClient>();
        var urlBuilder = new TvheadendUrlBuilder();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        resolver.Setup(x => x.ResolveContainerAsync(config, It.IsAny<CancellationToken>())).ReturnsAsync("mpegts");
        resolver.Setup(x => x.ResolveProfileSnapshotAsync(config, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LiveStreamProfileSnapshot("pass", "uuid", "profile-mpegts", "mpegts", string.Empty, string.Empty, "h264", "aac", null));

        var sut = new LiveStreamSourceService(NullLogger<LiveStreamSourceService>.Instance, library.Object, resolver.Object, api.Object, urlBuilder);
        var mediaSource = await sut.GetChannelStreamAsync("ch-99", CancellationToken.None);

        Assert.Equal(1234, mediaSource.BufferMs);
    }

    [Fact]
    public async Task GetChannelStreamAsync_WithCacheWriteEnabledAndNonMp4_DoesNotThrow()
    {
        var config = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9981,
            StreamingProfile = "pass",
            EnableMediaInfoCacheWrite = true,
            AllowAnonymousAccess = true,
        };

        var library = new Mock<ILibraryManager>();
        var resolver = new Mock<ILiveStreamProfileContainerResolver>();
        var api = new Mock<ITvheadendApiClient>();
        var urlBuilder = new TvheadendUrlBuilder();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        resolver.Setup(x => x.ResolveContainerAsync(config, It.IsAny<CancellationToken>())).ReturnsAsync("mpegts");
        resolver.Setup(x => x.ResolveProfileSnapshotAsync(config, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LiveStreamProfileSnapshot("pass", "uuid", "profile-mpegts", "mpegts", string.Empty, string.Empty, "h264", "aac", null));

        var sut = new LiveStreamSourceService(NullLogger<LiveStreamSourceService>.Instance, library.Object, resolver.Object, api.Object, urlBuilder);
        var mediaSource = await sut.GetChannelStreamAsync("ch-cache", CancellationToken.None);

        Assert.NotNull(mediaSource);
        Assert.Equal("mpegts", mediaSource.Container);
    }

    [Fact]
    public void ExtractQueryParameter_WithProfileInUrl_ReturnsExpectedValue()
    {
        var result = InvokePrivateStatic<string?>("ExtractQueryParameter", "http://tvh.local:9981/stream/channel/ch-1?profile=Pass&ticket=abc", "profile");
        Assert.Equal("Pass", result);
    }

    [Fact]
    public void ExtractCodecFromMediaStreams_WithVideoAndAudioEntries_ReturnsExpectedCodec()
    {
        using var doc = JsonDocument.Parse("{\"MediaStreams\":[{\"Type\":\"Video\",\"Codec\":\"h264\"},{\"Type\":\"Audio\",\"Codec\":\"aac\"}]}");
        var root = doc.RootElement;

        var video = InvokePrivateStatic<string?>("ExtractCodecFromMediaStreams", root, "Video");
        var audio = InvokePrivateStatic<string?>("ExtractCodecFromMediaStreams", root, "Audio");

        Assert.Equal("h264", video);
        Assert.Equal("aac", audio);
    }

    private static T InvokePrivateStatic<T>(string methodName, params object?[] args)
    {
        var method = typeof(LiveStreamSourceService).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, args);
        return Assert.IsType<T>(result);
    }
}
