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
    private DbContextOptions<RelayMetricsContext>? _lazyDbContextOptions;

    public RelayMetricsService(
        ILogger<RelayMetricsService> logger,
        ConfigurationProvider configProvider,
        DatabaseHealthService dbHealthService,
        DatabaseWriteCoordinator writeCoordinator,
        RelayActivityTracker activityTracker,
        DatabaseProvider databaseProvider)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _dbHealthService = dbHealthService ?? throw new ArgumentNullException(nameof(dbHealthService));
        _writeCoordinator = writeCoordinator ?? throw new ArgumentNullException(nameof(writeCoordinator));
        _activityTracker = activityTracker ?? throw new ArgumentNullException(nameof(activityTracker));
        _databaseProvider = databaseProvider ?? throw new ArgumentNullException(nameof(databaseProvider));
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
    internal RelayMetricsService(
        ILogger<RelayMetricsService> logger,
        ConfigurationProvider configProvider,
        DatabaseHealthService dbHealthService,
        DatabaseWriteCoordinator writeCoordinator,
        RelayActivityTracker activityTracker,
        DbContextOptions<RelayMetricsContext> dbContextOptions)
        : this(logger, configProvider, dbHealthService, writeCoordinator, activityTracker, CreateNullProvider())
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
        summary.SuccessfulRequests = rows.Count(r => r.FinalOutcome == "success");
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
        summary.AvgStreamSessionDurationMs = SafeAverage(streamRows, r => r.SessionDurationMs);
        summary.StreamEndedByDistribution = streamRows
            .Where(r => !string.IsNullOrEmpty(r.EndedBy))
            .GroupBy(r => r.EndedBy!)
            .ToDictionary(g => g.Key, g => (long)g.Count());

        // Image cache
        var imageRows = rows.Where(r => r.RelayType == "image").ToList();
        summary.ImageRequests = imageRows.Count;
        summary.CacheHits = imageRows.Count(r => r.CacheStatus == nameof(RelayCacheStatus.Hit));
        summary.CacheMisses = imageRows.Count(r => r.CacheStatus == nameof(RelayCacheStatus.Miss));
        summary.CacheRevalidated = imageRows.Count(r => r.CacheStatus == nameof(RelayCacheStatus.Revalidated));
        summary.NegativeCacheHits = imageRows.Count(r => r.CacheStatus == nameof(RelayCacheStatus.NegativeHit));
        summary.CacheHitRatio = summary.ImageRequests > 0
            ? Math.Round(summary.CacheHits * 100.0 / summary.ImageRequests, 1)
            : 0;
        summary.NotModified304Count = imageRows.Count(r => r.WasNotModified304);

        // Errors
        summary.TopFailureReasons = rows
            .Where(r => r.FailureReason != nameof(RelayFailureReason.None))
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

        // Slowest requests (max 20)
        summary.SlowestRequests = rows
            .OrderByDescending(r => r.TotalDurationMs)
            .Take(20)
            .Select(r => new RelaySlowestEntry
            {
                CreatedAtUtc = r.CreatedAtUtc,
                RelayType = r.RelayType,
                TotalDurationMs = r.TotalDurationMs,
                BytesSent = r.BytesSent,
                FinalOutcome = r.FinalOutcome,
                ChannelId = r.ChannelId,
            })
            .ToList();

        // Hourly trend
        summary.HourlyTrend = rows
            .GroupBy(r => new DateTime(r.CreatedAtUtc.Year, r.CreatedAtUtc.Month, r.CreatedAtUtc.Day, r.CreatedAtUtc.Hour, 0, 0, DateTimeKind.Utc))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var imgInBucket = g.Where(r => r.RelayType == "image").ToList();
                var cacheHitsInBucket = imgInBucket.Count(r => r.CacheStatus == nameof(RelayCacheStatus.Hit));
                return new RelayTrendBucket
                {
                    HourUtc = g.Key,
                    Requests = g.Count(),
                    Failures = g.Count(r => r.FinalOutcome == "failure"),
                    AvgStartupLatencyMs = SafeAverage(g.ToList(), r => r.StartupLatencyMs),
                    BytesTransferred = g.Sum(r => r.BytesSent),
                    CacheHitRatio = imgInBucket.Count > 0
                        ? Math.Round(cacheHitsInBucket * 100.0 / imgInBucket.Count, 1)
                        : null,
                };
            })
            .ToList();

        return summary;
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
        return _lazyDbContextOptions ??= _databaseProvider.CreateContextOptions<RelayMetricsContext>();
    }

    private RelayMetricsContext CreateContext() => new(GetDbContextOptions());
}
