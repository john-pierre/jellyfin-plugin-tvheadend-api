using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Statistic;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Database;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Statistic;

/// <summary>
/// Tracks live TV viewing sessions by listening to Jellyfin playback events.
/// Persists data to SQLite database in the plugin data directory.
/// Schema creation is owned by <see cref="DatabaseMigrationService"/> — this service assumes migrations have run.
/// Thread-safe: all DB access is serialized via a SemaphoreSlim.
/// </summary>
internal sealed class StatisticsService : IStatisticsService, IHostedService, IDisposable
{
    private readonly ILogger<StatisticsService> _logger;
    private readonly ISessionManager _sessionManager;
    private readonly ConfigurationProvider _configProvider;
    private readonly DatabaseHealthService _dbHealthService;
    private readonly DbContextOptions<ViewingSessionContext> _dbContextOptions;
    private readonly string _dbPath;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private Timer? _pruneTimer;
    private Timer? _stuckSessionTimer;

    public StatisticsService(
        ILogger<StatisticsService> logger,
        ISessionManager sessionManager,
        ConfigurationProvider configProvider,
        DatabaseHealthService dbHealthService,
        DbContextOptions<ViewingSessionContext> dbContextOptions,
        string dbPath)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _dbHealthService = dbHealthService ?? throw new ArgumentNullException(nameof(dbHealthService));
        _dbContextOptions = dbContextOptions ?? throw new ArgumentNullException(nameof(dbContextOptions));
        _dbPath = dbPath;
    }

    /// <inheritdoc />
    public IReadOnlyList<ViewingSession> AllSessions
    {
        get
        {
            if (!_dbHealthService.IsAvailable)
            {
                return Array.Empty<ViewingSession>();
            }

            _dbLock.Wait();
            try
            {
                using var dbContext = CreateDbContext();
                return dbContext.ViewingSessions
                    .AsNoTracking()
                    .OrderByDescending(s => s.StartTimeUtc)
                    .ToList()
                    .AsReadOnly();
            }
            finally
            {
                _dbLock.Release();
            }
        }
    }

    /// <inheritdoc />
    public ViewingStatisticsResult GetStatistics(int days)
    {
        if (!_dbHealthService.IsAvailable)
        {
            return new ViewingStatisticsResult();
        }

        _dbLock.Wait();
        try
        {
            using var dbContext = CreateDbContext();
            var cutoff = days > 0 ? DateTime.UtcNow.AddDays(-days) : DateTime.MinValue;
            var completedSessions = dbContext.ViewingSessions
                .Where(s => s.StartTimeUtc >= cutoff && s.EndTimeUtc.HasValue)
                .AsNoTracking()
                .OrderByDescending(s => s.StartTimeUtc)
                .ToList();
            var activeSessions = dbContext.ViewingSessions
                .Where(s => s.StartTimeUtc >= cutoff && !s.EndTimeUtc.HasValue)
                .AsNoTracking()
                .OrderByDescending(s => s.StartTimeUtc)
                .ToList();

            return new ViewingStatisticsResult
            {
                Sessions = completedSessions,
                ActiveSessions = activeSessions,
                TotalCount = completedSessions.Count + activeSessions.Count,
                ActiveCount = activeSessions.Count,
            };
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <inheritdoc />
    public void ClearStatistics()
    {
        if (!_dbHealthService.IsAvailable)
        {
            _logger.LogWarning("Statistics storage is unavailable; clear operation was skipped.");
            return;
        }

        _dbLock.Wait();
        try
        {
            using var dbContext = CreateDbContext();
            dbContext.ViewingSessions.RemoveRange(dbContext.ViewingSessions);
            dbContext.SaveChanges();
        }
        finally
        {
            _dbLock.Release();
        }

        _logger.LogInformation("Viewing statistics cleared.");
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Schema is initialized centrally by DatabaseHealthService before services start.
        if (!_dbHealthService.IsAvailable)
        {
            _logger.LogWarning("Statistics database is unavailable; tracking continues without persistence.");
        }

        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;

        _pruneTimer = new Timer(_ => PruneOldSessions(), null, TimeSpan.FromHours(24), TimeSpan.FromHours(24));
        _stuckSessionTimer = new Timer(_ => CloseOrphanedSessions(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        _logger.LogInformation("StatisticsService started — tracking live TV viewing sessions.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _pruneTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _stuckSessionTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        if (!_dbHealthService.IsAvailable)
        {
            _logger.LogInformation("StatisticsService stopped.");
            return;
        }

        await _dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var dbContext = CreateDbContext();
            var activeSessions = dbContext.ViewingSessions.Where(s => !s.EndTimeUtc.HasValue).ToList();
            foreach (var session in activeSessions)
            {
                session.EndTimeUtc = DateTime.UtcNow;
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _dbLock.Release();
        }

        _logger.LogInformation("StatisticsService stopped.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _pruneTimer?.Dispose();
        _stuckSessionTimer?.Dispose();
        _dbLock.Dispose();
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (!_dbHealthService.IsAvailable)
        {
            return;
        }

        if (e.Item is not LiveTvChannel)
        {
            return;
        }

        var sessionId = e.PlaySessionId ?? Guid.NewGuid().ToString("N");
        var userName = e.Users?.FirstOrDefault()?.Username ?? "Unknown";
        var playMethod = e.Session?.PlayState?.PlayMethod?.ToString() ?? "Unknown";

        var session = new ViewingSession
        {
            UserName = userName,
            DeviceName = e.DeviceName ?? "Unknown",
            ClientName = e.ClientName ?? "Unknown",
            ChannelName = e.Item.Name ?? "Unknown",
            ChannelId = e.Item.Id.ToString("N"),
            PlayMethod = playMethod,
            StartTimeUtc = DateTime.UtcNow,
            PlaySessionId = sessionId,
        };

        _dbLock.Wait();
        try
        {
            using var dbContext = CreateDbContext();
            dbContext.ViewingSessions.Add(session);
            dbContext.SaveChanges();
        }
        finally
        {
            _dbLock.Release();
        }

        _logger.LogDebug(
            "Live TV playback started: User={User}, Device={Device}, Client={Client}, Channel={Channel}, PlaySessionId={PlaySessionId}, PlayMethod={PlayMethod}",
            session.UserName,
            session.DeviceName,
            session.ClientName,
            session.ChannelName,
            sessionId,
            session.PlayMethod);
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (!_dbHealthService.IsAvailable)
        {
            return;
        }

        if (e.Item is not LiveTvChannel)
        {
            return;
        }

        var sessionId = e.PlaySessionId ?? string.Empty;
        var userName = e.Users?.FirstOrDefault()?.Username ?? "Unknown";
        var deviceName = e.DeviceName ?? "Unknown";
        var clientName = e.ClientName ?? "Unknown";
        var channelId = e.Item.Id.ToString("N");

        _logger.LogDebug(
            "Live TV playback stopped event: User={User}, Device={Device}, Client={Client}, PlaySessionId={PlaySessionId}, StoppedAt={StoppedAtMs}ms",
            userName,
            deviceName,
            clientName,
            string.IsNullOrEmpty(sessionId) ? "<empty>" : sessionId,
            e.PlaybackPositionTicks.HasValue ? TimeSpan.FromTicks(e.PlaybackPositionTicks.Value).TotalMilliseconds : 0);

        ViewingSession? session;
        _dbLock.Wait();
        try
        {
            using var dbContext = CreateDbContext();

            // Tier 1: exact match User+Device+Client+ChannelId+PlaySessionId
            session = dbContext.ViewingSessions.FirstOrDefault(s =>
                !s.EndTimeUtc.HasValue &&
                s.UserName == userName &&
                s.DeviceName == deviceName &&
                s.ClientName == clientName &&
                s.ChannelId == channelId &&
                s.PlaySessionId == sessionId);

            // Tier 2: PlaySessionId only (if provided)
            if (session == null && !string.IsNullOrEmpty(sessionId))
            {
                session = dbContext.ViewingSessions.FirstOrDefault(s =>
                    !s.EndTimeUtc.HasValue && s.PlaySessionId == sessionId);

                if (session != null)
                {
                    _logger.LogInformation(
                        "Playback stopped: PlaySessionId fallback match: Channel={Channel}",
                        session.ChannelName);
                }
            }

            // Tier 3: User+Device+Client+ChannelId (Swiftfin sends different PlaySessionId on stop)
            if (session == null)
            {
                session = dbContext.ViewingSessions
                    .Where(s =>
                        !s.EndTimeUtc.HasValue &&
                        s.UserName == userName &&
                        s.DeviceName == deviceName &&
                        s.ClientName == clientName &&
                        s.ChannelId == channelId)
                    .OrderByDescending(s => s.StartTimeUtc)
                    .FirstOrDefault();

                if (session != null)
                {
                    _logger.LogInformation(
                        "Playback stopped: User+Device+Client+Channel fallback match: User={User}, Device={Device}, Channel={Channel}",
                        session.UserName,
                        session.DeviceName,
                        session.ChannelName);
                }
            }

            if (session == null)
            {
                _logger.LogWarning(
                    "Playback stopped: no matching active session: User={User}, Device={Device}, Client={Client}, ChannelId={ChannelId}, PlaySessionId={PlaySessionId}",
                    userName,
                    deviceName,
                    clientName,
                    channelId,
                    string.IsNullOrEmpty(sessionId) ? "<empty>" : sessionId);
                return;
            }

            session.EndTimeUtc = DateTime.UtcNow;
            var currentPlayMethod = e.Session?.PlayState?.PlayMethod?.ToString();
            if (!string.IsNullOrEmpty(currentPlayMethod))
            {
                session.PlayMethod = currentPlayMethod;
            }

            dbContext.SaveChanges();
        }
        finally
        {
            _dbLock.Release();
        }

        _logger.LogDebug(
            "Live TV playback stopped: User={User}, Device={Device}, Channel={Channel}, Duration={Duration:F1}min",
            session.UserName,
            session.DeviceName,
            session.ChannelName,
            session.DurationMinutes ?? 0);
    }

    private void PruneOldSessions()
    {
        if (!_dbHealthService.IsAvailable)
        {
            return;
        }

        var retention = _configProvider.Configuration?.StatisticsRetentionPeriod ?? StatisticsRetentionPeriod.ThirtyDays;
        if (retention == StatisticsRetentionPeriod.Forever)
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-(int)retention);

        _dbLock.Wait();
        try
        {
            using var dbContext = CreateDbContext();
            var old = dbContext.ViewingSessions.Where(s => s.StartTimeUtc < cutoff).ToList();
            if (old.Count > 0)
            {
                dbContext.ViewingSessions.RemoveRange(old);
                dbContext.SaveChanges();
                _logger.LogInformation("Pruned {Count} viewing sessions older than {Days} days.", old.Count, (int)retention);
            }

            // Prune old health transitions and TVHeadend log entries with the same retention
            var oldTransitions = dbContext.HealthTransitions.Where(h => h.TimestampUtc < cutoff).ToList();
            if (oldTransitions.Count > 0)
            {
                dbContext.HealthTransitions.RemoveRange(oldTransitions);
                dbContext.SaveChanges();
                _logger.LogInformation("Pruned {Count} health transitions older than {Days} days.", oldTransitions.Count, (int)retention);
            }

            var oldLogs = dbContext.TvheadendLogEntries.Where(l => l.TimestampUtc < cutoff).ToList();
            if (oldLogs.Count > 0)
            {
                dbContext.TvheadendLogEntries.RemoveRange(oldLogs);
                dbContext.SaveChanges();
                _logger.LogInformation("Pruned {Count} TVHeadend log entries older than {Days} days.", oldLogs.Count, (int)retention);
            }
        }
        finally
        {
            _dbLock.Release();
        }
    }

    private void CloseOrphanedSessions()
    {
        if (!_dbHealthService.IsAvailable)
        {
            return;
        }

        _dbLock.Wait();
        try
        {
            using var dbContext = CreateDbContext();
            var openSessions = dbContext.ViewingSessions.Where(s => !s.EndTimeUtc.HasValue).ToList();
            if (openSessions.Count == 0)
            {
                return;
            }

            var activeKeys = _sessionManager.Sessions
                .Where(s => s.NowPlayingItem != null)
                .Select(s => (s.UserName ?? string.Empty, s.DeviceName ?? string.Empty, s.Client ?? string.Empty))
                .ToHashSet();

            var closedCount = 0;
            foreach (var session in openSessions)
            {
                if (activeKeys.Contains((session.UserName, session.DeviceName, session.ClientName)))
                {
                    continue;
                }

                session.EndTimeUtc = DateTime.UtcNow;
                closedCount++;
                _logger.LogWarning(
                    "Closed orphaned session not in Jellyfin: User={User}, Device={Device}, Client={Client}, Channel={Channel}, Duration={Duration:F1}min",
                    session.UserName,
                    session.DeviceName,
                    session.ClientName,
                    session.ChannelName,
                    session.DurationMinutes ?? 0);
            }

            if (closedCount > 0)
            {
                dbContext.SaveChanges();
                _logger.LogInformation("Closed {Count} orphaned session(s).", closedCount);
            }
        }
        finally
        {
            _dbLock.Release();
        }
    }

    private ViewingSessionContext CreateDbContext()
    {
        return new ViewingSessionContext(_dbContextOptions);
    }
}
