// Tests for relay metrics — timing context, aggregation, persistence, and dashboard DTOs.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Database;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Jellyfin.Plugin.TvHeadendApi.Service.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Relay;

/// <summary>
/// Tests for relay metrics: timing context, metrics service, aggregation, and persistence.
/// </summary>
public class RelayMetricsTests
{
    // ── RelayTimingContext ───────────────────────────────────────────

    [Fact]
    public void TimingContext_ToMetric_CreatesMetricWithCorrectRelayType()
    {
        var ctx = new RelayTimingContext { RelayType = RelayType.Image, MediaKind = MediaKind.Logo };
        ctx.ClientStatusCode = 200;

        var metric = ctx.ToMetric();

        Assert.Equal("image", metric.RelayType);
        Assert.Equal("Logo", metric.MediaKind);
        Assert.Equal("success", metric.FinalOutcome);
        Assert.Equal(nameof(RelayFailureReason.None), metric.FailureReason);
    }

    [Fact]
    public void TimingContext_ToMetric_StreamType_IncludesSessionDuration()
    {
        var ctx = new RelayTimingContext { RelayType = RelayType.Stream, MediaKind = MediaKind.LiveTvStream };
        ctx.ClientStatusCode = 200;
        Thread.Sleep(10); // Ensure some duration

        var metric = ctx.ToMetric();

        Assert.Equal("stream", metric.RelayType);
        Assert.NotNull(metric.SessionDurationMs);
        Assert.True(metric.SessionDurationMs > 0);
    }

    [Fact]
    public void TimingContext_ToMetric_FailureReason_SetsOutcomeToFailure()
    {
        var ctx = new RelayTimingContext { RelayType = RelayType.Image };
        ctx.FailureReason = RelayFailureReason.UpstreamTimeout;
        ctx.UpstreamTimedOut = true;
        ctx.ClientStatusCode = 504;

        var metric = ctx.ToMetric();

        Assert.Equal("failure", metric.FinalOutcome);
        Assert.Equal(nameof(RelayFailureReason.UpstreamTimeout), metric.FailureReason);
        Assert.True(metric.UpstreamTimedOut);
    }

    [Fact]
    public void TimingContext_ToMetric_ClientCancelled_SetsOutcomeToCancelled()
    {
        var ctx = new RelayTimingContext { RelayType = RelayType.Stream };
        ctx.ClientCancelled = true;
        ctx.ClientStatusCode = 499;

        var metric = ctx.ToMetric();

        Assert.Equal("cancelled", metric.FinalOutcome);
        Assert.True(metric.ClientCancelled);
    }

    [Fact]
    public void TimingContext_ToMetric_ImageType_IncludesImageSourceType()
    {
        var ctx = new RelayTimingContext { RelayType = RelayType.Image, ImageSourceType = ImageSourceType.ChannelLogo };
        ctx.ClientStatusCode = 200;

        var metric = ctx.ToMetric();

        Assert.Equal("ChannelLogo", metric.ImageSourceType);
    }

    [Fact]
    public void TimingContext_ToMetric_StreamType_ExcludesImageSourceType()
    {
        var ctx = new RelayTimingContext { RelayType = RelayType.Stream };
        ctx.ClientStatusCode = 200;

        var metric = ctx.ToMetric();

        Assert.Null(metric.ImageSourceType);
    }

    [Fact]
    public void TimingContext_MarkMilestones_CapturesTicks()
    {
        var ctx = new RelayTimingContext { RelayType = RelayType.Image };
        ctx.MarkUpstreamHeaders();
        ctx.MarkFirstByteFromUpstream();
        ctx.MarkFirstByteToClient();
        ctx.ClientStatusCode = 200;

        var metric = ctx.ToMetric();

        Assert.NotNull(metric.UpstreamHeadersDurationMs);
        Assert.NotNull(metric.FirstByteFromUpstreamDurationMs);
        Assert.NotNull(metric.FirstByteToClientDurationMs);
        Assert.True(metric.UpstreamHeadersDurationMs >= 0);
    }

    [Fact]
    public void TimingContext_BytesSent_RecordsCorrectly()
    {
        var ctx = new RelayTimingContext { RelayType = RelayType.Image };
        ctx.BytesSent = 12345;
        ctx.ClientStatusCode = 200;

        var metric = ctx.ToMetric();

        Assert.Equal(12345, metric.BytesSent);
    }

    [Fact]
    public void TimingContext_AverageBytesPerSecond_CalculatedWhenBytesExist()
    {
        var ctx = new RelayTimingContext { RelayType = RelayType.Image };
        ctx.BytesSent = 100000;
        ctx.ClientStatusCode = 200;
        Thread.Sleep(10);

        var metric = ctx.ToMetric();

        Assert.NotNull(metric.AverageBytesPerSecond);
        Assert.True(metric.AverageBytesPerSecond > 0);
    }

    // ── RelayActivityTracker ────────────────────────────────────────

    [Fact]
    public void ActivityTracker_IncrementAndDecrement()
    {
        var tracker = new RelayActivityTracker();
        Assert.Equal(0, tracker.ActiveStreams);

        tracker.IncrementStreams();
        Assert.Equal(1, tracker.ActiveStreams);

        tracker.IncrementStreams();
        Assert.Equal(2, tracker.ActiveStreams);

        tracker.DecrementStreams();
        Assert.Equal(1, tracker.ActiveStreams);

        tracker.DecrementStreams();
        Assert.Equal(0, tracker.ActiveStreams);
    }

    [Fact]
    public void ActivityTracker_DecrementFloorAtZero()
    {
        var tracker = new RelayActivityTracker();
        var result = tracker.DecrementStreams();
        Assert.Equal(0, result);
        Assert.Equal(0, tracker.ActiveStreams);
    }

    // ── RelayMetricsService — Aggregation ───────────────────────────

    [Fact]
    public void GetSummary_EmptyDatabase_ReturnsEmptySummary()
    {
        using var sut = CreateMetricsService();

        var summary = sut.GetSummary(24);

        Assert.NotNull(summary);
        Assert.Equal(0, summary.TotalRequests);
        Assert.Equal("last 24 hours", summary.TimeRange);
    }

    [Fact]
    public void GetSummary_WithMetrics_CalculatesCorrectly()
    {
        using var sut = CreateMetricsService();

        // Record several metrics
        RecordSync(sut, CreateImageMetric(totalMs: 50, outcome: "success"));
        RecordSync(sut, CreateImageMetric(totalMs: 100, outcome: "success"));
        RecordSync(sut, CreateImageMetric(totalMs: 200, outcome: "failure", failureReason: "Upstream404"));
        RecordSync(sut, CreateStreamMetric(totalMs: 5000, outcome: "success", sessionMs: 5000));

        Thread.Sleep(200); // Wait for ThreadPool persistence

        var summary = sut.GetSummary(0); // all time

        Assert.Equal(4, summary.TotalRequests);
        Assert.Equal(3, summary.SuccessfulRequests);
        Assert.Equal(1, summary.FailedRequests);
        Assert.True(summary.SuccessRate > 0);
    }

    [Fact]
    public void GetSummary_CalculatesPercentiles()
    {
        using var sut = CreateMetricsService();

        for (int i = 1; i <= 100; i++)
        {
            RecordSync(sut, CreateImageMetric(totalMs: i, outcome: "success"));
        }

        Thread.Sleep(200);

        var summary = sut.GetSummary(0);

        Assert.True(summary.TotalDuration.Avg > 0);
        Assert.True(summary.TotalDuration.Median > 0);
        Assert.True(summary.TotalDuration.P95 >= summary.TotalDuration.Median);
        Assert.True(summary.TotalDuration.P99 >= summary.TotalDuration.P95);
    }

    [Fact]
    public void GetSummary_CacheMetrics()
    {
        using var sut = CreateMetricsService();

        RecordSync(sut, CreateImageMetric(totalMs: 10, outcome: "success", cacheStatus: "Hit"));
        RecordSync(sut, CreateImageMetric(totalMs: 50, outcome: "success", cacheStatus: "Miss"));
        RecordSync(sut, CreateImageMetric(totalMs: 5, outcome: "success", cacheStatus: "Hit"));

        Thread.Sleep(200);

        var summary = sut.GetSummary(0);

        Assert.Equal(3, summary.ImageRequests);
        Assert.Equal(2, summary.CacheHits);
        Assert.Equal(1, summary.CacheMisses);
        Assert.True(summary.CacheHitRatio > 60);
    }

    [Fact]
    public void GetSummary_FailureReasonDistribution()
    {
        using var sut = CreateMetricsService();

        RecordSync(sut, CreateImageMetric(totalMs: 10, outcome: "failure", failureReason: "Upstream404"));
        RecordSync(sut, CreateImageMetric(totalMs: 10, outcome: "failure", failureReason: "Upstream404"));
        RecordSync(sut, CreateImageMetric(totalMs: 10, outcome: "failure", failureReason: "UpstreamTimeout"));

        Thread.Sleep(200);

        var summary = sut.GetSummary(0);

        Assert.True(summary.TopFailureReasons.ContainsKey("Upstream404"));
        Assert.Equal(2, summary.TopFailureReasons["Upstream404"]);
    }

    [Fact]
    public void GetSummary_RecentErrors_SortedNewestFirst()
    {
        using var sut = CreateMetricsService();

        RecordSync(sut, CreateImageMetric(totalMs: 10, outcome: "failure", failureReason: "Upstream404"));
        Thread.Sleep(50);
        RecordSync(sut, CreateImageMetric(totalMs: 20, outcome: "failure", failureReason: "UpstreamTimeout"));

        Thread.Sleep(200);

        var summary = sut.GetSummary(0);

        Assert.Equal(2, summary.RecentErrors.Count);
        Assert.True(summary.RecentErrors[0].CreatedAtUtc >= summary.RecentErrors[1].CreatedAtUtc);
    }

    [Fact]
    public void GetSummary_SlowestRequests_SortedByDuration()
    {
        using var sut = CreateMetricsService();

        RecordSync(sut, CreateImageMetric(totalMs: 10, outcome: "success"));
        RecordSync(sut, CreateImageMetric(totalMs: 500, outcome: "success"));
        RecordSync(sut, CreateImageMetric(totalMs: 100, outcome: "success"));

        Thread.Sleep(200);

        var summary = sut.GetSummary(0);

        Assert.True(summary.SlowestRequests.Count >= 2);
        Assert.True(summary.SlowestRequests[0].TotalDurationMs >= summary.SlowestRequests[1].TotalDurationMs);
    }

    [Fact]
    public void GetSummary_StreamEndedByDistribution()
    {
        using var sut = CreateMetricsService();

        RecordSync(sut, CreateStreamMetric(totalMs: 1000, outcome: "success", endedBy: "Completed"));
        RecordSync(sut, CreateStreamMetric(totalMs: 500, outcome: "cancelled", endedBy: "ClientCancelled"));
        RecordSync(sut, CreateStreamMetric(totalMs: 2000, outcome: "success", endedBy: "Completed"));

        Thread.Sleep(200);

        var summary = sut.GetSummary(0);

        Assert.True(summary.StreamEndedByDistribution.ContainsKey("Completed"));
        Assert.Equal(2, summary.StreamEndedByDistribution["Completed"]);
    }

    [Fact]
    public void GetSummary_HourlyTrend_GroupsByHour()
    {
        using var sut = CreateMetricsService();

        RecordSync(sut, CreateImageMetric(totalMs: 10, outcome: "success"));
        RecordSync(sut, CreateImageMetric(totalMs: 20, outcome: "success"));

        Thread.Sleep(500);

        var summary = sut.GetSummary(0);

        Assert.NotEmpty(summary.HourlyTrend);
        Assert.True(summary.HourlyTrend[0].Requests >= 2);
    }

    // ── Failure Reason Classification ───────────────────────────────

    [Theory]
    [InlineData(RelayFailureReason.None, "None")]
    [InlineData(RelayFailureReason.AuthDenied, "AuthDenied")]
    [InlineData(RelayFailureReason.UpstreamTimeout, "UpstreamTimeout")]
    [InlineData(RelayFailureReason.Upstream404, "Upstream404")]
    [InlineData(RelayFailureReason.ClientCancelled, "ClientCancelled")]
    public void FailureReason_ToString_ReturnsExpected(RelayFailureReason reason, string expected)
    {
        Assert.Equal(expected, reason.ToString());
    }

    // ── Cache Status Classification ─────────────────────────────────

    [Theory]
    [InlineData(RelayCacheStatus.NotApplicable, "NotApplicable")]
    [InlineData(RelayCacheStatus.Hit, "Hit")]
    [InlineData(RelayCacheStatus.Miss, "Miss")]
    public void CacheStatus_ToString_ReturnsExpected(RelayCacheStatus status, string expected)
    {
        Assert.Equal(expected, status.ToString());
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static RelayMetricsService CreateMetricsService()
    {
        var options = new DbContextOptionsBuilder<RelayMetricsContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var dir = Path.Combine(Path.GetTempPath(), "test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var pathProvider = new DataFolderPathProvider(() => dir);
        var provider = new DatabaseProvider(pathProvider);
        var factory = new DatabaseConnectionFactory(provider);
        var migration = new DatabaseMigrationService(factory, NullLogger<DatabaseMigrationService>.Instance);
        var recovery = new DatabaseRecoveryService(provider, migration, factory, NullLogger<DatabaseRecoveryService>.Instance);
        var dbHealth = new DatabaseHealthService(provider, factory, migration, recovery, NullLogger<DatabaseHealthService>.Instance);
        dbHealth.Initialize();

        var svc = new RelayMetricsService(
            NullLogger<RelayMetricsService>.Instance,
            new ConfigurationProvider(() => null),
            dbHealth,
            new DatabaseWriteCoordinator(),
            new RelayActivityTracker(),
            options);

        // Start the service to initialize
        svc.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return svc;
    }

    private static void RecordSync(RelayMetricsService svc, RelayRequestMetric metric)
    {
        svc.RecordMetric(metric);
    }

    private static RelayRequestMetric CreateImageMetric(
        double totalMs = 50,
        string outcome = "success",
        string failureReason = "None",
        string cacheStatus = "Miss")
    {
        return new RelayRequestMetric
        {
            CreatedAtUtc = DateTime.UtcNow,
            RelayType = "image",
            MediaKind = "Logo",
            ImageSourceType = "ChannelLogo",
            TotalDurationMs = totalMs,
            BytesSent = 1024,
            ClientStatusCode = outcome == "success" ? 200 : 502,
            FinalOutcome = outcome,
            FailureReason = failureReason,
            CacheStatus = cacheStatus,
            RequestMethod = "GET",
        };
    }

    private static RelayRequestMetric CreateStreamMetric(
        double totalMs = 5000,
        string outcome = "success",
        double sessionMs = 5000,
        string? endedBy = "Completed")
    {
        return new RelayRequestMetric
        {
            CreatedAtUtc = DateTime.UtcNow,
            RelayType = "stream",
            MediaKind = "LiveTvStream",
            TotalDurationMs = totalMs,
            SessionDurationMs = sessionMs,
            StartupLatencyMs = 150,
            BytesSent = 1024 * 1024,
            ClientStatusCode = outcome == "success" ? 200 : 502,
            FinalOutcome = outcome,
            FailureReason = outcome == "success" ? "None" : "Unknown",
            CacheStatus = "NotApplicable",
            RequestMethod = "GET",
            EndedBy = endedBy,
        };
    }
}
