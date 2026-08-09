// Background hosted service that periodically runs centralized database cleanup.

using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Database;

/// <summary>
/// Hosted service that periodically runs centralized database cleanup for all tables.
/// Replaces per-service prune timers (StatisticsService, RelayMetricsService, PluginLogService)
/// with a single coordinated cleanup schedule.
/// </summary>
internal sealed class DatabaseCleanupHostedService : IHostedService, IDisposable
{
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(24);

    private readonly DatabaseCleanupService _cleanupService;
    private readonly ConfigurationProvider _configProvider;
    private readonly ILogger<DatabaseCleanupHostedService> _logger;
    private Timer? _timer;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseCleanupHostedService"/> class.
    /// </summary>
    /// <param name="cleanupService">Central database cleanup service.</param>
    /// <param name="configProvider">Plugin configuration provider.</param>
    /// <param name="logger">Logger instance.</param>
    public DatabaseCleanupHostedService(
        DatabaseCleanupService cleanupService,
        ConfigurationProvider configProvider,
        ILogger<DatabaseCleanupHostedService> logger)
    {
        _cleanupService = cleanupService ?? throw new ArgumentNullException(nameof(cleanupService));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => RunCleanup(), null, FirstRunDelay, CleanupInterval);
        _logger.LogInformation(
            "DatabaseCleanupHostedService started — cleanup runs every {Hours} hours, first run in {Minutes} minutes.",
            CleanupInterval.TotalHours,
            FirstRunDelay.TotalMinutes);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _logger.LogInformation("DatabaseCleanupHostedService stopped.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
    }

    private void RunCleanup()
    {
        try
        {
            var config = _configProvider.Configuration;

            var statisticsRetentionDays = config?.StatisticsRetentionPeriod switch
            {
                StatisticsRetentionPeriod.Forever => 0, // 0 = skip cleanup
                _ => (int)(config?.StatisticsRetentionPeriod ?? StatisticsRetentionPeriod.ThirtyDays),
            };

            var logRetentionDays = config?.LogRetentionDays > 0 ? config.LogRetentionDays : 7;

            _cleanupService.RunCleanup(
                statisticsRetentionDays: statisticsRetentionDays,
                logRetentionDays: logRetentionDays,
                metricsRetentionDays: statisticsRetentionDays,
                healthEventRetentionDays: 30);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scheduled database cleanup failed.");
        }
    }
}
