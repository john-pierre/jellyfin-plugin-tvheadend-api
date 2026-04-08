using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Streaming;

/// <summary>
/// Default implementation for stream lifecycle requests that TVHeadend does not actively support.
/// </summary>
internal sealed class LiveStreamLifecycleService : ILiveStreamLifecycleService
{
    private readonly ILogger<LiveStreamLifecycleService> _logger;

    public LiveStreamLifecycleService(ILogger<LiveStreamLifecycleService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task CloseLiveStreamAsync(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            _logger.LogWarning("Stream ID is null or empty. No action required.");
        }
        else
        {
            _logger.LogInformation("TVHeadEnd does not support closing live streams directly. No action taken for Stream ID: {StreamId}.", id);
        }

        return Task.CompletedTask;
    }

    public Task ResetTunerAsync(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            _logger.LogWarning("Tuner ID is null or empty. No reset action required.");
        }
        else
        {
            _logger.LogInformation("TVHeadEnd does not require or support resetting tuners. No action taken for Tuner ID: {TunerId}.", id);
        }

        return Task.CompletedTask;
    }
}
