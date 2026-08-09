// Warms the channel-name cache at startup so telemetry never falls back to raw UUIDs.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Guide;

/// <summary>
/// Populates the <see cref="ChannelNameCache"/> once at startup by fetching the channel list.
/// Without this, sessions recorded between a server restart and the next guide refresh persist
/// raw channel UUIDs instead of display names in the telemetry history (the cache is otherwise
/// only filled as a side effect of <see cref="IGuideService.GetChannelsAsync"/>).
/// Retries with back-off because TVHeadend may not be reachable yet while Jellyfin boots.
/// </summary>
internal sealed class ChannelNameCacheWarmupService : IHostedService, IDisposable
{
    private const int MaxAttempts = 10;

    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly IGuideService _guideService;
    private readonly ChannelNameCache _cache;
    private readonly ILogger<ChannelNameCacheWarmupService> _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _warmupTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelNameCacheWarmupService"/> class.
    /// </summary>
    /// <param name="guideService">Guide service whose channel fetch populates the cache.</param>
    /// <param name="cache">The channel-name cache to warm.</param>
    /// <param name="logger">Logger instance.</param>
    public ChannelNameCacheWarmupService(
        IGuideService guideService,
        ChannelNameCache cache,
        ILogger<ChannelNameCacheWarmupService> logger)
    {
        _guideService = guideService ?? throw new ArgumentNullException(nameof(guideService));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _warmupTask = Task.Run(() => WarmUpAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_warmupTask != null)
        {
            try
            {
                await _warmupTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts.Dispose();
    }

    private async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(InitialDelay, cancellationToken).ConfigureAwait(false);

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                if (_cache.Count > 0)
                {
                    // A guide fetch already populated the cache — nothing to do.
                    return;
                }

                try
                {
                    // GetChannelsAsync populates the cache as a side effect.
                    await _guideService.GetChannelsAsync(cancellationToken).ConfigureAwait(false);
                    if (_cache.Count > 0)
                    {
                        _logger.LogInformation("Channel-name cache warmed with {Count} channels.", _cache.Count);
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Channel-name cache warmup attempt {Attempt}/{Max} failed.", attempt, MaxAttempts);
                }

                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogWarning("Channel-name cache warmup gave up after {Max} attempts — telemetry falls back to channel UUIDs until the next guide refresh.", MaxAttempts);
        }
        catch (OperationCanceledException)
        {
            // Shutdown — nothing to log.
        }
    }
}
