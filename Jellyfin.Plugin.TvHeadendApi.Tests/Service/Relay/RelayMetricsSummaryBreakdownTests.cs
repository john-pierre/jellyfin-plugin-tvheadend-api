// Tests for the zapping breakdowns in the relay metrics summary:
// startup latency warm-vs-cold cache, per-profile grouping, and the Direct Play KPI.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Jellyfin.Plugin.TvHeadendApi.Model.Statistic;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Database;
using Jellyfin.Plugin.TvHeadendApi.Service.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Statistic;
using Jellyfin.Plugin.TvHeadendApi.Service.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Relay;

/// <summary>
/// Verifies the warm-vs-cold and per-profile startup latency aggregations and the
/// Direct Play percentage sourced from viewing statistics.
/// </summary>
public sealed class RelayMetricsSummaryBreakdownTests
{
    [Fact]
    public void GetSummary_GroupsStartupLatencyByMediaInfoCacheStatus()
    {
        using var sut = CreateMetricsService(out _);

        Record(sut, StreamMetric(startupMs: 500, cacheStatus: "hit", profile: "jellyfin"));
        Record(sut, StreamMetric(startupMs: 700, cacheStatus: "hit", profile: "jellyfin"));
        Record(sut, StreamMetric(startupMs: 4200, cacheStatus: "miss", profile: "jellyfin"));
        Record(sut, StreamMetric(startupMs: 3000, cacheStatus: null, profile: null));
        WaitForMetrics(sut, 4);

        var summary = sut.GetSummary(0);

        Assert.True(summary.StartupLatencyByCacheStatus.ContainsKey("hit"));
        Assert.True(summary.StartupLatencyByCacheStatus.ContainsKey("miss"));
        Assert.True(summary.StartupLatencyByCacheStatus.ContainsKey("unknown"), "null cache status must land in the 'unknown' bucket");
        Assert.Equal(600, summary.StartupLatencyByCacheStatus["hit"].Avg, precision: 0);
        Assert.Equal(4200, summary.StartupLatencyByCacheStatus["miss"].Avg, precision: 0);
        Assert.True(
            summary.StartupLatencyByCacheStatus["hit"].P95 <= summary.StartupLatencyByCacheStatus["miss"].P95,
            "warm starts must not be slower than cold starts in this fixture");
    }

    [Fact]
    public void GetSummary_GroupsStartupLatencyByEffectiveProfile()
    {
        using var sut = CreateMetricsService(out _);

        Record(sut, StreamMetric(startupMs: 800, cacheStatus: "hit", profile: "jellyfin"));
        Record(sut, StreamMetric(startupMs: 1500, cacheStatus: "hit", profile: "pass"));
        WaitForMetrics(sut, 2);

        var summary = sut.GetSummary(0);

        Assert.Equal(800, summary.StartupLatencyByProfile["jellyfin"].Avg, precision: 0);
        Assert.Equal(1500, summary.StartupLatencyByProfile["pass"].Avg, precision: 0);
    }

    [Fact]
    public void GetSummary_ComputesDirectPlayPercent_FromViewingStatistics()
    {
        var stats = new Mock<IStatisticsService>();
        stats.Setup(x => x.GetStatistics(1)).Returns(new ViewingStatisticsResult
        {
            Sessions = new List<ViewingSession>
            {
                new() { PlayMethod = "DirectPlay" },
                new() { PlayMethod = "DirectPlay" },
                new() { PlayMethod = "Transcode" },
            },
            ActiveSessions = new List<ViewingSession>
            {
                new() { PlayMethod = "DirectPlay" },
            },
        });

        using var sut = CreateMetricsService(out _, stats.Object);
        Record(sut, StreamMetric(startupMs: 500, cacheStatus: "hit", profile: "jellyfin"));
        WaitForMetrics(sut, 1);

        var summary = sut.GetSummary(0);

        Assert.Equal(75.0, summary.DirectPlayPercent24h);
    }

    [Fact]
    public void GetSummary_DirectPlayPercent_NullWithoutStatisticsService()
    {
        using var sut = CreateMetricsService(out _);
        Record(sut, StreamMetric(startupMs: 500, cacheStatus: "hit", profile: "jellyfin"));
        WaitForMetrics(sut, 1);

        var summary = sut.GetSummary(0);

        Assert.Null(summary.DirectPlayPercent24h);
    }

    [Fact]
    public void GetSummary_AvgStreamSessionDuration_UsesTotalDuration()
    {
        using var sut = CreateMetricsService(out _);
        Record(sut, StreamMetric(startupMs: 500, cacheStatus: "hit", profile: "jellyfin", totalMs: 10_000));
        Record(sut, StreamMetric(startupMs: 500, cacheStatus: "hit", profile: "jellyfin", totalMs: 20_000));
        WaitForMetrics(sut, 2);

        var summary = sut.GetSummary(0);

        Assert.Equal(15_000, summary.AvgStreamSessionDurationMs!.Value, precision: 0);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static RelayMetricsService CreateMetricsService(out DatabaseHealthService dbHealth, IStatisticsService? statistics = null)
    {
        var options = new DbContextOptionsBuilder<RelayMetricsContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var dir = Path.Combine(Path.GetTempPath(), "tvh_sumtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var pathProvider = new DataFolderPathProvider(() => dir);
        var provider = new DatabaseProvider(pathProvider);
        var factory = new DatabaseConnectionFactory(provider);
        var migration = new DatabaseMigrationService(factory, NullLogger<DatabaseMigrationService>.Instance);
        var recovery = new DatabaseRecoveryService(provider, migration, factory, NullLogger<DatabaseRecoveryService>.Instance);
        dbHealth = new DatabaseHealthService(provider, factory, migration, recovery, NullLogger<DatabaseHealthService>.Instance);
        dbHealth.Initialize();

        return new RelayMetricsService(
            NullLogger<RelayMetricsService>.Instance,
            new ConfigurationProvider(() => null),
            dbHealth,
            new DatabaseWriteCoordinator(),
            new RelayActivityTracker(),
            options,
            statistics);
    }

    private static void Record(RelayMetricsService svc, RelayRequestMetric metric) => svc.RecordMetric(metric);

    private static void WaitForMetrics(RelayMetricsService svc, int expectedCount, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (svc.GetSummary(0).TotalRequests >= expectedCount)
            {
                return;
            }

            Thread.Sleep(50);
        }
    }

    private static RelayRequestMetric StreamMetric(double startupMs, string? cacheStatus, string? profile, double totalMs = 60_000)
    {
        return new RelayRequestMetric
        {
            CreatedAtUtc = DateTime.UtcNow,
            RelayType = "stream",
            MediaKind = "LiveTvStream",
            ChannelId = "ch-1",
            TotalDurationMs = totalMs,
            StartupLatencyMs = startupMs,
            BytesSent = 1024 * 1024,
            ClientStatusCode = 200,
            FinalOutcome = "success",
            FailureReason = "None",
            CacheStatus = "NotApplicable",
            RequestMethod = "GET",
            EndedBy = "ClientDisconnectAfterFirstByte",
            StreamFinalOutcome = "NormalDisconnect",
            NormalDisconnect = true,
            MediaInfoCacheStatus = cacheStatus,
            EffectiveProfile = profile,
        };
    }
}
