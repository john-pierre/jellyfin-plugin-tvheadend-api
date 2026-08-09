// Tests for RelayImageCache — on-disk caching of relayed TVHeadend images.

using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Relay;

public sealed class RelayImageCacheTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly PluginConfiguration _config;
    private readonly RelayImageCache _cache;

    public RelayImageCacheTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "tvh-img-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _config = new PluginConfiguration();
        var provider = new ConfigurationProvider(() => _config);
        _cache = new RelayImageCache(NullLogger<RelayImageCache>.Instance, provider, () => _tempRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    private string CacheDir => Path.Combine(_tempRoot, "relay-images");

    [Fact]
    [Trait("Category", "Unit")]
    public void Store_ThenTryGet_ReturnsBytesAndContentType()
    {
        var body = new byte[] { 1, 2, 3, 4, 5 };
        _cache.Store("imagecache/42", body, "image/png");

        var hit = _cache.TryGet("imagecache/42");

        Assert.NotNull(hit);
        Assert.Equal(body, hit!.Bytes);
        Assert.Equal("image/png", hit.ContentType);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TryGet_Miss_ReturnsNull()
    {
        Assert.Null(_cache.TryGet("imagecache/does-not-exist"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TryGet_DifferentPaths_DoNotCollide()
    {
        _cache.Store("imagecache/1", new byte[] { 1 }, "image/png");
        _cache.Store("imagecache/2", new byte[] { 2 }, "image/jpeg");

        Assert.Equal(new byte[] { 1 }, _cache.TryGet("imagecache/1")!.Bytes);
        Assert.Equal(new byte[] { 2 }, _cache.TryGet("imagecache/2")!.Bytes);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Store_Overwrites_ExistingEntry()
    {
        _cache.Store("imagecache/42", new byte[] { 1, 1 }, "image/png");
        _cache.Store("imagecache/42", new byte[] { 9, 9, 9 }, "image/jpeg");

        var hit = _cache.TryGet("imagecache/42");
        Assert.Equal(new byte[] { 9, 9, 9 }, hit!.Bytes);
        Assert.Equal("image/jpeg", hit.ContentType);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TryGet_ExpiredEntry_ReturnsNull()
    {
        _cache.Store("imagecache/old", new byte[] { 1, 2, 3 }, "image/png");
        // Age the file beyond the default 30-day retention.
        var binFile = Directory.EnumerateFiles(CacheDir, "*.bin").Single();
        File.SetLastWriteTimeUtc(binFile, DateTime.UtcNow.AddDays(-31));

        Assert.Null(_cache.TryGet("imagecache/old"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PruneExpired_RemovesStaleFiles_KeepsFresh()
    {
        _cache.Store("imagecache/stale", new byte[] { 1 }, "image/png");
        _cache.Store("imagecache/fresh", new byte[] { 2 }, "image/png");

        var staleKey = Directory.EnumerateFiles(CacheDir, "*.bin")
            .First(f => File.ReadAllBytes(f).SequenceEqual(new byte[] { 1 }));
        File.SetLastWriteTimeUtc(staleKey, DateTime.UtcNow.AddDays(-40));

        var removed = _cache.PruneExpired();

        Assert.Equal(1, removed);
        Assert.Null(_cache.TryGet("imagecache/stale"));
        Assert.NotNull(_cache.TryGet("imagecache/fresh"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Disabled_TryGetAndStore_AreNoOps()
    {
        _config.EnableRelayImageCache = false;

        _cache.Store("imagecache/42", new byte[] { 1, 2, 3 }, "image/png");

        Assert.False(_cache.Enabled);
        Assert.Null(_cache.TryGet("imagecache/42"));
        Assert.False(Directory.Exists(CacheDir));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetStats_ReportsFileCountBytesAndHealth()
    {
        _cache.Store("imagecache/1", new byte[100], "image/png");
        _cache.Store("imagecache/2", new byte[200], "image/jpeg");

        var s = _cache.GetStats();

        Assert.True(s.Enabled);
        Assert.Equal(2, s.FileCount);
        Assert.Equal(300, s.TotalBytes);
        Assert.Equal(30, s.RetentionDays);
        Assert.Equal(0, s.WriteErrors + s.ReadErrors);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Enabled_FalseWhenNoCacheRoot()
    {
        var provider = new ConfigurationProvider(() => _config);
        var noRootCache = new RelayImageCache(NullLogger<RelayImageCache>.Instance, provider, () => null);

        Assert.False(noRootCache.Enabled);
        Assert.Null(noRootCache.TryGet("imagecache/42"));
    }
}
