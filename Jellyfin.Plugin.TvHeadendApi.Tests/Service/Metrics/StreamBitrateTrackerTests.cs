// Unit tests for the rolling/peak bitrate computation used by the relay drain loop.

using Jellyfin.Plugin.TvHeadendApi.Service.Metrics;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Metrics;

/// <summary>
/// Verifies the honest rolling-window and peak bitrate semantics:
/// rolling covers only the trailing ~10 seconds, peak is the max rolling sample,
/// and neither is a disguised cumulative average.
/// </summary>
public sealed class StreamBitrateTrackerTests
{
    [Fact]
    public void FirstSample_UsesStreamStartAsWindowAnchor()
    {
        var tracker = new StreamBitrateTracker();

        // 5s, 5 MB → 5_000_000 * 8 / 5 = 8_000_000 bits/s.
        var rolling = tracker.AddSample(5000, 5_000_000);

        Assert.Equal(8_000_000, rolling, precision: 0);
        Assert.Equal(rolling, tracker.PeakBitrate);
    }

    [Fact]
    public void Rolling_ReflectsOnlyTheTrailingWindow_NotTheCumulativeAverage()
    {
        var tracker = new StreamBitrateTracker();

        // Fast start: 10 MB in the first 10 seconds.
        tracker.AddSample(5000, 5_000_000);
        tracker.AddSample(10_000, 10_000_000);

        // Then the stream stalls: only 1 MB over the next 10 seconds.
        var rolling = tracker.AddSample(20_000, 11_000_000);

        // Window anchor is the 10s sample: (11MB - 10MB) * 8 / 10s = 800_000 bits/s.
        Assert.Equal(800_000, rolling, precision: 0);

        // A cumulative average would be 11MB * 8 / 20s = 4.4 Mbit/s — must NOT be reported as rolling.
        Assert.True(rolling < 1_000_000, "rolling must reflect the stalled window, not the cumulative average");
    }

    [Fact]
    public void Peak_IsTheMaximumRollingSample()
    {
        var tracker = new StreamBitrateTracker();

        tracker.AddSample(5000, 5_000_000);      // 8 Mbit/s
        tracker.AddSample(10_000, 10_000_000);   // (10-5)MB*8/5s = 8 Mbit/s
        tracker.AddSample(15_000, 20_000_000);   // (20-5)MB*8/10s = 12 Mbit/s
        tracker.AddSample(25_000, 20_500_000);   // stall

        Assert.Equal(12_000_000, tracker.PeakBitrate, precision: 0);
    }

    [Fact]
    public void OldSamples_FallOutOfTheWindow()
    {
        var tracker = new StreamBitrateTracker();

        tracker.AddSample(1000, 1_000_000);
        tracker.AddSample(2000, 2_000_000);

        // 60 seconds later — both old samples are outside the 10s window, but the last one
        // is retained as anchor so the delta covers the trailing span.
        var rolling = tracker.AddSample(62_000, 62_000_000);

        // Anchor = (2000, 2MB): (62MB - 2MB) * 8 / 60s = 8_000_000 bits/s.
        Assert.Equal(8_000_000, rolling, precision: 0);
    }

    [Fact]
    public void ZeroElapsed_ReturnsZeroWithoutDividing()
    {
        var tracker = new StreamBitrateTracker();

        var rolling = tracker.AddSample(0, 0);

        Assert.Equal(0, rolling);
        Assert.Equal(0, tracker.PeakBitrate);
    }
}
