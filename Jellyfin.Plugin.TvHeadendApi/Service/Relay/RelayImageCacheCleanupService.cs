// Background hosted service that periodically prunes expired cached relay images.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// Hosted service that periodically removes cached relay image files older than the configured
/// retention period, so the on-disk image cache does not grow without bound.
/// </summary>
internal sealed class RelayImageCacheCleanupService : IHostedService, IDisposable
{
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    private readonly ILogger<RelayImageCacheCleanupService> _logger;
    private readonly RelayImageCache _imageCache;
    private Timer? _cleanupTimer;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayImageCacheCleanupService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="imageCache">The relay image cache to prune.</param>
    public RelayImageCacheCleanupService(ILogger<RelayImageCacheCleanupService> logger, RelayImageCache imageCache)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _imageCache = imageCache ?? throw new ArgumentNullException(nameof(imageCache));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer = new Timer(_ => RunPrune(), null, FirstRunDelay, Interval);
        _logger.LogInformation("RelayImageCacheCleanupService started — prune interval: {Hours}h.", Interval.TotalHours);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _logger.LogInformation("RelayImageCacheCleanupService stopped.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cleanupTimer?.Dispose();
    }

    private void RunPrune()
    {
        try
        {
            _imageCache.PruneExpired();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to prune the relay image cache.");
        }
    }
}
