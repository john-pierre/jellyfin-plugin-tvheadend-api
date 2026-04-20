using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Statistics;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Statistics;

/// <summary>
/// Tracks live TV viewing sessions by listening to Jellyfin playback events.
/// Persists data to SQLite database in the plugin data directory.
/// Thread-safe: all DB access is serialized via a SemaphoreSlim.
/// </summary>
internal sealed class StatisticsService : IStatisticsService, IHostedService, IDisposable
{
    private readonly ILogger<StatisticsService> _logger;
    private readonly ISessionManager _sessionManager;
    private readonly PluginConfigurationProvider _configProvider;
    private readonly DbContextOptions<ViewingSessionContext> _dbContextOptions;
    private readonly string _dbPath;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private Timer? _pruneTimer;
    private Timer? _stuckSessionTimer;
    private bool _databaseAvailable = true;
    private bool _schemaInitialized;

    public StatisticsService(
        ILogger<StatisticsService> logger,
        ISessionManager sessionManager,
        PluginConfigurationProvider configProvider,
        DbContextOptions<ViewingSessionContext> dbContextOptions,
        string dbPath)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _dbContextOptions = dbContextOptions ?? throw new ArgumentNullException(nameof(dbContextOptions));
        _dbPath = dbPath;
    }

    /// <inheritdoc />
    public IReadOnlyList<ViewingSession> AllSessions
    {
        get
        {
            if (!_databaseAvailable)
            {
                return Array.Empty<ViewingSession>();
            }

            _dbLock.Wait();
            try
            {
                using var dbContext = CreateDbContext();
                EnsureSchemaLocked(dbContext);
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
        if (!_databaseAvailable)
        {
            return new ViewingStatisticsResult();
        }

        _dbLock.Wait();
        try
        {
            using var dbContext = CreateDbContext();
            EnsureSchemaLocked(dbContext);
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
        if (!_databaseAvailable)
        {
            _logger.LogWarning("Statistics storage is unavailable; clear operation was skipped.");
            return;
        }

        _dbLock.Wait();
        try
        {
            using var dbContext = CreateDbContext();
            EnsureSchemaLocked(dbContext);
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
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _databaseAvailable = await InitializeDatabaseAsync(cancellationToken).ConfigureAwait(false);
            if (!_databaseAvailable)
            {
                _logger.LogWarning("Statistics database is unavailable; tracking continues without persistence.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize statistics database at {Path}.", _dbPath);
            _databaseAvailable = false;
        }
        finally
        {
            _dbLock.Release();
        }

        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;

        _pruneTimer = new Timer(_ => PruneOldSessions(), null, TimeSpan.FromHours(24), TimeSpan.FromHours(24));
        _stuckSessionTimer = new Timer(_ => CloseOrphanedSessions(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        _logger.LogInformation("StatisticsService started — tracking live TV viewing sessions.");
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _pruneTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _stuckSessionTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        if (!_databaseAvailable)
        {
            _logger.LogInformation("StatisticsService stopped.");
            return;
        }

        await _dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var dbContext = CreateDbContext();
            EnsureSchemaLocked(dbContext);
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
        if (!_databaseAvailable)
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
            EnsureSchemaLocked(dbContext);
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
        if (!_databaseAvailable)
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
            EnsureSchemaLocked(dbContext);

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
        if (!_databaseAvailable)
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
            EnsureSchemaLocked(dbContext);
            var old = dbContext.ViewingSessions.Where(s => s.StartTimeUtc < cutoff).ToList();
            if (old.Count > 0)
            {
                dbContext.ViewingSessions.RemoveRange(old);
                dbContext.SaveChanges();
                _logger.LogInformation("Pruned {Count} viewing sessions older than {Days} days.", old.Count, (int)retention);
            }
        }
        finally
        {
            _dbLock.Release();
        }
    }

    private void CloseOrphanedSessions()
    {
        if (!_databaseAvailable)
        {
            return;
        }

        _dbLock.Wait();
        try
        {
            using var dbContext = CreateDbContext();
            EnsureSchemaLocked(dbContext);
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

    /// <summary>
    /// Ensures the schema exists. Must be called while holding <see cref="_dbLock"/>.
    /// Resets the flag if the DB file was deleted at runtime.
    /// </summary>
    private void EnsureSchemaLocked(ViewingSessionContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        if (!_databaseAvailable)
        {
            return;
        }

        // If the file was deleted at runtime, recreate it.
        if (_schemaInitialized && !string.IsNullOrWhiteSpace(_dbPath) && !File.Exists(_dbPath))
        {
            _logger.LogWarning("Statistics DB file deleted at runtime. Recreating at {Path}.", _dbPath);
            _schemaInitialized = false;
        }

        if (_schemaInitialized)
        {
            return;
        }

        EnsureDirectoryExists();

        // EnsureCreated() only creates tables when the database file is brand-new.
        // For pre-existing database files that are missing tables (e.g. from a previous
        // failed or incomplete startup), EF Core silently skips schema creation.
        // We therefore always apply the DDL via CREATE TABLE IF NOT EXISTS so that the
        // schema is guaranteed regardless of whether the file already existed.
        dbContext.Database.EnsureCreated();

        // ExecuteSqlRaw is only supported by relational providers (SQLite).
        // Skip for in-memory provider used in tests.
        if (dbContext.Database.IsRelational())
        {
            ApplySchemaIfMissing(dbContext);
        }

        _schemaInitialized = true;
        _logger.LogDebug("Statistics database schema verified at {Path}.", _dbPath);
    }

    /// <summary>
    /// Executes idempotent DDL to create missing tables and indexes.
    /// Safe to call on both new and pre-existing databases.
    /// </summary>
    private void ApplySchemaIfMissing(ViewingSessionContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        dbContext.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "ViewingSessions" (
                "Id"            INTEGER NOT NULL CONSTRAINT "PK_ViewingSessions" PRIMARY KEY AUTOINCREMENT,
                "UserName"      TEXT    NOT NULL,
                "DeviceName"    TEXT    NOT NULL,
                "ClientName"    TEXT    NOT NULL,
                "ChannelName"   TEXT    NOT NULL,
                "ChannelId"     TEXT    NOT NULL,
                "PlayMethod"    TEXT    NOT NULL,
                "PlaySessionId" TEXT    NOT NULL,
                "StartTimeUtc"  TEXT    NOT NULL,
                "EndTimeUtc"    TEXT    NULL
            )
            """);

        dbContext.Database.ExecuteSqlRaw("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ViewingSession_Composite"
                ON "ViewingSessions" ("UserName", "DeviceName", "ClientName", "ChannelId", "PlaySessionId")
            """);

        dbContext.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS "IX_ViewingSessions_UserName"    ON "ViewingSessions" ("UserName")
            """);

        dbContext.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS "IX_ViewingSessions_DeviceName"  ON "ViewingSessions" ("DeviceName")
            """);

        dbContext.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS "IX_ViewingSessions_StartTimeUtc" ON "ViewingSessions" ("StartTimeUtc")
            """);

        dbContext.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS "IX_ViewingSessions_EndTimeUtc"  ON "ViewingSessions" ("EndTimeUtc")
            """);
    }

    private ViewingSessionContext CreateDbContext()
    {
        return new ViewingSessionContext(_dbContextOptions);
    }

    private void EnsureDirectoryExists()
    {
        if (string.IsNullOrWhiteSpace(_dbPath))
        {
            return;
        }

        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            _logger.LogInformation("Created statistics database directory: {Dir}", dir);
        }
    }

    private async Task<bool> InitializeDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await InitializeSchemaAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Statistics database initialized at {Path}.", _dbPath);
            return true;
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "Failed to initialize statistics database at {Path}.", _dbPath);
            if (!await TryRecoverDatabaseAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            _logger.LogInformation("Statistics database recovered at {Path}.", _dbPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize statistics database at {Path}.", _dbPath);
            return false;
        }
    }

    private async Task InitializeSchemaAsync(CancellationToken cancellationToken)
    {
        EnsureDirectoryExists();
        using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        if (dbContext.Database.IsRelational())
        {
            ApplySchemaIfMissing(dbContext);
        }

        _schemaInitialized = true;
    }

    private async Task<bool> TryRecoverDatabaseAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_dbPath))
        {
            return false;
        }

        try
        {
            EnsureDirectoryExists();
            if (File.Exists(_dbPath))
            {
                var backupPath = _dbPath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
                File.Move(_dbPath, backupPath, true);
                _logger.LogWarning("Moved unreadable statistics database to {BackupPath}.", backupPath);
            }

            DeleteIfExists(_dbPath + "-wal");
            DeleteIfExists(_dbPath + "-shm");
            _schemaInitialized = false;

            await InitializeSchemaAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Statistics database recovery failed at {Path}.", _dbPath);
            return false;
        }
    }

    private static void DeleteIfExists(string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }
}
