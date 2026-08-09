using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Guide;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Storage;
using Jellyfin.Plugin.TvHeadendApi.Service.Stream;
using Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class MediaInfoCacheServiceTests
{
    [Fact]
    public async Task WarmAllChannelCachesAsync_ProbeWithoutUsableStreams_FallsBackToSyntheticCache()
    {
        using var cacheDir = new TempDirectory();

        // FFprobe "succeeded" but found neither a video nor an audio stream — e.g. the
        // analyze window elapsed before the first keyframe. This must count as a failed
        // probe so the synthetic profile-based cache is written instead.
        var probeResult = new MediaInfo
        {
            Container = "mpegts",
            MediaStreams = new List<MediaStream> { new MediaStream { Type = MediaStreamType.Subtitle, Codec = "dvbsub" } },
        };

        var sut = CreateWarmupService(cacheDir.Path, new[] { new ChannelInfo { Id = "ch-1", Name = "One" } }, probeResult);
        var progress = new CollectingProgress();

        var result = await sut.WarmAllChannelCachesAsync(progress, CancellationToken.None);

        Assert.Equal(1, result.Warmed);
        Assert.Equal(0, result.Failed);

        var terminal = Assert.Single(progress.Reports, r => r.Status == "cached");
        Assert.Equal("Synthetic", terminal.Detail);

        var files = Directory.GetFiles(Path.Combine(cacheDir.Path, "mediainfo"), "*.json");
        var json = await File.ReadAllTextAsync(Assert.Single(files));
        using var doc = JsonDocument.Parse(json);

        // The synthetic fallback always declares a video and an audio stream; the
        // stream-less probe result must never be persisted.
        Assert.Equal("h264", MediaInfoCacheService.ExtractCodecFromMediaStreams(doc.RootElement, "Video"));
        Assert.Equal("aac", MediaInfoCacheService.ExtractCodecFromMediaStreams(doc.RootElement, "Audio"));
    }

    [Fact]
    public async Task WarmAllChannelCachesAsync_ProbeWithAudioOnly_PersistsProbedCache()
    {
        using var cacheDir = new TempDirectory();

        // Audio-only channels (radio) are a legitimate probe outcome and must keep the
        // real FFprobe result rather than falling back to the synthetic content.
        var probeResult = new MediaInfo
        {
            Container = "mpegts",
            MediaStreams = new List<MediaStream> { new MediaStream { Type = MediaStreamType.Audio, Codec = "aac" } },
        };

        var sut = CreateWarmupService(cacheDir.Path, new[] { new ChannelInfo { Id = "ch-radio", Name = "Radio" } }, probeResult);
        var progress = new CollectingProgress();

        var result = await sut.WarmAllChannelCachesAsync(progress, CancellationToken.None);

        Assert.Equal(1, result.Warmed);
        var terminal = Assert.Single(progress.Reports, r => r.Status == "cached");
        Assert.Equal("FFprobe", terminal.Detail);

        var files = Directory.GetFiles(Path.Combine(cacheDir.Path, "mediainfo"), "*.json");
        var json = await File.ReadAllTextAsync(Assert.Single(files));
        using var doc = JsonDocument.Parse(json);

        Assert.Null(MediaInfoCacheService.ExtractCodecFromMediaStreams(doc.RootElement, "Video"));
        Assert.Equal("aac", MediaInfoCacheService.ExtractCodecFromMediaStreams(doc.RootElement, "Audio"));
    }

    [Fact]
    public async Task WarmAllChannelCachesAsync_ProgressReports_UseStablePerChannelPositions()
    {
        using var cacheDir = new TempDirectory();
        var channels = Enumerable.Range(1, 4)
            .Select(i => new ChannelInfo { Id = $"ch-{i}", Name = $"Channel {i}" })
            .ToList();

        // A null probe result forces the synthetic path for every channel; the test
        // only cares about the reported positions.
        var sut = CreateWarmupService(cacheDir.Path, channels, probeResult: null);
        var progress = new CollectingProgress();

        var result = await sut.WarmAllChannelCachesAsync(progress, CancellationToken.None);

        Assert.Equal(4, result.Warmed);
        var reports = progress.Reports;
        var terminal = reports.Where(r => r.Status == "cached").ToList();
        Assert.Equal(4, terminal.Count);

        // Terminal positions must be exactly 1..Total with no duplicates or gaps,
        // even though up to two channels are processed concurrently.
        Assert.Equal(new[] { 1, 2, 3, 4 }, terminal.Select(r => r.Current).OrderBy(c => c).ToArray());

        // Every report of a channel (probing + terminal) must carry the same position.
        foreach (var channelReports in reports.GroupBy(r => r.ChannelId))
        {
            Assert.Single(channelReports.Select(r => r.Current).Distinct());
        }

        Assert.All(reports, r => Assert.Equal(4, r.Total));
    }

    [Fact]
    public async Task EnsureMediaInfoCacheStateAsync_MismatchWithProactiveWritesDisabled_DeletesCacheFile()
    {
        using var cacheDir = new TempDirectory();
        var library = new Mock<ILibraryManager>();
        library.Setup(x => x.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
            .Returns(Guid.Parse("99999999-8888-7777-6666-555555555555"));
        var sut = new MediaInfoCacheService(NullLogger<MediaInfoCacheService>.Instance, library.Object, () => cacheDir.Path);

        var initialSnapshot = new ProfileSnapshot("pass", "uuid", "profile-mpegts", "mpegts", string.Empty, string.Empty, "mpeg2video", "mp2", null);
        await sut.EnsureMediaInfoCacheStateAsync(
            "ch-1",
            "http://tvh:9981/stream/channel/ch-1?profile=pass",
            initialSnapshot,
            proactiveCacheEnabled: true,
            validationEnabled: true,
            CancellationToken.None);
        Assert.Single(Directory.GetFiles(Path.Combine(cacheDir.Path, "mediainfo"), "*.json"));

        // Validation on + proactive writes off: a mismatching cache file must be deleted,
        // not rewritten. The production caller currently always passes matching flags;
        // this locks the documented interface contract for the delete-only branch.
        var mismatchingSnapshot = new ProfileSnapshot("jellyfin", "uuid2", "profile-mp4", "mp4", string.Empty, string.Empty, "h264", "aac", null);
        await sut.EnsureMediaInfoCacheStateAsync(
            "ch-1",
            "http://tvh:9981/stream/channel/ch-1?profile=jellyfin",
            mismatchingSnapshot,
            proactiveCacheEnabled: false,
            validationEnabled: true,
            CancellationToken.None);

        Assert.Empty(Directory.GetFiles(Path.Combine(cacheDir.Path, "mediainfo"), "*.json"));
    }

    [Fact]
    public async Task Ensure_RuleSwitch_PreservesAndRestoresProbedDataViaProfileStore()
    {
        using var cacheDir = new TempDirectory();
        var sut = CreateWarmupService(cacheDir.Path, Array.Empty<ChannelInfo>(), probeResult: null);

        // A PROBED pass-through cache file: real source codecs (mpeg2video/ac3) that no
        // synthetic writer could know.
        var mediaInfoDir = Path.Combine(cacheDir.Path, "mediainfo");
        Directory.CreateDirectory(mediaInfoDir);
        var jellyfinFile = Path.Combine(mediaInfoDir, sut.BuildChannelCacheFileName("ch-1", "ch-1"));
        var probedJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Path"] = "http://jf:8096/api/tvheadend/relay/stream/ch-1?profile=pass&token=OLD",
            ["Container"] = "mpegts",
            ["MediaStreams"] = new[]
            {
                new Dictionary<string, object?> { ["Type"] = "Video", ["Codec"] = "mpeg2video" },
                new Dictionary<string, object?> { ["Type"] = "Audio", ["Codec"] = "ac3" },
            },
        });
        await File.WriteAllTextAsync(jellyfinFile, probedJson);

        // Rule switches this channel to the managed transcode profile: the mismatch rewrite
        // must FIRST preserve the probed pass data in the per-profile store.
        var jellyfinSnapshot = new ProfileSnapshot("jellyfin", "uuid", "profile-transcode", "mpegts", string.Empty, string.Empty, "h264", "aac", null);
        await sut.EnsureMediaInfoCacheStateAsync("ch-1", "http://jf:8096/api/tvheadend/relay/stream/ch-1?profile=jellyfin&token=NEW1", jellyfinSnapshot, true, true, CancellationToken.None);

        var afterSwitch = await File.ReadAllTextAsync(jellyfinFile);
        Assert.Contains("h264", afterSwitch);
        Assert.Contains("profile=jellyfin", afterSwitch);

        var storeDir = Path.Combine(mediaInfoDir, "profiles");
        var passStore = Directory.GetFiles(storeDir, "*.pass.json");
        Assert.Single(passStore);
        Assert.Contains("mpeg2video", await File.ReadAllTextAsync(passStore[0]));

        // Switching back to pass must RESTORE the probed data (not write synthetic h264/720p)
        // and patch the Path to the fresh URL/token.
        var passSnapshot = new ProfileSnapshot("pass", "uuid", "profile-mpegts", "mpegts", string.Empty, string.Empty, string.Empty, string.Empty, null);
        await sut.EnsureMediaInfoCacheStateAsync("ch-1", "http://jf:8096/api/tvheadend/relay/stream/ch-1?profile=pass&token=NEW2", passSnapshot, true, true, CancellationToken.None);

        var restored = await File.ReadAllTextAsync(jellyfinFile);
        Assert.Contains("mpeg2video", restored);
        Assert.Contains("token=NEW2", restored);
        Assert.DoesNotContain("token=OLD", restored);
    }

    [Fact]
    public async Task Ensure_PassThroughProfile_DoesNotFlagProbedCodecsAsMismatch()
    {
        using var cacheDir = new TempDirectory();
        var sut = CreateWarmupService(cacheDir.Path, Array.Empty<ChannelInfo>(), probeResult: null);

        var mediaInfoDir = Path.Combine(cacheDir.Path, "mediainfo");
        Directory.CreateDirectory(mediaInfoDir);
        var jellyfinFile = Path.Combine(mediaInfoDir, sut.BuildChannelCacheFileName("ch-2", "ch-2"));
        var probedJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Path"] = "http://jf:8096/api/tvheadend/relay/stream/ch-2?profile=pass&token=T",
            ["Container"] = "mpegts",
            ["MediaStreams"] = new[]
            {
                new Dictionary<string, object?> { ["Type"] = "Video", ["Codec"] = "mpeg2video" },
                new Dictionary<string, object?> { ["Type"] = "Audio", ["Codec"] = "mp2" },
            },
        });
        await File.WriteAllTextAsync(jellyfinFile, probedJson);

        // Pass-through snapshot carries NO output codecs — the probed source codecs are
        // exactly what the stream delivers and must be treated as a match, not destroyed.
        var passSnapshot = new ProfileSnapshot("pass", "uuid", "profile-mpegts", "mpegts", string.Empty, string.Empty, string.Empty, string.Empty, null);
        await sut.EnsureMediaInfoCacheStateAsync("ch-2", "http://jf:8096/api/tvheadend/relay/stream/ch-2?profile=pass&token=T", passSnapshot, true, true, CancellationToken.None);

        var after = await File.ReadAllTextAsync(jellyfinFile);
        Assert.Contains("mpeg2video", after);
        Assert.DoesNotContain("1280", after);
    }

    [Fact]
    public async Task InvalidateAllCachesAsync_AlsoClearsProfileStore()
    {
        using var cacheDir = new TempDirectory();
        var sut = CreateWarmupService(
            cacheDir.Path,
            new[] { new ChannelInfo { Id = "ch-1", Name = "One" } },
            probeResult: null);

        var mediaInfoDir = Path.Combine(cacheDir.Path, "mediainfo");
        var storeDir = Path.Combine(mediaInfoDir, "profiles");
        Directory.CreateDirectory(storeDir);

        var ownFile = Path.Combine(mediaInfoDir, sut.BuildChannelCacheFileName("ch-1", "ch-1"));
        await File.WriteAllTextAsync(ownFile, "{}");
        await File.WriteAllTextAsync(Path.Combine(storeDir, "a.pass.json"), "{}");

        var deleted = await sut.InvalidateAllCachesAsync();

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(ownFile));
        Assert.Empty(Directory.GetFiles(storeDir, "*.json"));
    }

    [Fact]
    public async Task InvalidateAllCachesAsync_LeavesOtherProvidersCacheFilesAlone()
    {
        // cache/mediainfo is Jellyfin's shared probe cache. Wiping *.json used to delete the
        // entries written by M3U/HDHomeRun and every other provider, costing each of them a
        // multi-second re-probe on their next tune.
        using var cacheDir = new TempDirectory();
        var sut = CreateWarmupService(
            cacheDir.Path,
            new[] { new ChannelInfo { Id = "ch-1", Name = "One" } },
            probeResult: null);

        var mediaInfoDir = Path.Combine(cacheDir.Path, "mediainfo");
        Directory.CreateDirectory(mediaInfoDir);

        var ownFile = Path.Combine(mediaInfoDir, sut.BuildChannelCacheFileName("ch-1", "ch-1"));
        var foreignFile = Path.Combine(mediaInfoDir, "some-other-provider-cache.json");
        await File.WriteAllTextAsync(ownFile, "{}");
        await File.WriteAllTextAsync(foreignFile, "{}");

        var deleted = await sut.InvalidateAllCachesAsync();

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(ownFile));
        Assert.True(File.Exists(foreignFile));
    }

    [Fact]
    public async Task Ensure_MatchingCache_RefreshesStaleStreamUrl()
    {
        using var cacheDir = new TempDirectory();
        var sut = CreateWarmupService(cacheDir.Path, Array.Empty<ChannelInfo>(), probeResult: null);

        var mediaInfoDir = Path.Combine(cacheDir.Path, "mediainfo");
        Directory.CreateDirectory(mediaInfoDir);
        var jellyfinFile = Path.Combine(mediaInfoDir, sut.BuildChannelCacheFileName("ch-3", "ch-3"));
        var probedJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Path"] = "http://jf:8096/api/tvheadend/relay/stream/ch-3?profile=pass&token=STALE",
            ["Container"] = "mpegts",
            ["MediaStreams"] = new[]
            {
                new Dictionary<string, object?> { ["Type"] = "Video", ["Codec"] = "mpeg2video" },
                new Dictionary<string, object?> { ["Type"] = "Audio", ["Codec"] = "ac3" },
            },
        });
        await File.WriteAllTextAsync(jellyfinFile, probedJson);

        // Same profile, matching media data — but the start uses a NEW URL (fresh token,
        // direct-to-TVHeadend shape). Jellyfin serves the cached Path verbatim, so the file
        // must pick up the new URL while keeping the probed streams untouched.
        var passSnapshot = new ProfileSnapshot("pass", "uuid", "profile-mpegts", "mpegts", string.Empty, string.Empty, string.Empty, string.Empty, null);
        await sut.EnsureMediaInfoCacheStateAsync("ch-3", "http://tvh:9981/stream/channel/ch-3?profile=pass&auth=FRESH", passSnapshot, true, true, CancellationToken.None);

        var after = await File.ReadAllTextAsync(jellyfinFile);
        Assert.Contains("auth=FRESH", after);
        Assert.DoesNotContain("token=STALE", after);
        Assert.Contains("mpeg2video", after);
        Assert.Contains("ac3", after);
    }

    [Fact]
    public async Task Warmup_AlreadyCachedChannel_SeedsMissingRuleCombinations()
    {
        using var cacheDir = new TempDirectory();
        var config = new PluginConfiguration();
        config.StreamingProfileSettings.ClientRules.Add(new StreamingProfileRule { Enabled = true, TvHeadendProfileName = "test-pass" });

        var sut = CreateWarmupService(cacheDir.Path, new[] { new ChannelInfo { Id = "ch-9", Name = "Nine" } }, probeResult: null, config);

        var mediaInfoDir = Path.Combine(cacheDir.Path, "mediainfo");
        Directory.CreateDirectory(mediaInfoDir);
        var jellyfinFile = Path.Combine(mediaInfoDir, sut.BuildChannelCacheFileName("ch-9", "ch-9"));
        var probedJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Path"] = "http://jf:8096/api/tvheadend/relay/stream/ch-9?profile=pass&token=KEEP",
            ["Container"] = "mpegts",
            ["MediaStreams"] = new[]
            {
                new Dictionary<string, object?> { ["Type"] = "Video", ["Codec"] = "mpeg2video" },
            },
        });
        await File.WriteAllTextAsync(jellyfinFile, probedJson);

        var progress = new CollectingProgress();
        await sut.WarmAllChannelCachesAsync(progress, CancellationToken.None);

        // The channel itself is skipped (cache is fresh) — but a rule added AFTER the file
        // was written must still get its (channel x profile) combination seeded.
        Assert.Single(progress.Reports, r => r.Status == "skipped");
        var storeDir = Path.Combine(mediaInfoDir, "profiles");
        Assert.Single(Directory.GetFiles(storeDir, "*.test_pass.json"));

        // The live file's own combination is preserved and the file itself stays untouched.
        var ownStore = Assert.Single(Directory.GetFiles(storeDir, "*.pass.json"));
        Assert.Contains("mpeg2video", await File.ReadAllTextAsync(ownStore));
        Assert.Contains("token=KEEP", await File.ReadAllTextAsync(jellyfinFile));
    }

    [Fact]
    public async Task Warmup_SeedingNeverOverwritesExistingStoreEntries()
    {
        using var cacheDir = new TempDirectory();
        var config = new PluginConfiguration();
        config.StreamingProfileSettings.ClientRules.Add(new StreamingProfileRule { Enabled = true, TvHeadendProfileName = "test-pass" });

        var sut = CreateWarmupService(cacheDir.Path, new[] { new ChannelInfo { Id = "ch-10", Name = "Ten" } }, probeResult: null, config);

        var mediaInfoDir = Path.Combine(cacheDir.Path, "mediainfo");
        var storeDir = Path.Combine(mediaInfoDir, "profiles");
        Directory.CreateDirectory(storeDir);
        var jellyfinName = sut.BuildChannelCacheFileName("ch-10", "ch-10");
        await File.WriteAllTextAsync(
            Path.Combine(mediaInfoDir, jellyfinName),
            "{\"Path\":\"http://jf/stream?profile=pass\",\"Container\":\"mpegts\",\"MediaStreams\":[{\"Type\":\"Video\",\"Codec\":\"mpeg2video\"}]}");

        // A store entry that already exists (e.g. probed during real playback) must never be
        // clobbered by warmup seeding.
        var storeFile = Path.Combine(storeDir, jellyfinName + ".test_pass.json");
        await File.WriteAllTextAsync(storeFile, "{\"Path\":\"http://jf/stream\",\"Marker\":\"KEEP-ME\"}");

        var progress = new CollectingProgress();
        await sut.WarmAllChannelCachesAsync(progress, CancellationToken.None);

        Assert.Contains("KEEP-ME", await File.ReadAllTextAsync(storeFile));
    }

    [Fact]
    public async Task Warmup_TranscodeProbe_IsNotCopiedIntoPassLikeVariantStore()
    {
        using var cacheDir = new TempDirectory();
        var config = new PluginConfiguration();
        config.StreamingProfileSettings.ClientRules.Add(new StreamingProfileRule { Enabled = true, TvHeadendProfileName = "test-pass" });

        // Effective profile declares h264 output (transcode-like); the rule variant is
        // pass-through-like (no output codecs).
        static ProfileSnapshot Factory(string? name) => string.Equals(name, "test-pass", StringComparison.OrdinalIgnoreCase)
            ? new ProfileSnapshot("test-pass", "uuid", "profile-mpegts", "mpegts", string.Empty, string.Empty, string.Empty, string.Empty, null)
            : new ProfileSnapshot("jellyfin", "uuid", "profile-transcode", "mpegts", string.Empty, string.Empty, "h264", "aac", null);

        var probeResult = new MediaInfo
        {
            Container = "mpegts",
            MediaStreams = new List<MediaStream>
            {
                new MediaStream { Type = MediaStreamType.Video, Codec = "hevc" },
                new MediaStream { Type = MediaStreamType.Audio, Codec = "aac" },
            },
        };

        var sut = CreateWarmupService(cacheDir.Path, new[] { new ChannelInfo { Id = "ch-11", Name = "Eleven" } }, probeResult, config, Factory);
        await sut.WarmAllChannelCachesAsync(new CollectingProgress(), CancellationToken.None);

        // The probed file holds TRANSCODE OUTPUT codecs — copying it into the pass store
        // would poison pass starts with wrong stream info. The variant must be synthetic.
        var storeDir = Path.Combine(cacheDir.Path, "mediainfo", "profiles");
        var variantFile = Assert.Single(Directory.GetFiles(storeDir, "*.test_pass.json"));
        Assert.DoesNotContain("hevc", await File.ReadAllTextAsync(variantFile));
    }

    [Fact]
    public async Task Warmup_PassLikeProbe_IsCopiedIntoPassLikeVariantStore()
    {
        using var cacheDir = new TempDirectory();
        var config = new PluginConfiguration();
        config.StreamingProfileSettings.ClientRules.Add(new StreamingProfileRule { Enabled = true, TvHeadendProfileName = "test-pass" });

        // Both the effective profile and the rule variant are pass-through-like: the probed
        // SOURCE codecs apply to both combinations and must be reused.
        static ProfileSnapshot Factory(string? name) =>
            new ProfileSnapshot(name ?? "pass", "uuid", "profile-mpegts", "mpegts", string.Empty, string.Empty, string.Empty, string.Empty, null);

        var probeResult = new MediaInfo
        {
            Container = "mpegts",
            MediaStreams = new List<MediaStream>
            {
                new MediaStream { Type = MediaStreamType.Video, Codec = "hevc" },
                new MediaStream { Type = MediaStreamType.Audio, Codec = "ac3" },
            },
        };

        var sut = CreateWarmupService(cacheDir.Path, new[] { new ChannelInfo { Id = "ch-12", Name = "Twelve" } }, probeResult, config, Factory);
        await sut.WarmAllChannelCachesAsync(new CollectingProgress(), CancellationToken.None);

        var storeDir = Path.Combine(cacheDir.Path, "mediainfo", "profiles");
        var variantFile = Assert.Single(Directory.GetFiles(storeDir, "*.test_pass.json"));
        Assert.Contains("hevc", await File.ReadAllTextAsync(variantFile));
    }

    private static MediaInfoCacheService CreateWarmupService(
        string cachePath,
        IReadOnlyList<ChannelInfo> channels,
        MediaInfo? probeResult,
        PluginConfiguration? config = null,
        Func<string?, ProfileSnapshot>? snapshotFactory = null)
    {
        config ??= new PluginConfiguration();

        var library = new Mock<ILibraryManager>();
        var idsByName = new ConcurrentDictionary<string, Guid>();
        library.Setup(x => x.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
            .Returns<string, Type>((name, _) => idsByName.GetOrAdd(name, _ => Guid.NewGuid()));

        var guide = new Mock<IGuideService>();
        guide.Setup(x => x.GetChannelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(channels);

        var profileResolver = new StreamingProfileResolver(
            NullLogger<StreamingProfileResolver>.Instance,
            new ConfigurationProvider(() => config));

        var containerResolver = new Mock<IProfileContainerResolver>();
        containerResolver.Setup(x => x.ResolveProfileSnapshotAsync(It.IsAny<PluginConfiguration>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<PluginConfiguration, string?, CancellationToken>((_, name, _) => Task.FromResult(
                snapshotFactory != null
                    ? snapshotFactory(name)
                    : new ProfileSnapshot("pass", "uuid", "profile-mpegts", "mpegts", string.Empty, string.Empty, "h264", "aac", null)));

        var relay = new Mock<IRelayUrlBuilder>();
        relay.Setup(x => x.BuildTokenizedStreamRelayUrlAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<string, string?, string?, string?, string?, CancellationToken>((ch, _, _, _, _, _) =>
                Task.FromResult($"http://jellyfin:8096/api/tvheadend/stream/{Uri.EscapeDataString(ch)}"));

        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);

        var encoder = new Mock<IMediaEncoder>();
        encoder.Setup(x => x.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(probeResult!);

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(x => x.CachePath).Returns(cachePath);

        return new MediaInfoCacheService(
            NullLogger<MediaInfoCacheService>.Instance,
            library.Object,
            new CachePathProvider(() => cachePath),
            guide.Object,
            profileResolver,
            containerResolver.Object,
            relay.Object,
            api.Object,
            encoder.Object,
            appPaths.Object);
    }

    private sealed class CollectingProgress : IProgress<CacheWarmupProgress>
    {
        private readonly object _lock = new();
        private readonly List<CacheWarmupProgress> _reports = new();

        public IReadOnlyList<CacheWarmupProgress> Reports
        {
            get
            {
                lock (_lock)
                {
                    return _reports.ToList();
                }
            }
        }

        public void Report(CacheWarmupProgress value)
        {
            lock (_lock)
            {
                _reports.Add(value);
            }
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tvhapi-cache-tests-" + Guid.NewGuid().ToString("N"));
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
