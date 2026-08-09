// On-disk cache for relayed TVHeadend images (channel logos, EPG artwork).

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// A simple, thread-safe on-disk cache for relayed TVHeadend images. Images are small and rarely
/// change, so caching them in the plugin's data folder avoids re-fetching from TVHeadend on every
/// request — keeping artwork available even when TVHeadend is briefly unreachable and sparing the
/// backend from repeated load (Jellyfin can request the same logo from many clients).
/// </summary>
internal sealed class RelayImageCache
{
    /// <summary>Sub-directory under the plugin cache root that holds cached image files.</summary>
    private const string SubDirectory = "relay-images";

    private readonly ILogger<RelayImageCache> _logger;
    private readonly ConfigurationProvider _configProvider;
    private readonly Func<string?> _cacheRootResolver;

    private long _writeErrorCount;
    private long _readErrorCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayImageCache"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="configProvider">Provides the live plugin configuration.</param>
    /// <param name="cachePathProvider">Resolves the plugin cache root directory.</param>
    public RelayImageCache(ILogger<RelayImageCache> logger, ConfigurationProvider configProvider, CachePathProvider cachePathProvider)
        : this(logger, configProvider, () => (cachePathProvider ?? throw new ArgumentNullException(nameof(cachePathProvider))).Path)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayImageCache"/> class with an explicit cache-root
    /// resolver. Used for unit testing against a temporary directory.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="configProvider">Provides the live plugin configuration.</param>
    /// <param name="cacheRootResolver">Resolves the plugin cache root directory, or null when unavailable.</param>
    internal RelayImageCache(ILogger<RelayImageCache> logger, ConfigurationProvider configProvider, Func<string?> cacheRootResolver)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _cacheRootResolver = cacheRootResolver ?? throw new ArgumentNullException(nameof(cacheRootResolver));
    }

    /// <summary>
    /// Gets a value indicating whether image caching is enabled. Requires both the configuration flag
    /// and an available cache directory (the data folder is not present during very early startup).
    /// </summary>
    public bool Enabled => (_configProvider.Configuration?.EnableRelayImageCache ?? true) && CacheDirectory() != null;

    /// <summary>Gets the retention period in days (minimum 1).</summary>
    private int RetentionDays => Math.Max(1, _configProvider.Configuration?.RelayImageCacheRetentionDays ?? 30);

    /// <summary>
    /// Attempts to read a fresh cached image for the given upstream path.
    /// </summary>
    /// <param name="upstreamPath">The relative TVHeadend image path (the cache key).</param>
    /// <returns>The cached image, or <c>null</c> on a miss, a stale entry, or when caching is disabled.</returns>
    public CachedImage? TryGet(string upstreamPath)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(upstreamPath))
        {
            return null;
        }

        var dir = CacheDirectory();
        if (dir == null)
        {
            return null;
        }

        var key = ComputeKey(upstreamPath);
        var binPath = Path.Combine(dir, key + ".bin");
        try
        {
            var info = new FileInfo(binPath);
            if (!info.Exists)
            {
                return null;
            }

            if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromDays(RetentionDays))
            {
                return null; // stale — treat as a miss; the cleanup sweep removes it
            }

            var bytes = File.ReadAllBytes(binPath);
            if (bytes.Length == 0)
            {
                return null;
            }

            var typePath = Path.Combine(dir, key + ".type");
            var contentType = File.Exists(typePath) ? File.ReadAllText(typePath) : null;
            return new CachedImage(bytes, string.IsNullOrWhiteSpace(contentType) ? null : contentType);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Threading.Interlocked.Increment(ref _readErrorCount);
            return null; // never let a cache read break the relay
        }
    }

    /// <summary>
    /// Stores an image in the cache. Writes atomically (temp file + rename) so concurrent stores of the
    /// same image and concurrent reads never observe a partially-written file.
    /// </summary>
    /// <param name="upstreamPath">The relative TVHeadend image path (the cache key).</param>
    /// <param name="body">The image bytes.</param>
    /// <param name="contentType">The upstream content type, if known.</param>
    public void Store(string upstreamPath, byte[] body, string? contentType)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!Enabled || body.Length == 0 || string.IsNullOrWhiteSpace(upstreamPath))
        {
            return;
        }

        var dir = CacheDirectory();
        if (dir == null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(dir);
            var key = ComputeKey(upstreamPath);
            var binPath = Path.Combine(dir, key + ".bin");
            var tempPath = Path.Combine(dir, key + "." + Path.GetRandomFileName() + ".tmp");

            File.WriteAllBytes(tempPath, body);
            File.Move(tempPath, binPath, overwrite: true); // atomic replace on the same volume

            if (!string.IsNullOrWhiteSpace(contentType))
            {
                File.WriteAllText(Path.Combine(dir, key + ".type"), contentType);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Threading.Interlocked.Increment(ref _writeErrorCount);
            _logger.LogDebug(ex, "Failed to cache relayed image for {UpstreamPath}", upstreamPath);
        }
    }

    /// <summary>
    /// Returns a snapshot of cache size and health for the dashboard.
    /// </summary>
    /// <returns>The current cache statistics.</returns>
    public RelayImageCacheStats GetStats()
    {
        long files = 0;
        long bytes = 0;
        var dir = CacheDirectory();
        if (dir != null && Directory.Exists(dir))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.bin"))
                {
                    files++;
                    try
                    {
                        bytes += new FileInfo(f).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // ignore a single unreadable file in the size tally
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // directory enumeration failed — report what we have
            }
        }

        return new RelayImageCacheStats(
            Enabled,
            files,
            bytes,
            RetentionDays,
            System.Threading.Interlocked.Read(ref _writeErrorCount),
            System.Threading.Interlocked.Read(ref _readErrorCount));
    }

    /// <summary>
    /// Removes cached image files older than the retention period.
    /// </summary>
    /// <returns>The number of cached images removed.</returns>
    public int PruneExpired()
    {
        var dir = CacheDirectory();
        if (dir == null || !Directory.Exists(dir))
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(RetentionDays);
        var removed = 0;
        foreach (var binPath in Directory.EnumerateFiles(dir, "*.bin"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(binPath) >= cutoff)
                {
                    continue;
                }

                File.Delete(binPath);
                var typePath = Path.Combine(dir, Path.GetFileNameWithoutExtension(binPath) + ".type");
                if (File.Exists(typePath))
                {
                    File.Delete(typePath);
                }

                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip files we cannot remove this pass; the next sweep retries.
            }
        }

        if (removed > 0)
        {
            _logger.LogInformation("Pruned {Count} expired relay image(s) from the cache.", removed);
        }

        return removed;
    }

    private string? CacheDirectory()
    {
        var root = _cacheRootResolver();
        return string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, SubDirectory);
    }

    private static string ComputeKey(string upstreamPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(upstreamPath));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
