// Rolling bitrate tracker — honest rolling/peak bitrate computation for the relay drain loop.

using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Metrics;

/// <summary>
/// Computes an honest rolling bitrate over a sliding time window from cumulative byte
/// counters, plus the peak of those rolling samples. Used by the relay stream drain loop,
/// which feeds it one sample per periodic session update.
/// <para>
/// Rolling = bits transferred within the last ~<see cref="WindowMs"/> milliseconds divided
/// by that window's actual span. Peak = maximum rolling sample observed so far.
/// The cumulative average is intentionally NOT computed here — it is trivially
/// <c>totalBytes * 8 / elapsedSeconds</c> at the call site.
/// </para>
/// Not thread-safe — each stream request owns exactly one instance on its request path.
/// </summary>
public sealed class StreamBitrateTracker
{
    /// <summary>Sliding window span for the rolling bitrate, in milliseconds.</summary>
    public const double WindowMs = 10_000;

    private readonly Queue<Sample> _samples = new();

    /// <summary>Gets the peak rolling bitrate observed so far, in bits per second. 0 until the first sample.</summary>
    public double PeakBitrate { get; private set; }

    /// <summary>
    /// Adds a sample (cumulative bytes at a given elapsed time) and returns the rolling
    /// bitrate in bits per second over the last ~10 seconds.
    /// </summary>
    /// <param name="elapsedMs">Elapsed stream time in milliseconds when the sample was taken.</param>
    /// <param name="totalBytes">Cumulative bytes sent at that moment.</param>
    /// <returns>The rolling bitrate in bits per second (0 when the window span is not yet measurable).</returns>
    public double AddSample(double elapsedMs, long totalBytes)
    {
        // Drop samples that fell out of the window, but always keep at least one previous
        // sample as the window anchor so the delta covers the full trailing span.
        while (_samples.Count > 1 && elapsedMs - _samples.Peek().ElapsedMs > WindowMs)
        {
            _samples.Dequeue();
        }

        double rolling;
        if (_samples.Count == 0)
        {
            // First sample: the window starts at stream start (elapsed 0, 0 bytes).
            rolling = elapsedMs > 0 ? totalBytes * 8.0 / (elapsedMs / 1000.0) : 0;
        }
        else
        {
            var anchor = _samples.Peek();
            var spanMs = elapsedMs - anchor.ElapsedMs;
            rolling = spanMs > 0 ? (totalBytes - anchor.TotalBytes) * 8.0 / (spanMs / 1000.0) : 0;
        }

        _samples.Enqueue(new Sample(elapsedMs, totalBytes));

        if (rolling > PeakBitrate)
        {
            PeakBitrate = rolling;
        }

        return rolling;
    }

    private readonly record struct Sample(double ElapsedMs, long TotalBytes);
}
