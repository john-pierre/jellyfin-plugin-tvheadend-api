// Unit tests for the consolidated RelayTimingContext → RelayRequestMetric conversion:
// session identity, zapping telemetry, stream outcome derivation, and the corrected
// StartupFailedWithin5Seconds semantics.

using System.Threading;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Model.Relay;

/// <summary>
/// Tests for the single persistence point of relay stream telemetry.
/// </summary>
public sealed class RelayTimingContextTelemetryTests
{
    [Fact]
    public void ToMetric_Stream_CarriesSessionIdentityAndZappingTelemetry()
    {
        var ctx = new RelayTimingContext
        {
            RelayType = RelayType.Stream,
            MediaKind = MediaKind.LiveTvStream,
            ChannelId = "ch-1",
            SessionId = "sess-123",
            ChannelName = "Das Erste",
            ClientName = "Infuse",
            UserAgent = "Infuse/7.6.8",
            RequestMethod = "GET",
            EffectiveProfile = "jellyfin",
            ResolutionSource = "ClientRule",
            MediaInfoCacheStatus = "hit",
            StreamSetupMs = 42.5,
            PeakBitrate = 12_000_000,
            ClientStatusCode = 200,
            EndedBy = StreamEndedBy.UpstreamEof,
        };
        ctx.MarkFirstByteToClient();

        var metric = ctx.ToMetric();

        Assert.Equal("sess-123", metric.SessionId);
        Assert.Equal("Das Erste", metric.ChannelName);
        Assert.Equal("Infuse", metric.ClientName);
        Assert.Equal("Infuse/7.6.8", metric.UserAgent);
        Assert.Equal("GET", metric.RequestMethod);
        Assert.Equal("jellyfin", metric.EffectiveProfile);
        Assert.Equal("ClientRule", metric.ResolutionSource);
        Assert.Equal("hit", metric.MediaInfoCacheStatus);
        Assert.Equal(42.5, metric.StreamSetupMs);
        Assert.Equal(12_000_000, metric.PeakBitrate);
    }

    [Fact]
    public void ToMetric_Image_DoesNotCarryStreamOnlyFields()
    {
        var ctx = new RelayTimingContext
        {
            RelayType = RelayType.Image,
            SessionId = "sess-should-not-persist",
            EffectiveProfile = "jellyfin",
            MediaInfoCacheStatus = "hit",
            PeakBitrate = 1,
            ClientStatusCode = 200,
        };

        var metric = ctx.ToMetric();

        Assert.Null(metric.SessionId);
        Assert.Null(metric.EffectiveProfile);
        Assert.Null(metric.MediaInfoCacheStatus);
        Assert.Null(metric.PeakBitrate);
        Assert.Null(metric.StreamFinalOutcome);
        Assert.Null(metric.NormalDisconnect);
    }

    [Fact]
    public void ToMetric_Stream_DerivesStreamFinalOutcomeFromEndedBy()
    {
        var ctx = new RelayTimingContext
        {
            RelayType = RelayType.Stream,
            ClientStatusCode = 200,
            EndedBy = StreamEndedBy.ClientDisconnectAfterFirstByte,
            ClientCancelled = true,
            FailureReason = RelayFailureReason.ClientCancelled,
        };
        ctx.MarkFirstByteToClient();

        var metric = ctx.ToMetric();

        Assert.Equal(nameof(StreamFinalOutcome.NormalDisconnect), metric.StreamFinalOutcome);
        Assert.True(metric.NormalDisconnect);
        Assert.Equal("cancelled", metric.FinalOutcome);
    }

    [Fact]
    public void ToMetric_StreamAbortBeforeFirstByte_ClassifiesStartupFailed()
    {
        var ctx = new RelayTimingContext
        {
            RelayType = RelayType.Stream,
            ClientStatusCode = 499,
            ClientCancelled = true,
            FailureReason = RelayFailureReason.ClientCancelled,
            EndedBy = StreamEndedBy.StartupCancelledBeforeFirstByte,
        };

        var metric = ctx.ToMetric();

        Assert.Equal(nameof(StreamFinalOutcome.StartupFailed), metric.StreamFinalOutcome);
        Assert.False(metric.NormalDisconnect);
    }

    // ── StartupFailedWithin5Seconds — corrected semantics ─────────────────

    [Fact]
    public void StartupFailed_SlowFirstByteAndFailure_IsFlagged()
    {
        var ctx = new RelayTimingContext
        {
            RelayType = RelayType.Stream,
            ClientStatusCode = 502,
            FailureReason = RelayFailureReason.Upstream5xx,
            EndedBy = StreamEndedBy.UpstreamHttpError,
        };

        // Simulate a first byte after > 5s by setting the tick directly (ticks are Stopwatch-based).
        ctx.FirstByteToClientTicks = (long)(System.Diagnostics.Stopwatch.Frequency * 6.0);

        var metric = ctx.ToMetric();

        Assert.True(metric.StartupFailedWithin5Seconds, "failure with first byte after 5s must count as startup failure");
    }

    [Fact]
    public void StartupFailed_FastFailureWithoutFirstByte_IsNotFlagged()
    {
        // An upstream 404 after a few milliseconds is a failure, but NOT a startup-latency failure.
        var ctx = new RelayTimingContext
        {
            RelayType = RelayType.Stream,
            ClientStatusCode = 404,
            FailureReason = RelayFailureReason.Upstream404,
            EndedBy = StreamEndedBy.UpstreamHttpError,
        };

        var metric = ctx.ToMetric();

        Assert.False(metric.StartupFailedWithin5Seconds);
    }

    [Fact]
    public void StartupFailed_SuccessWithSlowFirstByte_IsNotFlagged()
    {
        // The old (inverted) semantics flagged this; a successful stream is never a startup FAILURE.
        var ctx = new RelayTimingContext
        {
            RelayType = RelayType.Stream,
            ClientStatusCode = 200,
            EndedBy = StreamEndedBy.UpstreamEof,
        };
        ctx.FirstByteToClientTicks = (long)(System.Diagnostics.Stopwatch.Frequency * 6.0);

        var metric = ctx.ToMetric();

        Assert.False(metric.StartupFailedWithin5Seconds);
        Assert.True(metric.StartupLatencyMs > 5000);
    }

    [Fact]
    public void StartupFailed_QuickCancelBeforeFirstByte_IsNotFlagged()
    {
        // Zapping away within a second — no first byte, but the request did not last 5s.
        var ctx = new RelayTimingContext
        {
            RelayType = RelayType.Stream,
            ClientStatusCode = 499,
            FailureReason = RelayFailureReason.UpstreamTimeout,
            EndedBy = StreamEndedBy.UpstreamTimeout,
        };

        var metric = ctx.ToMetric();

        Assert.False(metric.StartupFailedWithin5Seconds, "a fast failure is not a startup-latency failure");
    }

    [Fact]
    public void ToMetric_CacheLookupDuration_IsPersisted()
    {
        var ctx = new RelayTimingContext
        {
            RelayType = RelayType.Image,
            CacheStatus = RelayCacheStatus.Hit,
            CacheLookupDurationMs = 1.25,
            ClientStatusCode = 200,
        };

        var metric = ctx.ToMetric();

        Assert.Equal(1.25, metric.CacheLookupDurationMs);
    }

    [Fact]
    public void ToMetric_RequestMethod_IsTakenFromContext()
    {
        var ctx = new RelayTimingContext
        {
            RelayType = RelayType.Stream,
            RequestMethod = "HEAD",
            ClientStatusCode = 200,
            EndedBy = StreamEndedBy.UpstreamEof,
        };

        var metric = ctx.ToMetric();

        Assert.Equal("HEAD", metric.RequestMethod);
    }

    [Fact]
    public void ToMetric_AverageBytesPerSecond_IsBytesNotBits()
    {
        var ctx = new RelayTimingContext { RelayType = RelayType.Stream, ClientStatusCode = 200, EndedBy = StreamEndedBy.UpstreamEof };
        ctx.BytesSent = 1_000_000;
        Thread.Sleep(20);

        var metric = ctx.ToMetric();

        // bytes/s = bytes / seconds — sanity-check the unit stays bytes (not multiplied by 8).
        Assert.NotNull(metric.AverageBytesPerSecond);
        var expected = 1_000_000 / (metric.TotalDurationMs / 1000.0);
        Assert.Equal(expected, metric.AverageBytesPerSecond!.Value, precision: 3);
    }
}
