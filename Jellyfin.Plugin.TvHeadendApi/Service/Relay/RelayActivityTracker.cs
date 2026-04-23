// Thread-safe in-memory tracker for active relay streams.

using System.Threading;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// Lightweight thread-safe tracker for the number of currently active relay streams.
/// Provides live state that complements persisted historical metrics.
/// </summary>
public sealed class RelayActivityTracker
{
    private int _activeStreams;

    /// <summary>Gets the current number of active relay streams.</summary>
    public int ActiveStreams => Volatile.Read(ref _activeStreams);

    /// <summary>Increments the active stream count.</summary>
    /// <returns>The new active stream count.</returns>
    public int IncrementStreams() => Interlocked.Increment(ref _activeStreams);

    /// <summary>Decrements the active stream count (floor at 0).</summary>
    /// <returns>The new active stream count.</returns>
    public int DecrementStreams()
    {
        while (true)
        {
            var current = Volatile.Read(ref _activeStreams);
            if (current <= 0)
            {
                return 0;
            }

            if (Interlocked.CompareExchange(ref _activeStreams, current - 1, current) == current)
            {
                return current - 1;
            }
        }
    }
}
