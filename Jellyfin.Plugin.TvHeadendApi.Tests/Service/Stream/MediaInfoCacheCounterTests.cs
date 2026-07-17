// Unit tests for the mediainfo cache reconciliation outcomes and since-start counters.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Stream;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Stream;

/// <summary>
/// Verifies that <see cref="MediaInfoCacheService.EnsureMediaInfoCacheStateAsync"/> returns
/// honest outcomes (hit/miss/mismatch/restored) and that the since-start counters count
/// mismatches as mismatches (not hits) and invalidations on every delete path.
/// </summary>
public sealed class MediaInfoCacheCounterTests : IDisposable
{
    private static readonly Guid FixedInternalChannelId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private readonly string _tempDir;
    private readonly MediaInfoCacheService _sut;

    public MediaInfoCacheCounterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tvh_cache_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var library = new Mock<ILibraryManager>();
        library.Setup(x => x.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>())).Returns(FixedInternalChannelId);
        _sut = new MediaInfoCacheService(NullLogger<MediaInfoCacheService>.Instance, library.Object, () => _tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    private static ProfileSnapshot JellyfinSnapshot()
        => new("jellyfin", "uuid-1", "profile-mpegts", "mpegts", string.Empty, string.Empty, "h264", "aac", null);

    private static ProfileSnapshot PassSnapshot()
        => new("pass", "uuid-2", "profile-mpegts", "mpegts", string.Empty, string.Empty, string.Empty, string.Empty, null);

    [Fact]
    public async Task NoCacheFile_ReturnsMiss_AndCountsMiss()
    {
        var status = await _sut.EnsureMediaInfoCacheStateAsync(
            "ch-1", "http://jf/relay/stream/ch-1?profile=jellyfin&token=T1", JellyfinSnapshot(), true, true, CancellationToken.None);

        Assert.Equal(MediaInfoCacheStatus.Miss, status);
        var counters = _sut.GetCounters();
        Assert.Equal(0, counters.Hits);
        Assert.Equal(1, counters.Misses);
        Assert.Equal(0, counters.Mismatches);
    }

    [Fact]
    public async Task MatchingCacheFile_ReturnsHit_AndCountsHit()
    {
        // First call writes the synthetic file (miss), second call finds it matching (hit).
        await _sut.EnsureMediaInfoCacheStateAsync(
            "ch-1", "http://jf/relay/stream/ch-1?profile=jellyfin&token=T1", JellyfinSnapshot(), true, true, CancellationToken.None);
        var status = await _sut.EnsureMediaInfoCacheStateAsync(
            "ch-1", "http://jf/relay/stream/ch-1?profile=jellyfin&token=T2", JellyfinSnapshot(), true, true, CancellationToken.None);

        Assert.Equal(MediaInfoCacheStatus.Hit, status);
        var counters = _sut.GetCounters();
        Assert.Equal(1, counters.Hits);
        Assert.Equal(1, counters.Misses);
        Assert.Equal(0, counters.Mismatches);
    }

    [Fact]
    public async Task ProfileMismatch_ReturnsMismatch_AndDoesNotCountAsHit()
    {
        await _sut.EnsureMediaInfoCacheStateAsync(
            "ch-1", "http://jf/relay/stream/ch-1?profile=jellyfin&token=T1", JellyfinSnapshot(), true, true, CancellationToken.None);

        // Same channel now resolves to "pass" — the existing file belongs to "jellyfin".
        var status = await _sut.EnsureMediaInfoCacheStateAsync(
            "ch-1", "http://jf/relay/stream/ch-1?profile=pass&token=T2", PassSnapshot(), true, true, CancellationToken.None);

        Assert.Equal(MediaInfoCacheStatus.Mismatch, status);
        var counters = _sut.GetCounters();
        Assert.Equal(0, counters.Hits);
        Assert.Equal(1, counters.Misses);
        Assert.Equal(1, counters.Mismatches);
    }

    [Fact]
    public async Task Mismatch_WithProactiveDisabled_DeletesFile_AndCountsInvalidation()
    {
        await _sut.EnsureMediaInfoCacheStateAsync(
            "ch-1", "http://jf/relay/stream/ch-1?profile=jellyfin&token=T1", JellyfinSnapshot(), true, true, CancellationToken.None);

        var status = await _sut.EnsureMediaInfoCacheStateAsync(
            "ch-1", "http://jf/relay/stream/ch-1?profile=pass&token=T2", PassSnapshot(), false, true, CancellationToken.None);

        Assert.Equal(MediaInfoCacheStatus.Mismatch, status);
        Assert.Equal(1, _sut.GetCounters().Invalidations);
    }

    [Fact]
    public async Task MismatchThenSwitchBack_RestoresFromProfileStore()
    {
        // jellyfin (miss+write) → pass (mismatch, jellyfin file preserved in profile store,
        // pass file written) → jellyfin again: restored from the per-profile store.
        await _sut.EnsureMediaInfoCacheStateAsync(
            "ch-1", "http://jf/relay/stream/ch-1?profile=jellyfin&token=T1", JellyfinSnapshot(), true, true, CancellationToken.None);
        await _sut.EnsureMediaInfoCacheStateAsync(
            "ch-1", "http://jf/relay/stream/ch-1?profile=pass&token=T2", PassSnapshot(), true, true, CancellationToken.None);

        // Delete the live file so the third call takes the miss→restore path.
        var liveFile = Path.Combine(_tempDir, "mediainfo", _sut.BuildChannelCacheFileName("ch-1", "ch-1"));
        File.Delete(liveFile);

        var status = await _sut.EnsureMediaInfoCacheStateAsync(
            "ch-1", "http://jf/relay/stream/ch-1?profile=jellyfin&token=T3", JellyfinSnapshot(), true, true, CancellationToken.None);

        Assert.Equal(MediaInfoCacheStatus.Restored, status);
    }

    [Fact]
    public async Task ValidationDisabled_ExistingFile_CountsAsHit()
    {
        await _sut.EnsureMediaInfoCacheStateAsync(
            "ch-1", "http://jf/relay/stream/ch-1?profile=jellyfin&token=T1", JellyfinSnapshot(), true, true, CancellationToken.None);

        var status = await _sut.EnsureMediaInfoCacheStateAsync(
            "ch-1", "http://jf/relay/stream/ch-1?profile=jellyfin&token=T2", JellyfinSnapshot(), true, false, CancellationToken.None);

        Assert.Equal(MediaInfoCacheStatus.Hit, status);
        Assert.Equal(1, _sut.GetCounters().Hits);
    }

    [Fact]
    public void RecordStreamBuildReuseHit_IncrementsHitCounter()
    {
        _sut.RecordStreamBuildReuseHit();
        _sut.RecordStreamBuildReuseHit();

        Assert.Equal(2, _sut.GetCounters().Hits);
    }

    [Fact]
    public async Task InvalidateChannelCache_CountsInvalidation()
    {
        await _sut.EnsureMediaInfoCacheStateAsync(
            "ch-1", "http://jf/relay/stream/ch-1?profile=jellyfin&token=T1", JellyfinSnapshot(), true, true, CancellationToken.None);

        var deleted = await _sut.InvalidateChannelCacheAsync("ch-1");

        Assert.True(deleted);
        Assert.Equal(1, _sut.GetCounters().Invalidations);
    }

    [Fact]
    public async Task UnreadableCacheFile_IsDeleted_AndCountsInvalidation()
    {
        var mediaInfoDir = Path.Combine(_tempDir, "mediainfo");
        Directory.CreateDirectory(mediaInfoDir);
        var liveFile = Path.Combine(mediaInfoDir, _sut.BuildChannelCacheFileName("ch-1", "ch-1"));
        await File.WriteAllTextAsync(liveFile, "this is not json {{{");

        var status = await _sut.EnsureMediaInfoCacheStateAsync(
            "ch-1", "http://jf/relay/stream/ch-1?profile=jellyfin&token=T1", JellyfinSnapshot(), true, true, CancellationToken.None);

        // Unreadable file → deleted (invalidation) → treated as a miss.
        Assert.Equal(MediaInfoCacheStatus.Miss, status);
        Assert.False(File.Exists(liveFile) && new FileInfo(liveFile).Length == 0);
        Assert.Equal(1, _sut.GetCounters().Invalidations);
        Assert.Equal(1, _sut.GetCounters().Misses);
    }
}
