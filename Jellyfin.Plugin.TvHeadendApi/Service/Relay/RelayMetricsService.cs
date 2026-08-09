// Relay metrics service — persists relay request summaries and provides dashboard aggregations.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Database;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// Persists relay request metrics to SQLite and provides aggregated dashboard queries.
/// Schema creation is owned by <see cref="DatabaseMigrationService"/> — this service assumes migrations have run.
/// Thread-safe: all DB writes are serialized via <see cref="DatabaseWriteCoordinator"/>.
/// </summary>
internal sealed class RelayMetricsService : IRelayMetricsService, IHostedService, IDisposable
{
    private readonly ILogger<RelayMetricsService> _logger;
    private readonly ConfigurationProvider _configProvider;
    private readonly DatabaseHealthService _dbHealthService;
    private readonly DatabaseWriteCoordinator _writeCoordinator;
    private readonly RelayActivityTracker _activityTracker;
    private readonly DatabaseProvider _databaseProvider;
    private readonly RelayImageCache? _imageCache;
    private readonly Guide.ChannelNameCache? _channelNameCache;
    private readonly Statistic.IStatisticsService? _statisticsService;
    private readonly object _dbContextOptionsLock = new();
    private DbContextOptions<RelayMetricsContext>? _lazyDbContextOptions;

    public RelayMetricsService(
        ILogger<RelayMetricsService> logger,
        ConfigurationProvider configProvider,
        DatabaseHealthService dbHealthService,
        DatabaseWriteCoordinator writeCoordinator,
        RelayActivityTracker activityTracker,
        DatabaseProvider databaseProvider,
        RelayImageCache? imageCache = null,
        Guide.ChannelNameCache? channelNameCache = null,
        Statistic.IStatisticsService? statisticsService = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _dbHealthService = dbHealthService ?? throw new ArgumentNullException(nameof(dbHealthService));
        _writeCoordinator = writeCoordinator ?? throw new ArgumentNullException(nameof(writeCoordinator));
        _activityTracker = activityTracker ?? throw new ArgumentNullException(nameof(activityTracker));
        _databaseProvider = databaseProvider ?? throw new ArgumentNullException(nameof(databaseProvider));
        _imageCache = imageCache;
        _channelNameCache = channelNameCache;
        _statisticsService = statisticsService;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayMetricsService"/> class
    /// with pre-built context options for unit testing.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="configProvider">Configuration provider.</param>
    /// <param name="dbHealthService">Database health service.</param>
    /// <param name="writeCoordinator">Write coordinator.</param>
    /// <param name="activityTracker">Relay activity tracker.</param>
    /// <param name="dbContextOptions">Pre-built EF Core context options.</param>
    /// <param name="statisticsService">Optional viewing statistics service for the Direct Play KPI.</param>
    internal RelayMetricsService(
        ILogger<RelayMetricsService> logger,
        ConfigurationProvider configProvider,
        DatabaseHealthService dbHealthService,
        DatabaseWriteCoordinator writeCoordinator,
        RelayActivityTracker activityTracker,
        DbContextOptions<RelayMetricsContext> dbContextOptions,
        Statistic.IStatisticsService? statisticsService = null)
        : this(logger, configProvider, dbHealthService, writeCoordinator, activityTracker, CreateNullProvider(), null, null, statisticsService)
    {
        _lazyDbContextOptions = dbContextOptions;
    }

    /// <inheritdoc />
    public int ActiveStreams => _activityTracker.ActiveStreams;

    // ── IHostedService ──────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_dbHealthService.IsAvailable)
        {
            _logger.LogWarning("Relay metrics database unavailable; metrics will not be persisted.");
        }

        _logger.LogInformation("RelayMetricsService started.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("RelayMetricsService stopped.");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        // No local resources to dispose — write coordination and cleanup are centralized.
    }

    // ── IRelayMetricsService — Write ────────────────────────────────────

    /// <inheritdoc />
    public void RecordMetric(RelayRequestMetric metric)
    {
        if (!_dbHealthService.IsAvailable || metric == null)
        {
            return;
        }

        // Fire-and-forget on ThreadPool to keep relay hot path non-blocking.
        ThreadPool.QueueUserWorkItem(_ => PersistMetricSync(metric!));
    }

    /// <inheritdoc />
    public int ClearAll()
    {
        if (!_dbHealthService.IsAvailable)
        {
            return 0;
        }

        using var writeLock = _writeCoordinator.AcquireWrite();
        try
        {
            using var db = CreateContext();
            var count = db.RelayRequestMetrics.Count();
            db.RelayRequestMetrics.RemoveRange(db.RelayRequestMetrics);
            db.SaveChanges();
            _logger.LogInformation("Cleared {Count} relay metrics.", count);
            return count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear relay metrics.");
            return 0;
        }
    }

    // ── IRelayMetricsService — Read / Aggregation ───────────────────

    /// <inheritdoc />
    public RelayMetricsSummary GetSummary(int hours)
    {
        if (!_dbHealthService.IsAvailable)
        {
            return new RelayMetricsSummary { TimeRange = FormatTimeRange(hours) };
        }

        try
        {
            using var db = CreateContext();

            var cutoff = hours > 0 ? DateTime.UtcNow.AddHours(-hours) : DateTime.MinValue;
            var rows = db.RelayRequestMetrics
                .AsNoTracking()
                .Where(r => r.CreatedAtUtc >= cutoff)
                .ToList();

            return BuildSummary(rows, hours);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read relay metrics summary.");
            _dbHealthService.RecordError(ex);
            return new RelayMetricsSummary { TimeRange = FormatTimeRange(hours) };
        }
    }

    // ── Private — Persistence ───────────────────────────────────────

    private void PersistMetricSync(RelayRequestMetric metric)
    {
        using var writeLock = _writeCoordinator.AcquireWrite();
        try
        {
            using var db = CreateContext();
            db.RelayRequestMetrics.Add(metric);
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist relay metric.");
        }
    }

    // ── Private — Aggregation ───────────────────────────────────────

    private RelayMetricsSummary BuildSummary(List<RelayRequestMetric> rows, int hours)
    {
        var summary = new RelayMetricsSummary
        {
            TimeRange = FormatTimeRange(hours),
            ActiveStreams = _activityTracker.ActiveStreams,
        };

        if (rows.Count == 0)
        {
            return summary;
        }

        summary.TotalRequests = rows.Count;
        summary.SuccessfulRequests = rows.Count(r => r.FinalOutcome == "success" || r.FinalOutcome == "cancelled");
        summary.FailedRequests = rows.Count(r => r.FinalOutcome == "failure");
        summary.CancelledRequests = rows.Count(r => r.FinalOutcome == "cancelled");
        summary.SuccessRate = summary.TotalRequests > 0
            ? Math.Round(summary.SuccessfulRequests * 100.0 / summary.TotalRequests, 1)
            : 0;

        // Latency — total duration
        var durations = rows.Select(r => r.TotalDurationMs).OrderBy(d => d).ToList();
        summary.TotalDuration = ComputePercentiles(durations);

        // Latency — startup (streams only)
        var startupLatencies = rows
            .Where(r => r.StartupLatencyMs.HasValue)
            .Select(r => r.StartupLatencyMs!.Value)
            .OrderBy(d => d).ToList();
        summary.StartupLatency = ComputePercentiles(startupLatencies);

        // Upstream timing averages
        summary.AvgUpstreamHeadersMs = SafeAverage(rows, r => r.UpstreamHeadersDurationMs);
        summary.AvgFirstByteFromUpstreamMs = SafeAverage(rows, r => r.FirstByteFromUpstreamDurationMs);
        summary.AvgFirstByteToClientMs = SafeAverage(rows, r => r.FirstByteToClientDurationMs);

        // Bandwidth
        summary.TotalBytesTransferred = rows.Sum(r => r.BytesSent);
        summary.AvgBytesPerRequest = summary.TotalRequests > 0
            ? (double)summary.TotalBytesTransferred / summary.TotalRequests
            : 0;

        // Stream health
        var streamRows = rows.Where(r => r.RelayType == "stream").ToList();
        summary.StartupFailuresWithin5s = rows.Count(r => r.StartupFailedWithin5Seconds);
        summary.AvgStreamSessionDurationMs = streamRows.Count > 0
            ? Math.Round(streamRows.Average(r => r.TotalDurationMs), 1)
            : null;
        summary.StreamEndedByDistribution = streamRows
            .Where(r => !string.IsNullOrEmpty(r.EndedBy))
            .GroupBy(r => r.EndedBy!)
            .ToDictionary(g => g.Key, g => (long)g.Count());

        // Zapping: startup latency warm vs cold mediainfo cache, and per effective profile.
        summary.StartupLatencyByCacheStatus = GroupStartupLatency(
            streamRows,
            r => string.IsNullOrEmpty(r.MediaInfoCacheStatus) ? "unknown" : r.MediaInfoCacheStatus!);
        summary.StartupLatencyByProfile = GroupStartupLatency(
            streamRows,
            r => string.IsNullOrEmpty(r.EffectiveProfile) ? "(none)" : r.EffectiveProfile!);

        // Direct Play share over the last 24h from Jellyfin viewing statistics (PlayMethod).
        summary.DirectPlayPercent24h = ComputeDirectPlayPercent24h();

        // Image cache
        var imageRows = rows.Where(r => r.RelayType == "image").ToList();
        summary.ImageRequests = imageRows.Count;
        summary.CacheHits = imageRows.Count(r => r.CacheStatus == nameof(RelayCacheStatus.Hit));
        summary.CacheMisses = imageRows.Count(r => r.CacheStatus == nameof(RelayCacheStatus.Miss));
        summary.CacheHitRatio = summary.ImageRequests > 0
            ? Math.Round(summary.CacheHits * 100.0 / summary.ImageRequests, 1)
            : 0;
        summary.NotModified304Count = imageRows.Count(r => r.WasNotModified304);

        // On-disk image cache size + health (independent of the request rows).
        if (_imageCache != null)
        {
            var cacheStats = _imageCache.GetStats();
            summary.ImageCacheEnabled = cacheStats.Enabled;
            summary.ImageCacheFileCount = cacheStats.FileCount;
            summary.ImageCacheBytes = cacheStats.TotalBytes;
            summary.ImageCacheRetentionDays = cacheStats.RetentionDays;
            summary.ImageCacheErrors = cacheStats.WriteErrors + cacheStats.ReadErrors;
        }

        // Errors — exclude ClientCancelled (normal user behavior, not a failure)
        summary.TopFailureReasons = rows
            .Where(r => r.FailureReason != nameof(RelayFailureReason.None)
                     && r.FailureReason != nameof(RelayFailureReason.ClientCancelled))
            .GroupBy(r => r.FailureReason)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .ToDictionary(g => g.Key, g => (long)g.Count());

        summary.UpstreamStatusDistribution = rows
            .Where(r => r.UpstreamStatusCode.HasValue)
            .GroupBy(r => r.UpstreamStatusCode!.Value)
            .ToDictionary(g => g.Key, g => (long)g.Count());

        // Recent errors (newest first, max 20)
        summary.RecentErrors = rows
            .Where(r => r.FinalOutcome == "failure")
            .OrderByDescending(r => r.CreatedAtUtc)
            .Take(20)
            .Select(r => new RelayErrorEntry
            {
                CreatedAtUtc = r.CreatedAtUtc,
                RelayType = r.RelayType,
                FailureReason = r.FailureReason,
                UpstreamStatusCode = r.UpstreamStatusCode,
                TotalDurationMs = r.TotalDurationMs,
                ChannelId = r.ChannelId,
            })
            .ToList();

        // Slowest requests (max 20) — ranked by STARTUP latency (time-to-first-byte), the metric a
        // user actually perceives as "slow". A long-running live stream is not "slow"; its total
        // duration is just watch time. Requests that never delivered a byte (failed/timed out before
        // first byte) fall back to total duration so they still surface as slow.
        summary.SlowestRequests = rows
            .OrderByDescending(r => r.StartupLatencyMs ?? r.TotalDurationMs)
            .Take(20)
            .Select(r => new RelaySlowestEntry
            {
                CreatedAtUtc = r.CreatedAtUtc,
                RelayType = r.RelayType,
                TotalDurationMs = r.TotalDurationMs,
                StartupLatencyMs = r.StartupLatencyMs,
                BytesSent = r.BytesSent,
                FinalOutcome = r.FinalOutcome,
                ChannelId = r.ChannelId,
            })
            .ToList();

        // Request trend — adapt the bucket granularity to the selected range so a 7d/30d view does not
        // produce 168/720 hourly bars, and a 1h view is not a single bar. Driven entirely by `hours`.
        var daily = hours == 0 || hours > 48;
        summary.TrendGranularity = daily ? "day" : "hour";
        summary.HourlyTrend = rows
            .GroupBy(r => daily
                ? new DateTime(r.CreatedAtUtc.Year, r.CreatedAtUtc.Month, r.CreatedAtUtc.Day, 0, 0, 0, DateTimeKind.Utc)
                : new DateTime(r.CreatedAtUtc.Year, r.CreatedAtUtc.Month, r.CreatedAtUtc.Day, r.CreatedAtUtc.Hour, 0, 0, DateTimeKind.Utc))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var imgInBucket = g.Where(r => r.RelayType == "image").ToList();
                var cacheHitsInBucket = imgInBucket.Count(r => r.CacheStatus == nameof(RelayCacheStatus.Hit));
                return new RelayTrendBucket
                {
                    HourUtc = g.Key,
                    Requests = g.Count(),
                    StreamRequests = g.Count(r => r.RelayType == "stream"),
                    PeakConcurrentStreams = g.Max(r => r.ParallelActiveStreamCountAtStart ?? 0),
                    Failures = g.Count(r => r.FinalOutcome == "failure" && !r.ClientCancelled),
                    AvgStartupLatencyMs = SafeAverage(g.ToList(), r => r.StartupLatencyMs),
                    BytesTransferred = g.Sum(r => r.BytesSent),
                    CacheHitRatio = imgInBucket.Count > 0
                        ? Math.Round(cacheHitsInBucket * 100.0 / imgInBucket.Count, 1)
                        : null,
                };
            })
            .ToList();

        // Problem channels — channels whose stream relays failed (excluding normal client cancels),
        // worst first, with names resolved from the channel cache. Surfaces "which channels misbehave".
        var channelStreamRows = rows.Where(r => r.RelayType == "stream" && !string.IsNullOrEmpty(r.ChannelId)).ToList();
        summary.ProblemChannels = channelStreamRows
            .Where(r => r.FinalOutcome == "failure" && !r.ClientCancelled)
            .GroupBy(r => r.ChannelId!, StringComparer.Ordinal)
            .Select(g => new ProblemChannel
            {
                ChannelId = g.Key,
                ChannelName = _channelNameCache?.GetName(g.Key) ?? g.Key,
                FailedRequests = g.Count(),
                TotalRequests = channelStreamRows.Count(r => string.Equals(r.ChannelId, g.Key, StringComparison.Ordinal)),
                LastFailureReason = g.OrderByDescending(r => r.CreatedAtUtc).First().FailureReason,
            })
            .OrderByDescending(p => p.FailedRequests)
            .Take(20)
            .ToList();

        return summary;
    }

    /// <summary>
    /// Groups stream rows by the given key and computes startup latency percentiles per group.
    /// Only rows that actually delivered a first byte (StartupLatencyMs set) contribute.
    /// </summary>
    private static Dictionary<string, LatencyPercentiles> GroupStartupLatency(
        List<RelayRequestMetric> streamRows,
        Func<RelayRequestMetric, string> keySelector)
    {
        return streamRows
            .Where(r => r.StartupLatencyMs.HasValue)
            .GroupBy(keySelector, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => ComputePercentiles(g.Select(r => r.StartupLatencyMs!.Value).OrderBy(d => d).ToList()),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Computes the Direct Play share (0–100) of Live TV sessions started in the last 24 hours,
    /// from the viewing statistics (Jellyfin PlayMethod). Null when unavailable or empty.
    /// </summary>
    private double? ComputeDirectPlayPercent24h()
    {
        if (_statisticsService == null)
        {
            return null;
        }

        try
        {
            var stats = _statisticsService.GetStatistics(1);
            var sessions = stats.Sessions.Concat(stats.ActiveSessions)
                .Where(s => !string.IsNullOrEmpty(s.PlayMethod) && s.PlayMethod != "Unknown")
                .ToList();
            if (sessions.Count == 0)
            {
                return null;
            }

            var directPlay = sessions.Count(s => string.Equals(s.PlayMethod, "DirectPlay", StringComparison.OrdinalIgnoreCase));
            return Math.Round(directPlay * 100.0 / sessions.Count, 1);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to compute Direct Play percentage from viewing statistics.");
            return null;
        }
    }

    private static LatencyPercentiles ComputePercentiles(List<double> sorted)
    {
        if (sorted.Count == 0)
        {
            return new LatencyPercentiles();
        }

        return new LatencyPercentiles
        {
            Avg = Math.Round(sorted.Average(), 1),
            Median = Math.Round(Percentile(sorted, 50), 1),
            P95 = Math.Round(Percentile(sorted, 95), 1),
            P99 = Math.Round(Percentile(sorted, 99), 1),
        };
    }

    private static double Percentile(List<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var index = (percentile / 100.0) * (sorted.Count - 1);
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper)
        {
            return sorted[lower];
        }

        return sorted[lower] + ((index - lower) * (sorted[upper] - sorted[lower]));
    }

    private static double? SafeAverage(List<RelayRequestMetric> rows, Func<RelayRequestMetric, double?> selector)
    {
        var values = rows.Where(r => selector(r).HasValue).Select(r => selector(r)!.Value).ToList();
        return values.Count > 0 ? Math.Round(values.Average(), 1) : null;
    }

    private static string FormatTimeRange(int hours) => hours switch
    {
        <= 0 => "all time",
        1 => "last hour",
        24 => "last 24 hours",
        168 => "last 7 days",
        720 => "last 30 days",
        _ => $"last {hours} hours",
    };

    // ── Private — Context ────────────────────────────────────────────

    private static DatabaseProvider CreateNullProvider()
    {
        return new DatabaseProvider(new Storage.DataFolderPathProvider(() => null));
    }

    private DbContextOptions<RelayMetricsContext> GetDbContextOptions()
    {
        // Lock the lazy init: this singleton is reached concurrently from per-request metric persistence
        // (ThreadPool) and dashboard reads, so an unsynchronized ??= could build the options twice.
        lock (_dbContextOptionsLock)
        {
            return _lazyDbContextOptions ??= _databaseProvider.CreateContextOptions<RelayMetricsContext>();
        }
    }

    private RelayMetricsContext CreateContext() => new(GetDbContextOptions());
}
