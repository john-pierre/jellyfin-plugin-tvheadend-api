using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Statistic;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Database;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Jellyfin.Plugin.TvHeadendApi.Service.Statistic;
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
    private readonly ConfigurationProvider _configProvider;
    private readonly DatabaseHealthService _dbHealthService;
    private readonly DatabaseProvider _databaseProvider;

    private readonly BlockingCollection<PluginLogEntry> _queue =
        new(new ConcurrentQueue<PluginLogEntry>(), MaxQueueSize);

    private DbContextOptions<ViewingSessionContext>? _lazyDbContextOptions;
    private CancellationTokenSource? _cts;
    private Task? _writerTask;
    private volatile bool _dbLoggingFailed;

    public PluginLogService(
        ILogger<PluginLogService> logger,
        ConfigurationProvider configProvider,
        DatabaseHealthService dbHealthService,
        DatabaseProvider databaseProvider)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _dbHealthService = dbHealthService ?? throw new ArgumentNullException(nameof(dbHealthService));
        _databaseProvider = databaseProvider ?? throw new ArgumentNullException(nameof(databaseProvider));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginLogService"/> class
    /// with pre-built context options for unit testing.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="configProvider">Configuration provider.</param>
    /// <param name="dbHealthService">Database health service.</param>
    /// <param name="dbContextOptions">Pre-built EF Core context options.</param>
    internal PluginLogService(
        ILogger<PluginLogService> logger,
        ConfigurationProvider configProvider,
        DatabaseHealthService dbHealthService,
        DbContextOptions<ViewingSessionContext> dbContextOptions)
        : this(logger, configProvider, dbHealthService, CreateNullProvider())
    {
        _lazyDbContextOptions = dbContextOptions;
    }

    private static DatabaseProvider CreateNullProvider()
    {
        return new DatabaseProvider(new Storage.DataFolderPathProvider(() => null));
    }

    private DbContextOptions<ViewingSessionContext> GetDbContextOptions()
    {
        return _lazyDbContextOptions ??= _databaseProvider.CreateContextOptions<ViewingSessionContext>();
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

        // Exception text can carry secrets too (e.g. a URL with ?auth= in an HttpRequestException).
        var sanitizedException = exception != null && config.EnableDebugLogSanitization
            ? LogSanitizer.Sanitize(exception)
            : exception;

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
            Exception = sanitizedException != null ? Truncate(sanitizedException, MaxExceptionLength) : null,
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
    /// <param name="receivedAtUtc">
    /// The accurate UTC receipt time for real-time (Comet) logs. When provided it is used as the stored
    /// timestamp instead of the line's embedded local-time timestamp (which has no offset and would skew
    /// the dashboard). Null for the historical import path, which falls back to the parsed timestamp.
    /// </param>
    public void EnqueueTvHeadendLog(string rawLine, DateTime? receivedAtUtc = null)
    {
        var config = _configProvider.Configuration;
        if (config is not { StoreTvHeadendLogsInSqlite: true, TvHeadendLogImportEnabled: true })
        {
            return;
        }

        // Drop high-frequency, zero-diagnostic-value TVHeadend log noise (e.g. the internal EPG
        // grabber output that repeats every refetch interval). Importing it floods the log table
        // and buries plugin events, making the dashboard log view useless.
        if (IsLowValueTvhNoise(rawLine))
        {
            return;
        }

        var parsed = LogParser.Parse(rawLine);

        var sanitizedMessage = config.EnableDebugLogSanitization
            ? LogSanitizer.Sanitize(parsed.MessageWithoutTimestamp)
            : parsed.MessageWithoutTimestamp;

        var entry = new PluginLogEntry
        {
            // Prefer the receipt time for real-time (Comet) logs — it is accurate and timezone-proof.
            // TVHeadend embeds local-time timestamps with no offset, so trusting them risks a TZ skew
            // (they previously appeared hours in the future). The parsed timestamp is a fallback for
            // the historical import path only.
            CreatedAtUtc = receivedAtUtc ?? parsed.ParsedTimestampUtc ?? DateTime.UtcNow,
            Source = "tvheadend",
            LogType = parsed.LogType,
            Level = parsed.Level,
            Category = parsed.Category,
            Message = Truncate(sanitizedMessage, MaxMessageLength) ?? string.Empty,

            // RawSource is always sanitized: TVHeadend access-log lines embed a persistent
            // ?auth=<token>, and this field is returned verbatim to admins. Redact unconditionally
            // (independent of EnableDebugLogSanitization) so the token never lands at rest.
            RawSource = Truncate(LogSanitizer.Sanitize(rawLine), MaxMessageLength),
            RawLineHash = parsed.OriginalLineHash,
            ImportedAtUtc = DateTime.UtcNow,
        };

        _queue.TryAdd(entry);
    }

    /// <summary>
    /// Returns <c>true</c> for high-frequency, zero-diagnostic-value TVHeadend log lines
    /// (the internal EPG grabber's periodic output and no-op playlist re-scans) that would
    /// otherwise flood the log table every refetch interval and bury plugin events.
    /// </summary>
    /// <param name="rawLine">The raw TVHeadend log line.</param>
    /// <returns><c>true</c> when the line should be dropped rather than imported.</returns>
    private static bool IsLowValueTvhNoise(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return true;
        }

        return rawLine.Contains("tv_grab_url", StringComparison.OrdinalIgnoreCase)
            || rawLine.Contains("m3u parse: 0 new", StringComparison.OrdinalIgnoreCase);
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
        if (!_dbHealthService.IsAvailable)
        {
            return Array.Empty<PluginLogEntry>();
        }

        try
        {
            using var db = new ViewingSessionContext(GetDbContextOptions());
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
        if (!_dbHealthService.IsAvailable)
        {
            return 0;
        }

        try
        {
            using var db = new ViewingSessionContext(GetDbContextOptions());
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

    /// <inheritdoc />
    public int ClearAll()
    {
        if (!_dbHealthService.IsAvailable)
        {
            return 0;
        }

        try
        {
            using var db = new ViewingSessionContext(GetDbContextOptions());
            var count = db.PluginLogEntries.Count();
            db.PluginLogEntries.RemoveRange(db.PluginLogEntries);
            db.SaveChanges();
            _logger.LogInformation("Cleared {Count} log entries.", count);
            return count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear plugin log entries");
            return 0;
        }
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _writerTask = Task.Run(() => WriteLoopAsync(_cts.Token), _cts.Token);
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

        _cts?.Dispose();
    }

    public void Dispose()
    {
        _queue.Dispose();
        _cts?.Dispose();
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
        if (_dbLoggingFailed || !_dbHealthService.IsAvailable)
        {
            return;
        }

        try
        {
            using var db = new ViewingSessionContext(GetDbContextOptions());
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
