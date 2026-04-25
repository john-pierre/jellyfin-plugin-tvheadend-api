using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Statistics;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Jellyfin.Plugin.TvHeadendApi.Service.Statistics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Logging;

/// <summary>
/// Manages plugin log persistence to SQLite and provides query capabilities for the dashboard.
/// Uses a bounded in-memory queue to decouple log writers from DB I/O and prevent blocking hot paths.
/// Also handles log retention cleanup.
/// </summary>
internal sealed class PluginLogService : IPluginLogQueryService, IHostedService, IDisposable
{
    private const int MaxQueueSize = 2000;
    private const int FlushBatchSize = 50;
    private const int MaxMessageLength = 4096;
    private const int MaxExceptionLength = 8192;

    private readonly ILogger<PluginLogService> _logger;
    private readonly PluginConfigurationProvider _configProvider;
    private readonly DbContextOptions<ViewingSessionContext> _dbContextOptions;

    private readonly BlockingCollection<PluginLogEntry> _queue =
        new(new ConcurrentQueue<PluginLogEntry>(), MaxQueueSize);

    private CancellationTokenSource? _cts;
    private Task? _writerTask;
    private Task? _cleanupTask;
    private volatile bool _dbLoggingFailed;
    private volatile bool _databaseReady;

    public PluginLogService(
        ILogger<PluginLogService> logger,
        PluginConfigurationProvider configProvider,
        DbContextOptions<ViewingSessionContext> dbContextOptions)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _dbContextOptions = dbContextOptions;
    }

    /// <summary>
    /// Enqueues a plugin-originated log entry for async persistence.
    /// Non-blocking: drops the entry if the queue is full.
    /// </summary>
    /// <param name="level">The log level string (e.g. "Information").</param>
    /// <param name="category">The logger category (class name).</param>
    /// <param name="message">The log message text.</param>
    /// <param name="exception">Optional exception details.</param>
    /// <param name="eventId">Optional event ID.</param>
    /// <param name="correlationId">Optional correlation ID.</param>
    /// <param name="channelId">Optional channel ID.</param>
    public void EnqueuePluginLog(
        string level,
        string category,
        string message,
        string? exception = null,
        string? eventId = null,
        string? correlationId = null,
        string? channelId = null)
    {
        var config = _configProvider.Configuration;
        if (config is not { StorePluginLogsInSqlite: true })
        {
            return;
        }

        var sanitizedMessage = config.EnableDebugLogSanitization
            ? LogSanitizer.Sanitize(message)
            : message;

        var logType = level.ToLowerInvariant() switch
        {
            "trace" => "trace",
            "debug" => "debug",
            "information" => "information",
            "warning" => "warning",
            "error" => "error",
            "critical" => "critical",
            _ => "information",
        };

        var entry = new PluginLogEntry
        {
            CreatedAtUtc = DateTime.UtcNow,
            Source = "plugin",
            LogType = logType,
            Level = level,
            Category = Truncate(category, 256) ?? string.Empty,
            Message = Truncate(sanitizedMessage, MaxMessageLength) ?? string.Empty,
            Exception = exception != null ? Truncate(exception, MaxExceptionLength) : null,
            EventId = eventId,
            CorrelationId = correlationId,
            ChannelId = channelId,
        };

        // Non-blocking enqueue — drop if full
        _queue.TryAdd(entry);
    }

    /// <summary>
    /// Enqueues a TVHeadend-originated log entry for async persistence.
    /// Parses the raw log line to extract timestamp, level, category, and clean message.
    /// </summary>
    /// <param name="rawLine">The raw TVHeadend log line.</param>
    public void EnqueueTvHeadendLog(string rawLine)
    {
        var config = _configProvider.Configuration;
        if (config is not { StoreTvHeadendLogsInSqlite: true, TvHeadendLogImportEnabled: true })
        {
            return;
        }

        var parsed = TvHeadendLogParser.Parse(rawLine);

        var sanitizedMessage = config.EnableDebugLogSanitization
            ? LogSanitizer.Sanitize(parsed.MessageWithoutTimestamp)
            : parsed.MessageWithoutTimestamp;

        var entry = new PluginLogEntry
        {
            CreatedAtUtc = parsed.ParsedTimestampUtc ?? DateTime.UtcNow,
            Source = "tvheadend",
            LogType = parsed.LogType,
            Level = parsed.Level,
            Category = parsed.Category,
            Message = Truncate(sanitizedMessage, MaxMessageLength) ?? string.Empty,
            RawSource = Truncate(rawLine, MaxMessageLength),
            RawLineHash = parsed.OriginalLineHash,
            ImportedAtUtc = DateTime.UtcNow,
        };

        _queue.TryAdd(entry);
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginLogEntry> QueryLogs(
        string? source = null,
        string? level = null,
        string? logType = null,
        string? search = null,
        int limit = 500,
        string sortField = "created_at_utc",
        string sortDirection = "desc")
    {
        if (!_databaseReady)
        {
            return Array.Empty<PluginLogEntry>();
        }

        try
        {
            using var db = new ViewingSessionContext(_dbContextOptions);
            IQueryable<PluginLogEntry> query = db.PluginLogEntries.AsNoTracking();

            if (!string.IsNullOrEmpty(source))
            {
                query = query.Where(e => e.Source == source);
            }

            if (!string.IsNullOrEmpty(level))
            {
                query = query.Where(e => e.Level == level);
            }

            if (!string.IsNullOrEmpty(logType))
            {
                query = query.Where(e => e.LogType == logType);
            }

            if (!string.IsNullOrEmpty(search))
            {
                query = query.Where(e =>
                    e.Message.Contains(search) ||
                    (e.Category != null && e.Category.Contains(search)));
            }

            query = ApplySort(query, sortField, sortDirection);

            return query.Take(Math.Clamp(limit, 1, 5000)).ToList().AsReadOnly();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query plugin log entries from SQLite");
            return Array.Empty<PluginLogEntry>();
        }
    }

    /// <inheritdoc />
    public int GetTotalCount(string? source = null, string? level = null, string? logType = null, string? search = null)
    {
        if (!_databaseReady)
        {
            return 0;
        }

        try
        {
            using var db = new ViewingSessionContext(_dbContextOptions);
            IQueryable<PluginLogEntry> query = db.PluginLogEntries.AsNoTracking();

            if (!string.IsNullOrEmpty(source))
            {
                query = query.Where(e => e.Source == source);
            }

            if (!string.IsNullOrEmpty(level))
            {
                query = query.Where(e => e.Level == level);
            }

            if (!string.IsNullOrEmpty(logType))
            {
                query = query.Where(e => e.LogType == logType);
            }

            if (!string.IsNullOrEmpty(search))
            {
                query = query.Where(e =>
                    e.Message.Contains(search) ||
                    (e.Category != null && e.Category.Contains(search)));
            }

            return query.Count();
        }
        catch
        {
            return 0;
        }
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        EnsureDatabase();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _writerTask = Task.Run(() => WriteLoopAsync(_cts.Token), _cts.Token);
        _cleanupTask = Task.Run(() => CleanupLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        _queue.CompleteAdding();
        if (_cts != null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_writerTask != null)
        {
            try
            {
                await _writerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        if (_cleanupTask != null)
        {
            try
            {
                await _cleanupTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        _cts?.Dispose();
    }

    public void Dispose()
    {
        _queue.Dispose();
        _cts?.Dispose();
    }

    private void EnsureDatabase()
    {
        try
        {
            using var db = new ViewingSessionContext(_dbContextOptions);

            // EnsureCreated is all-or-nothing: if the DB file already exists
            // (e.g. from an earlier schema without plugin_log_entries), it does nothing.
            // We call it first for fresh installs, then explicitly create the table
            // if it is still missing after an upgrade.
            db.Database.EnsureCreated();
            EnsurePluginLogTable(db);
            _databaseReady = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure plugin log database schema");
        }
    }

    /// <summary>
    /// Creates the <c>plugin_log_entries</c> table and its indexes via raw SQL
    /// when the table does not yet exist. This handles the case where the SQLite
    /// database was created by an earlier plugin version that did not include
    /// the unified log table.
    /// </summary>
    private void EnsurePluginLogTable(ViewingSessionContext db)
    {
        const string checkSql =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='plugin_log_entries';";

        using var cmd = db.Database.GetDbConnection().CreateCommand();
        db.Database.OpenConnection();
        cmd.CommandText = checkSql;
        var exists = Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;

        if (exists)
        {
            return;
        }

        _logger.LogInformation("Creating missing plugin_log_entries table in existing database");

        const string createTableSql = """
            CREATE TABLE plugin_log_entries (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                CreatedAtUtc    TEXT    NOT NULL,
                Source          TEXT    NOT NULL,
                LogType         TEXT    NOT NULL,
                Level           TEXT    NOT NULL,
                Category        TEXT,
                Message         TEXT    NOT NULL,
                Exception       TEXT,
                EventId         TEXT,
                CorrelationId   TEXT,
                ChannelId       TEXT,
                RawSource       TEXT,
                RawLineHash     TEXT,
                ImportedAtUtc   TEXT
            );

            CREATE INDEX IX_plugin_log_created_at     ON plugin_log_entries (CreatedAtUtc);
            CREATE INDEX IX_plugin_log_source          ON plugin_log_entries (Source);
            CREATE INDEX IX_plugin_log_level            ON plugin_log_entries (Level);
            CREATE INDEX IX_plugin_log_type             ON plugin_log_entries (LogType);
            CREATE INDEX IX_plugin_log_category         ON plugin_log_entries (Category);
            CREATE INDEX IX_plugin_log_source_created   ON plugin_log_entries (Source, CreatedAtUtc);
            CREATE INDEX IX_plugin_log_level_created    ON plugin_log_entries (Level, CreatedAtUtc);
            CREATE INDEX IX_plugin_log_raw_hash         ON plugin_log_entries (RawLineHash);
            """;

        db.Database.ExecuteSqlRaw(createTableSql);
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        var batch = new List<PluginLogEntry>(FlushBatchSize);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Block until at least one item is available or cancelled
                if (_queue.TryTake(out var first, Timeout.Infinite, ct))
                {
                    batch.Add(first);

                    // Drain up to batch size
                    while (batch.Count < FlushBatchSize && _queue.TryTake(out var next))
                    {
                        batch.Add(next);
                    }

                    await FlushBatchAsync(batch).ConfigureAwait(false);
                    batch.Clear();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                // Queue completed adding
                break;
            }
        }

        // Drain remaining items on shutdown
        while (_queue.TryTake(out var remaining))
        {
            batch.Add(remaining);
            if (batch.Count >= FlushBatchSize)
            {
                await FlushBatchAsync(batch).ConfigureAwait(false);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await FlushBatchAsync(batch).ConfigureAwait(false);
        }
    }

    private async Task FlushBatchAsync(List<PluginLogEntry> batch)
    {
        if (_dbLoggingFailed)
        {
            return;
        }

        try
        {
            using var db = new ViewingSessionContext(_dbContextOptions);
            db.PluginLogEntries.AddRange(batch);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Log one warning through Jellyfin's normal logger, then disable DB logging
            // to prevent recursion or repeated failures
            _dbLoggingFailed = true;
            _logger.LogWarning(ex, "Failed to persist plugin log batch to SQLite — DB logging disabled for this session");
        }
    }

    private async Task CleanupLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(1), ct).ConfigureAwait(false);
                RunRetentionCleanup();
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void RunRetentionCleanup()
    {
        try
        {
            var config = _configProvider.Configuration;
            var retentionDays = config?.LogRetentionDays > 0 ? config.LogRetentionDays : 7;
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

            using var db = new ViewingSessionContext(_dbContextOptions);
            var oldEntries = db.PluginLogEntries.Where(e => e.CreatedAtUtc < cutoff);
            db.PluginLogEntries.RemoveRange(oldEntries);
            var deleted = db.SaveChanges();

            if (deleted > 0)
            {
                _logger.LogInformation("Plugin log cleanup: removed {Count} entries older than {Days} days", deleted, retentionDays);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Plugin log retention cleanup failed");
        }
    }

    private static IQueryable<PluginLogEntry> ApplySort(
        IQueryable<PluginLogEntry> query,
        string sortField,
        string sortDirection)
    {
        var desc = string.Equals(sortDirection, "desc", StringComparison.OrdinalIgnoreCase);

        return sortField.ToLowerInvariant() switch
        {
            "source" => desc ? query.OrderByDescending(e => e.Source) : query.OrderBy(e => e.Source),
            "level" => desc ? query.OrderByDescending(e => e.Level) : query.OrderBy(e => e.Level),
            "log_type" or "logtype" or "type" => desc ? query.OrderByDescending(e => e.LogType) : query.OrderBy(e => e.LogType),
            "category" => desc ? query.OrderByDescending(e => e.Category) : query.OrderBy(e => e.Category),
            _ => desc ? query.OrderByDescending(e => e.CreatedAtUtc) : query.OrderBy(e => e.CreatedAtUtc),
        };
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (value == null)
        {
            return null;
        }

        return value.Length > maxLength ? value[..maxLength] : value;
    }
}
