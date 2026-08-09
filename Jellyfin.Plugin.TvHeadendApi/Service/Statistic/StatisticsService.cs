using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Statistic;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Database;
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
/// Thread-safe: all DB writes are serialized via <see cref="DatabaseWriteCoordinator"/>.
/// </summary>
internal sealed class StatisticsService : IStatisticsService, IHostedService, IDisposable
{
    private readonly ILogger<StatisticsService> _logger;
    private readonly ISessionManager _sessionManager;
    private readonly ConfigurationProvider _configProvider;
    private readonly DatabaseHealthService _dbHealthService;
    private readonly DatabaseWriteCoordinator _writeCoordinator;
    private readonly DatabaseProvider _databaseProvider;
    private readonly object _dbContextOptionsLock = new();
    private DbContextOptions<ViewingSessionContext>? _lazyDbContextOptions;
    private Timer? _stuckSessionTimer;

    public StatisticsService(
        ILogger<StatisticsService> logger,
        ISessionManager sessionManager,
        ConfigurationProvider configProvider,
        DatabaseHealthService dbHealthService,
        DatabaseWriteCoordinator writeCoordinator,
        DatabaseProvider databaseProvider)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _dbHealthService = dbHealthService ?? throw new ArgumentNullException(nameof(dbHealthService));
        _writeCoordinator = writeCoordinator ?? throw new ArgumentNullException(nameof(writeCoordinator));
        _databaseProvider = databaseProvider ?? throw new ArgumentNullException(nameof(databaseProvider));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StatisticsService"/> class
    /// with pre-built context options for unit testing.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="sessionManager">Session manager instance.</param>
    /// <param name="configProvider">Configuration provider.</param>
    /// <param name="dbHealthService">Database health service.</param>
    /// <param name="writeCoordinator">Write coordinator.</param>
    /// <param name="dbContextOptions">Pre-built EF Core context options.</param>
    internal StatisticsService(
        ILogger<StatisticsService> logger,
        ISessionManager sessionManager,
        ConfigurationProvider configProvider,
        DatabaseHealthService dbHealthService,
        DatabaseWriteCoordinator writeCoordinator,
        DbContextOptions<ViewingSessionContext> dbContextOptions)
        : this(logger, sessionManager, configProvider, dbHealthService, writeCoordinator, CreateNullProvider())
    {
        _lazyDbContextOptions = dbContextOptions;
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

            try
            {
                using var dbContext = CreateDbContext();
                return dbContext.ViewingSessions
                    .AsNoTracking()
                    .OrderByDescending(s => s.StartTimeUtc)
                    .ToList()
                    .AsReadOnly();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read viewing sessions.");
                _dbHealthService.RecordError(ex);
                return Array.Empty<ViewingSession>();
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read viewing statistics.");
            _dbHealthService.RecordError(ex);
            return new ViewingStatisticsResult();
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

        using var writeLock = _writeCoordinator.AcquireWrite();
        using var dbContext = CreateDbContext();
        dbContext.ViewingSessions.RemoveRange(dbContext.ViewingSessions);
        dbContext.SaveChanges();

        _logger.LogInformation("Viewing statistics cleared.");
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_dbHealthService.IsAvailable)
        {
            _logger.LogWarning("Statistics database is unavailable; tracking continues without persistence.");
        }

        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;

        _stuckSessionTimer = new Timer(_ => CloseOrphanedSessions(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        _logger.LogInformation("StatisticsService started — tracking live TV viewing sessions.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _stuckSessionTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        if (!_dbHealthService.IsAvailable)
        {
            _logger.LogInformation("StatisticsService stopped.");
            return;
        }

        try
        {
            using var writeLock = await _writeCoordinator.AcquireWriteAsync(cancellationToken).ConfigureAwait(false);
            using var dbContext = CreateDbContext();
            var activeSessions = dbContext.ViewingSessions.Where(s => !s.EndTimeUtc.HasValue).ToList();
            foreach (var session in activeSessions)
            {
                session.EndTimeUtc = DateTime.UtcNow;
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to close active sessions during shutdown.");
        }

        _logger.LogInformation("StatisticsService stopped.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stuckSessionTimer?.Dispose();
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
        var deviceName = e.DeviceName ?? "Unknown";
        var clientName = e.ClientName ?? "Unknown";
        var channelName = e.Item.Name ?? "Unknown";
        var channelId = e.Item.Id.ToString("N");

        try
        {
            using var writeLock = _writeCoordinator.AcquireWrite();
            using var dbContext = CreateDbContext();

            // A device/client plays one live stream at a time, so a new start implies any
            // session still open for the same user/device/client has ended. Clients zapping
            // channels do not always send a stop for the previous channel, so close such
            // rows here to keep them from lingering and inflating viewing statistics.
            var openSessions = dbContext.ViewingSessions
                .Where(s => !s.EndTimeUtc.HasValue &&
                    s.UserName == userName &&
                    s.DeviceName == deviceName &&
                    s.ClientName == clientName)
                .ToList();

            var alreadyTracked = false;
            foreach (var openSession in openSessions)
            {
                if (openSession.ChannelId == channelId && openSession.PlaySessionId == sessionId)
                {
                    // The same playback was re-announced (client retry) — keep the existing
                    // open row instead of inserting a duplicate of the unique composite key.
                    alreadyTracked = true;
                    continue;
                }

                openSession.EndTimeUtc = DateTime.UtcNow;
                _logger.LogDebug(
                    "Closed previous open session on new playback start: User={User}, Device={Device}, Channel={Channel}",
                    openSession.UserName,
                    openSession.DeviceName,
                    openSession.ChannelName);
            }

            if (!alreadyTracked)
            {
                dbContext.ViewingSessions.Add(new ViewingSession
                {
                    UserName = userName,
                    DeviceName = deviceName,
                    ClientName = clientName,
                    ChannelName = channelName,
                    ChannelId = channelId,
                    PlayMethod = playMethod,
                    StartTimeUtc = DateTime.UtcNow,
                    PlaySessionId = sessionId,
                });
            }

            dbContext.SaveChanges();

            _logger.LogDebug(
                "Live TV playback started: User={User}, Device={Device}, Client={Client}, Channel={Channel}, PlaySessionId={PlaySessionId}, PlayMethod={PlayMethod}",
                userName,
                deviceName,
                clientName,
                channelName,
                sessionId,
                playMethod);
        }
        catch (Exception ex)
        {
            // Never throw out of a Jellyfin playback event handler — log and degrade.
            _logger.LogWarning(ex, "Failed to record live TV playback start for PlaySessionId={PlaySessionId}.", sessionId);
            _dbHealthService.RecordError(ex);
        }
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

        try
        {
            using var writeLock = _writeCoordinator.AcquireWrite();
            using var dbContext = CreateDbContext();

            // Tier 1: exact match User+Device+Client+ChannelId+PlaySessionId
            var session = dbContext.ViewingSessions.FirstOrDefault(s =>
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

            _logger.LogDebug(
                "Live TV playback stopped: User={User}, Device={Device}, Channel={Channel}, Duration={Duration:F1}min",
                session.UserName,
                session.DeviceName,
                session.ChannelName,
                session.DurationMinutes ?? 0);
        }
        catch (Exception ex)
        {
            // Never throw out of a Jellyfin playback event handler — log and degrade.
            _logger.LogWarning(
                ex,
                "Failed to record live TV playback stop for PlaySessionId={PlaySessionId}.",
                string.IsNullOrEmpty(sessionId) ? "<empty>" : sessionId);
            _dbHealthService.RecordError(ex);
        }
    }

    /// <summary>
    /// Closes open viewing sessions whose user/device/client is no longer playing the recorded
    /// channel in Jellyfin. The key includes the channel so that a stale row left behind by a
    /// channel switch is reaped even while the same device is actively watching another channel.
    /// Normally invoked by the hourly timer; exposed internally for testing.
    /// </summary>
    internal void CloseOrphanedSessions()
    {
        try
        {
            if (!_dbHealthService.IsAvailable)
            {
                return;
            }

            using var writeLock = _writeCoordinator.AcquireWrite();
            using var dbContext = CreateDbContext();
            var openSessions = dbContext.ViewingSessions.Where(s => !s.EndTimeUtc.HasValue).ToList();
            if (openSessions.Count == 0)
            {
                return;
            }

            var activeKeys = _sessionManager.Sessions
                .Where(s => s.NowPlayingItem != null)
                .Select(s => (
                    User: s.UserName ?? string.Empty,
                    Device: s.DeviceName ?? string.Empty,
                    Client: s.Client ?? string.Empty,
                    ChannelId: s.NowPlayingItem?.Id.ToString("N") ?? string.Empty))
                .ToHashSet();

            var closedCount = 0;
            foreach (var session in openSessions)
            {
                if (activeKeys.Contains((session.UserName, session.DeviceName, session.ClientName, session.ChannelId)))
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
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to close orphaned sessions — database may be temporarily unavailable.");
        }
    }

    private static DatabaseProvider CreateNullProvider()
    {
        return new DatabaseProvider(new Storage.DataFolderPathProvider(() => null));
    }

    private DbContextOptions<ViewingSessionContext> GetDbContextOptions()
    {
        // Lock the lazy init: this singleton is reached concurrently from playback events (write path)
        // and dashboard reads (no write lock), so an unsynchronized ??= could build the options twice.
        lock (_dbContextOptionsLock)
        {
            return _lazyDbContextOptions ??= _databaseProvider.CreateContextOptions<ViewingSessionContext>();
        }
    }

    private ViewingSessionContext CreateDbContext()
    {
        return new ViewingSessionContext(GetDbContextOptions());
    }
}
