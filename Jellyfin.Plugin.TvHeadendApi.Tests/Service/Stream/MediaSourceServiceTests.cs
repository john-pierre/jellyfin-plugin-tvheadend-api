using System;
using System.Collections.Generic;
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
    public void ExtractQueryParameter_WithRelativeUrl_ReturnsNull()
    {
        var result = MediaSourceService.ExtractQueryParameter("/stream/channel/ch-1?profile=Pass", "profile");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractQueryParameter_WithNullUrl_ReturnsNull()
    {
        Assert.Null(MediaSourceService.ExtractQueryParameter(null, "profile"));
    }

    [Fact]
    public void ExtractQueryParameter_WithEmptyParameterName_ReturnsNull()
    {
        Assert.Null(MediaSourceService.ExtractQueryParameter("http://tvh:9981/stream?profile=pass", ""));
    }

    [Fact]
    public void ExtractQueryParameter_WithNoQueryString_ReturnsNull()
    {
        Assert.Null(MediaSourceService.ExtractQueryParameter("http://tvh:9981/stream/channel/ch-1", "profile"));
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
    public void ExtractCodecFromMediaStreams_WithNoVideoStream_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("{\"MediaStreams\":[{\"Type\":\"Audio\",\"Codec\":\"aac\"}]}");
        var result = MediaSourceService.ExtractCodecFromMediaStreams(doc.RootElement, "Video");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractCodecFromMediaStreams_WithNoMediaStreamsProperty_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("{}");
        var result = MediaSourceService.ExtractCodecFromMediaStreams(doc.RootElement, "Video");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractCodecFromMediaStreams_WithNonArrayMediaStreams_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("{\"MediaStreams\":\"not-an-array\"}");
        var result = MediaSourceService.ExtractCodecFromMediaStreams(doc.RootElement, "Video");
        Assert.Null(result);
    }

    [Theory]
    [InlineData(null, "mpegts")]
    [InlineData("", "mpegts")]
    [InlineData("  ", "mpegts")]
    [InlineData("mp4", "mp4")]
    [InlineData("matroska", "matroska")]
    public void NormalizeContainerForCache_ReturnsExpected(string? input, string expected)
    {
        Assert.Equal(expected, MediaSourceService.NormalizeContainerForCache(input));
    }

    [Fact]
    public void TryParseCacheSnapshot_WithMalformedJson_ReturnsFalse()
    {
        var result = MediaSourceService.TryParseCacheSnapshot("{bad-json", "cache.json", out var snapshot);
        Assert.False(result);
        Assert.Null(snapshot);
    }

    [Fact]
    public void TryParseCacheSnapshot_WithEmptyString_ReturnsFalse()
    {
        Assert.False(MediaSourceService.TryParseCacheSnapshot("", "cache.json", out _));
    }

    [Fact]
    public void TryParseCacheSnapshot_WithWhitespace_ReturnsFalse()
    {
        Assert.False(MediaSourceService.TryParseCacheSnapshot("   ", null, out _));
    }

    [Fact]
    public async Task GetChannelStreamAsync_WithMatchingCacheAndValidation_DoesNotRewrite()
    {
        // Cache hit path: cache exists, matches profile snapshot, validation enabled → no rewrite
        var config = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9981,
            StreamingProfile = "pass",
            EnableMediaInfoCacheWrite = true,
            EnableMediaInfoCacheValidation = true,
            AllowAnonymousAccess = true,
        };

        var snapshot = new ProfileSnapshot("pass", "uuid", "profile-mpegts", "mpegts", string.Empty, string.Empty, "h264", "aac", null);
        using var cacheDir = new TempDirectory();

        // Write initial cache
        var initialSut = CreateSut(config, snapshot, cacheDir.Path);
        await initialSut.GetChannelStreamAsync("ch-match", CancellationToken.None);

        var initialJson = await ReadSingleCacheFileAsync(cacheDir.Path);

        // Read again with same snapshot — should not rewrite
        var secondSut = CreateSut(config, snapshot, cacheDir.Path);
        await secondSut.GetChannelStreamAsync("ch-match", CancellationToken.None);

        var secondJson = await ReadSingleCacheFileAsync(cacheDir.Path);
        Assert.Equal(initialJson, secondJson);
    }

    [Fact]
    public async Task GetChannelStreamAsync_MismatchedCacheWithProactiveCacheDisabled_DeletesCacheFile()
    {
        // Stale cache + validation enabled + proactive cache disabled → delete
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
        await initialSut.GetChannelStreamAsync("ch-del", CancellationToken.None);

        // Confirm cache file written
        var cacheFiles = Directory.GetFiles(Path.Combine(cacheDir.Path, "mediainfo"), "*.json");
        Assert.Single(cacheFiles);

        // Now use mismatched snapshot with proactive cache DISABLED, validation enabled → should delete
        var deleteConfig = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9981,
            StreamingProfile = "jellyfin",
            EnableMediaInfoCacheWrite = false,
            EnableMediaInfoCacheValidation = true,
            AllowAnonymousAccess = true,
        };

        var deleteSnapshot = new ProfileSnapshot("jellyfin", "uuid2", "profile-mp4", "mp4", string.Empty, string.Empty, "h264", "aac", null);
        var deleteSut = CreateSut(deleteConfig, deleteSnapshot, cacheDir.Path);
        await deleteSut.GetChannelStreamAsync("ch-del", CancellationToken.None);

        var remainingFiles = Directory.GetFiles(Path.Combine(cacheDir.Path, "mediainfo"), "*.json");
        Assert.Empty(remainingFiles);
    }

    [Fact]
    public void BuildMediaInfoCacheContent_WithNullVideoAndAudioCodec_DefaultsToH264AndAac()
    {
        var snapshot = new ProfileSnapshot("pass", "uuid", "profile-mpegts", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, null);
        var content = MediaSourceService.BuildMediaInfoCacheContent("http://tvh:9981/stream", snapshot);

        Assert.Equal("mpegts", content["Container"]);
        var streams = (Dictionary<string, object?>[])content["MediaStreams"]!;
        Assert.Equal("h264", streams[0]["Codec"]);
        Assert.Equal("aac", streams[1]["Codec"]);
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
