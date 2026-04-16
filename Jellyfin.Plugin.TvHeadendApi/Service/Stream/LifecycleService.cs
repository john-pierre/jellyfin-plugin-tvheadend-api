using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Stream;

/// <summary>
/// Default implementation for stream lifecycle requests that TVHeadend does not actively support.
/// </summary>
internal sealed class LifecycleService : ILifecycleService
{
    private readonly ILogger<LifecycleService> _logger;

    public LifecycleService(ILogger<LifecycleService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task CloseLiveStreamAsync(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            _logger.LogDebug("CloseLiveStream called with empty ID. No action required.");
        }
        else
        {
            // TVH HTTP streams close when the client disconnects; no explicit server-side close needed.
            _logger.LogDebug("CloseLiveStream called for Stream ID: {StreamId}. TVH closes HTTP streams on client disconnect.", id);
        }

        return Task.CompletedTask;
    }

    public Task ResetTunerAsync(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            _logger.LogDebug("ResetTuner called with empty ID. No action required.");
        }
        else
        {
            _logger.LogDebug("ResetTuner called for Tuner ID: {TunerId}. TVH manages tuner lifecycle internally.", id);
        }

        return Task.CompletedTask;
    }
}
