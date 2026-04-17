using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Stream;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class MediaSourceServiceTests
{
    private static readonly Guid FixedInternalChannelId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Constructor_WithNullLogger_Throws()
    {
        var library = new Mock<ILibraryManager>();
        var resolver = new Mock<IProfileContainerResolver>();
        var api = new Mock<IApiClient>();
        var urlBuilder = new UrlBuilder();

        Assert.Throws<ArgumentNullException>(() => new MediaSourceService(null!, library.Object, resolver.Object, api.Object, urlBuilder, () => null));
    }

    [Fact]
    public async Task GetChannelStreamAsync_WithMissingConfig_ThrowsInvalidOperationException()
    {
        var library = new Mock<ILibraryManager>();
        var resolver = new Mock<IProfileContainerResolver>();
        var api = new Mock<IApiClient>();
        var urlBuilder = new UrlBuilder();
        api.Setup(x => x.GetCurrentConfiguration()).Returns((PluginConfiguration?)null);

        var sut = new MediaSourceService(NullLogger<MediaSourceService>.Instance, library.Object, resolver.Object, api.Object, urlBuilder, () => null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.GetChannelStreamAsync("ch-1", CancellationToken.None));
    }

    [Fact]
    public async Task GetChannelStreamAsync_WithEmptyChannelId_ThrowsArgumentException()
    {
        var library = new Mock<ILibraryManager>();
        var resolver = new Mock<IProfileContainerResolver>();
        var api = new Mock<IApiClient>();
        var urlBuilder = new UrlBuilder();
        api.Setup(x => x.GetCurrentConfiguration()).Returns(new PluginConfiguration());

        var sut = new MediaSourceService(NullLogger<MediaSourceService>.Instance, library.Object, resolver.Object, api.Object, urlBuilder, () => null);

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
        var resolver = new Mock<IProfileContainerResolver>();
        var api = new Mock<IApiClient>();
        var urlBuilder = new UrlBuilder();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        resolver.Setup(x => x.ResolveContainerAsync(config, It.IsAny<CancellationToken>())).ReturnsAsync("mpegts");
        resolver.Setup(x => x.ResolveProfileSnapshotAsync(config, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileSnapshot("pass", "uuid", "profile-mpegts", "mpegts", string.Empty, string.Empty, "h264", "aac", null));

        var sut = new MediaSourceService(NullLogger<MediaSourceService>.Instance, library.Object, resolver.Object, api.Object, urlBuilder, () => null);
        var mediaSource = await sut.GetChannelStreamAsync("ch-42", CancellationToken.None);

        Assert.Equal("ch-42", mediaSource.Id);
        Assert.Equal("mpegts", mediaSource.Container);
        Assert.Contains("stream/channel/ch-42?profile=pass", mediaSource.Path);
        Assert.True(mediaSource.SupportsDirectPlay);
        Assert.True(mediaSource.SupportsDirectStream);
    }

    [Fact]
    public async Task GetChannelStreamAsync_PassesAnalyzeDurationFromConfig_WhenValueIsZero()
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
        var resolver = new Mock<IProfileContainerResolver>();
        var api = new Mock<IApiClient>();
        var urlBuilder = new UrlBuilder();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        resolver.Setup(x => x.ResolveContainerAsync(config, It.IsAny<CancellationToken>())).ReturnsAsync("mpegts");
        resolver.Setup(x => x.ResolveProfileSnapshotAsync(config, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileSnapshot("pass", "uuid", "profile-mpegts", "mpegts", string.Empty, string.Empty, "h264", "aac", null));

        var sut = new MediaSourceService(NullLogger<MediaSourceService>.Instance, library.Object, resolver.Object, api.Object, urlBuilder, () => null);
        var mediaSource = await sut.GetChannelStreamAsync("ch-1", CancellationToken.None);

        Assert.Equal(0, mediaSource.AnalyzeDurationMs);
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
        var resolver = new Mock<IProfileContainerResolver>();
        var api = new Mock<IApiClient>();
        var urlBuilder = new UrlBuilder();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        resolver.Setup(x => x.ResolveContainerAsync(config, It.IsAny<CancellationToken>())).ReturnsAsync("mpegts");
        resolver.Setup(x => x.ResolveProfileSnapshotAsync(config, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileSnapshot("pass", "uuid", "profile-mpegts", "mpegts", string.Empty, string.Empty, "h264", "aac", null));

        var sut = new MediaSourceService(NullLogger<MediaSourceService>.Instance, library.Object, resolver.Object, api.Object, urlBuilder, () => null);
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
        var resolver = new Mock<IProfileContainerResolver>();
        var api = new Mock<IApiClient>();
        var urlBuilder = new UrlBuilder();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        resolver.Setup(x => x.ResolveContainerAsync(config, It.IsAny<CancellationToken>())).ReturnsAsync("mpegts");
        resolver.Setup(x => x.ResolveProfileSnapshotAsync(config, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileSnapshot("pass", "uuid", "profile-mpegts", "mpegts", string.Empty, string.Empty, "h264", "aac", null));

        var sut = new MediaSourceService(NullLogger<MediaSourceService>.Instance, library.Object, resolver.Object, api.Object, urlBuilder, () => null);
        var mediaSource = await sut.GetChannelStreamAsync("ch-99", CancellationToken.None);

        Assert.Equal(1234, mediaSource.BufferMs);
    }

    [Fact]
    public async Task GetChannelStreamAsync_EncodesChannelIdAndProfileInPath()
    {
        var config = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9981,
            StreamingProfile = "jelly fin+fast",
            EnableMediaInfoCacheWrite = false,
            AllowAnonymousAccess = true,
        };

        var library = new Mock<ILibraryManager>();
        var resolver = new Mock<IProfileContainerResolver>();
        var api = new Mock<IApiClient>();
        var urlBuilder = new UrlBuilder();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        resolver.Setup(x => x.ResolveContainerAsync(config, It.IsAny<CancellationToken>())).ReturnsAsync("mpegts");
        resolver.Setup(x => x.ResolveProfileSnapshotAsync(config, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileSnapshot("jelly fin+fast", "uuid", "profile-mpegts", "mpegts", string.Empty, string.Empty, "h264", "aac", null));

        var sut = new MediaSourceService(NullLogger<MediaSourceService>.Instance, library.Object, resolver.Object, api.Object, urlBuilder, () => null);
        var mediaSource = await sut.GetChannelStreamAsync("ch/1", CancellationToken.None);

        Assert.Contains("stream/channel/ch%2F1", mediaSource.Path, StringComparison.Ordinal);
        Assert.Contains("profile=jelly%20fin%2Bfast", mediaSource.Path, StringComparison.Ordinal);
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
        var resolver = new Mock<IProfileContainerResolver>();
        var api = new Mock<IApiClient>();
        var urlBuilder = new UrlBuilder();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        resolver.Setup(x => x.ResolveContainerAsync(config, It.IsAny<CancellationToken>())).ReturnsAsync("mpegts");
        resolver.Setup(x => x.ResolveProfileSnapshotAsync(config, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileSnapshot("pass", "uuid", "profile-mpegts", "mpegts", string.Empty, string.Empty, "h264", "aac", null));

        var sut = new MediaSourceService(NullLogger<MediaSourceService>.Instance, library.Object, resolver.Object, api.Object, urlBuilder, () => null);
        var mediaSource = await sut.GetChannelStreamAsync("ch-cache", CancellationToken.None);

        Assert.NotNull(mediaSource);
        Assert.Equal("mpegts", mediaSource.Container);
    }

    [Fact]
    public async Task GetChannelStreamAsync_WithCacheWriteEnabled_WritesExpectedCacheFile()
    {
        var config = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9981,
            StreamingProfile = "jellyfin",
            EnableMediaInfoCacheWrite = true,
            EnableMediaInfoCacheValidation = true,
            AllowAnonymousAccess = true,
        };

        var snapshot = new ProfileSnapshot("jellyfin", "uuid", "profile-mp4", "mp4", string.Empty, string.Empty, "h264", "aac", null);
        using var cacheDir = new TempDirectory();
        var sut = CreateSut(config, snapshot, cacheDir.Path);

        await sut.GetChannelStreamAsync("ch-cache", CancellationToken.None);

        var cacheJson = await ReadSingleCacheFileAsync(cacheDir.Path);
        using var doc = JsonDocument.Parse(cacheJson);
        Assert.Equal("mp4", doc.RootElement.GetProperty("Container").GetString());
        Assert.True(doc.RootElement.GetProperty("IsInfiniteStream").GetBoolean());
        Assert.Equal("jellyfin", MediaSourceService.ExtractQueryParameter(doc.RootElement.GetProperty("Path").GetString(), "profile"));
        Assert.Equal("h264", MediaSourceService.ExtractCodecFromMediaStreams(doc.RootElement, "Video"));
        Assert.Equal("aac", MediaSourceService.ExtractCodecFromMediaStreams(doc.RootElement, "Audio"));
    }

    [Fact]
    public async Task GetChannelStreamAsync_WithMalformedExistingCache_RewritesCacheFile()
    {
        var config = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9981,
            StreamingProfile = "jellyfin",
            EnableMediaInfoCacheWrite = true,
            EnableMediaInfoCacheValidation = true,
            AllowAnonymousAccess = true,
        };

        var snapshot = new ProfileSnapshot("jellyfin", "uuid", "profile-mp4", "mp4", string.Empty, string.Empty, "h264", "aac", null);
        using var cacheDir = new TempDirectory();
        var cacheFilePath = GetCacheFilePath(cacheDir.Path, "ch-bad");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
        await File.WriteAllTextAsync(cacheFilePath, "{not-json");

        var sut = CreateSut(config, snapshot, cacheDir.Path);
        await sut.GetChannelStreamAsync("ch-bad", CancellationToken.None);

        var rewrittenJson = await File.ReadAllTextAsync(cacheFilePath);
        Assert.True(MediaSourceService.TryParseCacheSnapshot(rewrittenJson, cacheFilePath, out var parsed));
        Assert.Equal("jellyfin", parsed!.ProfileName);
        Assert.Equal("mp4", parsed.Container);
    }

    [Fact]
    public async Task GetChannelStreamAsync_WithMismatchedCacheAndValidationEnabled_RewritesCacheFile()
    {
        var initialConfig = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9981,
            StreamingProfile = "pass",
            EnableMediaInfoCacheWrite = true,
            EnableMediaInfoCacheValidation = true,
            AllowAnonymousAccess = true,
        };

        using var cacheDir = new TempDirectory();
        var initialSnapshot = new ProfileSnapshot("pass", "uuid", "profile-mpegts", "mpegts", string.Empty, string.Empty, "mpeg2video", "mp2", null);
        var rewrittenSnapshot = new ProfileSnapshot("jellyfin", "uuid2", "profile-mp4", "mp4", string.Empty, string.Empty, "h264", "aac", null);

        var initialSut = CreateSut(initialConfig, initialSnapshot, cacheDir.Path);
        await initialSut.GetChannelStreamAsync("ch-rewrite", CancellationToken.None);

        var rewrittenConfig = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9981,
            StreamingProfile = "jellyfin",
            EnableMediaInfoCacheWrite = true,
            EnableMediaInfoCacheValidation = true,
            AllowAnonymousAccess = true,
        };

        var rewrittenSut = CreateSut(rewrittenConfig, rewrittenSnapshot, cacheDir.Path);
        await rewrittenSut.GetChannelStreamAsync("ch-rewrite", CancellationToken.None);

        var rewrittenJson = await ReadSingleCacheFileAsync(cacheDir.Path);
        Assert.True(MediaSourceService.TryParseCacheSnapshot(rewrittenJson, GetCacheFilePath(cacheDir.Path, "ch-rewrite"), out var parsed));
        Assert.Equal("jellyfin", parsed!.ProfileName);
        Assert.Equal("h264", parsed.VideoCodec);
        Assert.Equal("aac", parsed.AudioCodec);
        Assert.Equal("mp4", parsed.Container);
    }

    [Fact]
    public async Task GetChannelStreamAsync_WithValidationDisabled_KeepsExistingCacheFile()
    {
        var initialConfig = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9981,
            StreamingProfile = "pass",
            EnableMediaInfoCacheWrite = true,
            EnableMediaInfoCacheValidation = true,
            AllowAnonymousAccess = true,
        };

        using var cacheDir = new TempDirectory();
        var initialSnapshot = new ProfileSnapshot("pass", "uuid", "profile-mpegts", "mpegts", string.Empty, string.Empty, "mpeg2video", "mp2", null);
        var initialSut = CreateSut(initialConfig, initialSnapshot, cacheDir.Path);
        await initialSut.GetChannelStreamAsync("ch-preserve", CancellationToken.None);

        var rewrittenConfig = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9981,
            StreamingProfile = "jellyfin",
            EnableMediaInfoCacheWrite = true,
            EnableMediaInfoCacheValidation = false,
            AllowAnonymousAccess = true,
        };

        var rewrittenSnapshot = new ProfileSnapshot("jellyfin", "uuid2", "profile-mp4", "mp4", string.Empty, string.Empty, "h264", "aac", null);
        var rewrittenSut = CreateSut(rewrittenConfig, rewrittenSnapshot, cacheDir.Path);
        await rewrittenSut.GetChannelStreamAsync("ch-preserve", CancellationToken.None);

        var finalJson = await ReadSingleCacheFileAsync(cacheDir.Path);
        Assert.True(MediaSourceService.TryParseCacheSnapshot(finalJson, GetCacheFilePath(cacheDir.Path, "ch-preserve"), out var parsed));
        Assert.Equal("pass", parsed!.ProfileName);
        Assert.Equal("mpegts", parsed.Container);
    }

    [Fact]
    public void ExtractQueryParameter_WithProfileInUrl_ReturnsExpectedValue()
    {
        var result = MediaSourceService.ExtractQueryParameter("http://tvh.local:9981/stream/channel/ch-1?profile=Pass&ticket=abc", "profile");
        Assert.Equal("Pass", result);
    }

    [Fact]
    public void ExtractCodecFromMediaStreams_WithVideoAndAudioEntries_ReturnsExpectedCodec()
    {
        using var doc = JsonDocument.Parse("{\"MediaStreams\":[{\"Type\":\"Video\",\"Codec\":\"h264\"},{\"Type\":\"Audio\",\"Codec\":\"aac\"}]}");
        var root = doc.RootElement;

        var video = MediaSourceService.ExtractCodecFromMediaStreams(root, "Video");
        var audio = MediaSourceService.ExtractCodecFromMediaStreams(root, "Audio");

        Assert.Equal("h264", video);
        Assert.Equal("aac", audio);
    }

    [Fact]
    public void TryParseCacheSnapshot_WithMalformedJson_ReturnsFalse()
    {
        var result = MediaSourceService.TryParseCacheSnapshot("{bad-json", "cache.json", out var snapshot);
        Assert.False(result);
        Assert.Null(snapshot);
    }

    // ── GetRecordingStreamUrl ──────────────────────────────────────────

    [Fact]
    public void GetRecordingStreamUrl_WhenConfigMissing_ReturnsNull()
    {
        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns((PluginConfiguration?)null);
        var sut = new MediaSourceService(
            NullLogger<MediaSourceService>.Instance,
            new Mock<ILibraryManager>().Object,
            new Mock<IProfileContainerResolver>().Object,
            api.Object,
            new UrlBuilder(),
            () => null);

        var result = sut.GetRecordingStreamUrl("recording-uuid");

        Assert.Null(result);
    }

    [Fact]
    public void GetRecordingStreamUrl_WithAuthToken_ReturnsUrlWithAuth()
    {
        var config = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9981,
            UseSSL = false,
            Webroot = "/",
            AuthToken = "mytoken123"
        };
        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.GetBaseUrl(config)).Returns("http://tvh.local:9981");
        api.Setup(x => x.GetWebRoot(config)).Returns("/");
        var sut = new MediaSourceService(
            NullLogger<MediaSourceService>.Instance,
            new Mock<ILibraryManager>().Object,
            new Mock<IProfileContainerResolver>().Object,
            api.Object,
            new UrlBuilder(),
            () => null);

        var result = sut.GetRecordingStreamUrl("rec-abc");

        Assert.Equal("http://tvh.local:9981/dvrfile/rec-abc?auth=mytoken123", result);
    }

    [Fact]
    public void GetRecordingStreamUrl_WithoutAuthToken_ReturnsUrlWithoutAuth()
    {
        var config = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9981,
            UseSSL = false,
            Webroot = "/",
            AuthToken = ""
        };
        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.GetBaseUrl(config)).Returns("http://tvh.local:9981");
        api.Setup(x => x.GetWebRoot(config)).Returns("/");
        var sut = new MediaSourceService(
            NullLogger<MediaSourceService>.Instance,
            new Mock<ILibraryManager>().Object,
            new Mock<IProfileContainerResolver>().Object,
            api.Object,
            new UrlBuilder(),
            () => null);

        var result = sut.GetRecordingStreamUrl("rec-def");

        Assert.Equal("http://tvh.local:9981/dvrfile/rec-def", result);
    }

    private static MediaSourceService CreateSut(PluginConfiguration config, ProfileSnapshot snapshot, string cachePath)
    {
        var library = new Mock<ILibraryManager>();
        library.Setup(x => x.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>())).Returns(FixedInternalChannelId);

        var resolver = new Mock<IProfileContainerResolver>();
        resolver.Setup(x => x.ResolveContainerAsync(config, It.IsAny<CancellationToken>())).ReturnsAsync(snapshot.Container);
        resolver.Setup(x => x.ResolveProfileSnapshotAsync(config, It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);

        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);

        return new MediaSourceService(NullLogger<MediaSourceService>.Instance, library.Object, resolver.Object, api.Object, new UrlBuilder(), () => cachePath);
    }

    private static string GetCacheFilePath(string cachePath, string channelId)
    {
        var fileName = MediaSourceService.BuildMediainfoCacheFileName(
            "Jellyfin.LiveTv.LiveTvMediaSourceProvider",
            "LiveTvChannel",
            FixedInternalChannelId.ToString("N"),
            channelId);
        return Path.Combine(cachePath, "mediainfo", fileName);
    }

    private static async Task<string> ReadSingleCacheFileAsync(string cachePath)
    {
        var files = Directory.GetFiles(Path.Combine(cachePath, "mediainfo"), "*.json");
        Assert.Single(files);
        return await File.ReadAllTextAsync(files[0]);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tvhapi-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
