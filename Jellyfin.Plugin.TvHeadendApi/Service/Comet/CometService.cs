using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Statistic;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Database;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Logging;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Jellyfin.Plugin.TvHeadendApi.Service.Statistic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Comet;

/// <summary>
/// Manages the TVHeadend Comet WebSocket connection and buffers operational snapshots for the admin UI.
/// Implements exponential backoff reconnect and persists TVHeadend logs to SQLite.
/// </summary>
internal sealed class CometService : IHostedService, ICometSnapshotReader, IAsyncDisposable, IDisposable
{
    private const int MaxBufferSize = 200;
    private const int MaxLogTextLength = 2048;
    private const int MaxPersistQueueSize = 1000;
    private const int PersistFlushBatchSize = 100;
    internal const string WebSocketSubProtocol = "tvheadend-comet";

    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<CometService> _logger;
    private readonly IApiClient _apiClient;
    private readonly IUrlBuilder _urlBuilder;
    private readonly ConfigurationProvider _configProvider;
    private readonly DatabaseProvider? _databaseProvider;
    private readonly PluginLogService? _pluginLogService;
    private readonly DatabaseWriteCoordinator? _writeCoordinator;
    private readonly List<LogMessage> _logBuffer = new();
    private readonly object _logLock = new();
    private readonly object _diskLock = new();
    private readonly ConcurrentQueue<TvheadendLogEntry> _persistQueue = new();

    private DbContextOptions<ViewingSessionContext>? _lazyDbContextOptions;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _receiveLoopTask;
    private Task _persistFlushTask = Task.CompletedTask;
    private int _persistFlushScheduled;
    private int _reconnectAttempt;
    private int _stopped;

    // Buffers (thread-safe, bounded)
    private DiskSpaceUpdate? _lastDiskSpaceUpdate;

    public CometService(
        ILogger<CometService> logger,
        IApiClient apiClient,
        IUrlBuilder urlBuilder,
        ConfigurationProvider configProvider,
        DatabaseProvider? databaseProvider = null,
        PluginLogService? pluginLogService = null,
        DatabaseWriteCoordinator? writeCoordinator = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _urlBuilder = urlBuilder ?? throw new ArgumentNullException(nameof(urlBuilder));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _databaseProvider = databaseProvider;
        _pluginLogService = pluginLogService;
        _writeCoordinator = writeCoordinator;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CometService"/> class
    /// with pre-built context options for unit testing.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="apiClient">TVHeadend API client.</param>
    /// <param name="urlBuilder">TVHeadend URL builder.</param>
    /// <param name="configProvider">Configuration provider.</param>
    /// <param name="dbContextOptions">Pre-built EF Core context options.</param>
    /// <param name="pluginLogService">Optional plugin log service.</param>
    /// <param name="writeCoordinator">Optional coordinator serializing SQLite writes across services.</param>
    internal CometService(
        ILogger<CometService> logger,
        IApiClient apiClient,
        IUrlBuilder urlBuilder,
        ConfigurationProvider configProvider,
        DbContextOptions<ViewingSessionContext>? dbContextOptions,
        PluginLogService? pluginLogService,
        DatabaseWriteCoordinator? writeCoordinator = null)
        : this(logger, apiClient, urlBuilder, configProvider, (DatabaseProvider?)null, pluginLogService, writeCoordinator)
    {
        _lazyDbContextOptions = dbContextOptions;
    }

    /// <summary>
    /// Gets the current reconnect attempt counter. Exposed for unit testing the backoff policy.
    /// </summary>
    internal int ReconnectAttempt => _reconnectAttempt;

    private DbContextOptions<ViewingSessionContext>? GetDbContextOptions()
    {
        if (_databaseProvider == null && _lazyDbContextOptions == null)
        {
            return null;
        }

        if (_lazyDbContextOptions != null)
        {
            return _lazyDbContextOptions;
        }

        return _lazyDbContextOptions ??= _databaseProvider!.CreateContextOptions<ViewingSessionContext>();
    }

    /// <summary>
    /// Gets the last N log messages from the buffer.
    /// </summary>
    /// <param name="count">The maximum number of log messages to retrieve. Default is 100.</param>
    /// <returns>A list of log messages, up to the specified count.</returns>
    public IReadOnlyList<LogMessage> GetRecentLogs(int count = 100)
    {
        lock (_logLock)
        {
            return _logBuffer.TakeLast(count).ToList();
        }
    }

    /// <summary>
    /// Gets historical log entries from SQLite persistence.
    /// </summary>
    /// <param name="count">Maximum number of entries. Default: 500.</param>
    /// <param name="sinceUtc">Optional: only return entries after this timestamp.</param>
    /// <returns>Log entries in reverse chronological order.</returns>
    public IReadOnlyList<TvheadendLogEntry> GetLogHistory(int count = 500, DateTime? sinceUtc = null)
    {
        var dbOpts = GetDbContextOptions();
        if (dbOpts == null)
        {
            return Array.Empty<TvheadendLogEntry>();
        }

        try
        {
            using var db = new ViewingSessionContext(dbOpts);
            IQueryable<TvheadendLogEntry> query = db.TvheadendLogEntries.AsNoTracking();
            if (sinceUtc.HasValue)
            {
                query = query.Where(l => l.TimestampUtc >= sinceUtc.Value);
            }

            return query
                .OrderByDescending(l => l.TimestampUtc)
                .Take(count)
                .ToList()
                .AsReadOnly();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read TVHeadend log history from SQLite");
            return Array.Empty<TvheadendLogEntry>();
        }
    }

    /// <summary>
    /// Gets the most recent disk space update, if any.
    /// </summary>
    /// <returns>The most recent DiskSpaceUpdate, or null if no updates have been received.</returns>
    public DiskSpaceUpdate? GetLastDiskSpaceUpdate()
    {
        lock (_diskLock)
        {
            return _lastDiskSpaceUpdate;
        }
    }

    /// <summary>
    /// Starts the Comet service during plugin initialization.
    /// Connects to TVHeadend WebSocket if host is configured.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the asynchronous startup operation.</returns>
    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        var config = _configProvider.Configuration;
        if (config == null || string.IsNullOrWhiteSpace(config.Host))
        {
            _logger.LogDebug("Comet service startup skipped: no TvHeadend host configured");
            return Task.CompletedTask;
        }

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveLoopTask = RunWithReconnectAsync(_cancellationTokenSource.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the Comet service during plugin shutdown.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the asynchronous shutdown operation.</returns>
    Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        return StopAsync();
    }

    /// <summary>
    /// Runs the WebSocket connection loop with exponential backoff reconnection.
    /// </summary>
    private async Task RunWithReconnectAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var config = _configProvider.Configuration;
                if (config == null || string.IsNullOrWhiteSpace(config.Host))
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var receivedAnyMessage = await ConnectAndReceiveAsync(config, cancellationToken).ConfigureAwait(false);

                // A graceful close is only "normal" when the session actually delivered data.
                // A close straight after the handshake must back off like a failure, otherwise
                // the loop spins against the endpoint with zero delay.
                await ApplyPostSessionBackoffAsync(receivedAnyMessage, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _reconnectAttempt++;
                var delay = CalculateBackoffDelay();
                _logger.LogWarning(
                    ex,
                    "Comet WebSocket connection failed (attempt {Attempt}). Reconnecting in {Delay}s",
                    _reconnectAttempt,
                    delay.TotalSeconds);

                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Applies the reconnect backoff policy after a WebSocket session ended without an exception.
    /// A graceful close received before any application message is treated as a failure: peers that
    /// accept the handshake but immediately close (proxy subprotocol mismatch, post-handshake auth
    /// rejection, load shedding) would otherwise cause a tight, unthrottled connect/close loop.
    /// </summary>
    /// <param name="receivedAnyMessage">Whether the session delivered at least one application message.</param>
    /// <param name="cancellationToken">Cancellation token observed while delaying.</param>
    /// <returns>The delay that was awaited before the next reconnect attempt.</returns>
    internal async Task<TimeSpan> ApplyPostSessionBackoffAsync(bool receivedAnyMessage, CancellationToken cancellationToken)
    {
        if (receivedAnyMessage)
        {
            // Healthy session ended with a graceful close — reset backoff and reconnect promptly.
            _reconnectAttempt = 0;
            return TimeSpan.Zero;
        }

        _reconnectAttempt++;
        var delay = CalculateBackoffDelay();
        _logger.LogWarning(
            "Comet WebSocket closed before any message was received (attempt {Attempt}). Reconnecting in {Delay}s",
            _reconnectAttempt,
            delay.TotalSeconds);

        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        return delay;
    }

    private TimeSpan CalculateBackoffDelay()
    {
        var config = _configProvider.Configuration;
        var baseDelay = config?.CometReconnectBaseDelaySeconds > 0
            ? config.CometReconnectBaseDelaySeconds
            : 2;
        var maxDelay = config?.CometReconnectMaxDelaySeconds > 0
            ? config.CometReconnectMaxDelaySeconds
            : 120;

        return CalculateBackoffDelay(_reconnectAttempt, baseDelay, maxDelay);
    }

    /// <summary>
    /// Calculates the exponential backoff delay with jitter for the given reconnect attempt.
    /// </summary>
    /// <param name="reconnectAttempt">The 1-based reconnect attempt counter.</param>
    /// <param name="baseDelaySeconds">Base delay in seconds.</param>
    /// <param name="maxDelaySeconds">Upper bound for the delay in seconds.</param>
    /// <returns>The bounded delay to wait before the next reconnect attempt.</returns>
    internal static TimeSpan CalculateBackoffDelay(int reconnectAttempt, int baseDelaySeconds, int maxDelaySeconds)
    {
        // Exponential backoff with jitter: base * 2^attempt + random jitter
        var exponentialSeconds = baseDelaySeconds * Math.Pow(2, Math.Min(reconnectAttempt - 1, 6));
        var jitter = Random.Shared.NextDouble() * baseDelaySeconds;
        var totalSeconds = Math.Min(exponentialSeconds + jitter, maxDelaySeconds);

        return TimeSpan.FromSeconds(totalSeconds);
    }

    /// <summary>
    /// Connects and runs the receive loop. Returns on disconnect/error.
    /// The reconnect attempt counter is intentionally NOT reset on a successful handshake:
    /// a peer that accepts the handshake but closes before sending data would otherwise
    /// defeat the exponential backoff. The counter resets on the first received message.
    /// </summary>
    /// <returns><c>true</c> when at least one application message was received during the session.</returns>
    private async Task<bool> ConnectAndReceiveAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        var wsUri = BuildWebSocketUri(config, _urlBuilder);
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(WebSocketSubProtocol);

        using var httpClient = _apiClient.CreateApiHttpClient(config);
        await socket.ConnectAsync(wsUri, httpClient, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "TVHeadend Comet service connected to {WebSocketUrl}",
            _urlBuilder.MaskSensitiveData(wsUri.ToString(), config));

        return await ReceiveLoopAsync(socket, cancellationToken).ConfigureAwait(false);
    }

    internal static Uri BuildWebSocketUri(PluginConfiguration config, IUrlBuilder urlBuilder)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(urlBuilder);

        var resourceUri = new Uri(urlBuilder.BuildResourceUrl(config, "comet/ws"), UriKind.Absolute);
        var webSocketScheme = string.Equals(resourceUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? "wss"
            : "ws";

        var uriBuilder = new UriBuilder(resourceUri)
        {
            Scheme = webSocketScheme,
            Port = resourceUri.Port,
        };

        return uriBuilder.Uri;
    }

    /// <summary>
    /// Stops the WebSocket connection and cancels the receive loop.
    /// Idempotent: the host runs <see cref="IHostedService.StopAsync(CancellationToken)"/> before
    /// container disposal triggers <see cref="Dispose"/>, so subsequent invocations are no-ops.
    /// </summary>
    /// <returns>A task representing the asynchronous stop operation.</returns>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return;
        }

        try
        {
            if (_cancellationTokenSource != null)
            {
                await _cancellationTokenSource.CancelAsync().ConfigureAwait(false);
            }

            if (_receiveLoopTask != null)
            {
                try
                {
                    await _receiveLoopTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancelling the token
                }

                _receiveLoopTask = null;
            }

            // Best-effort: let an in-flight legacy log flush finish so queued entries are not lost.
            // The flush task never faults — all failures are caught and logged inside it.
            await _persistFlushTask.ConfigureAwait(false);

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;

            _logger.LogInformation("TVHeadend Comet service stopped");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during Comet service shutdown");
        }
    }

    /// <summary>
    /// Receives and processes messages from the WebSocket in a background loop.
    /// </summary>
    /// <param name="socket">The connected WebSocket.</param>
    /// <param name="cancellationToken">Cancellation token to stop the receive loop.</param>
    /// <returns><c>true</c> when at least one application message was received before the loop ended.</returns>
    private async Task<bool> ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var stringBuilder = new StringBuilder();
        var receivedAnyMessage = false;

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            stringBuilder.Clear();

            // Read message (may be chunked)
            WebSocketReceiveResult? result = null;
            do
            {
                result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    stringBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("WebSocket close frame received");
                    if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                    {
                        await socket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Closing",
                            CancellationToken.None).ConfigureAwait(false);
                    }

                    return receivedAnyMessage;
                }
            }
            while (result != null && !result.EndOfMessage);

            if (stringBuilder.Length > 0)
            {
                if (!receivedAnyMessage)
                {
                    // First application message proves a working end-to-end Comet channel — reset backoff.
                    receivedAnyMessage = true;
                    _reconnectAttempt = 0;
                }

                ProcessMessage(stringBuilder.ToString());
            }
        }

        return receivedAnyMessage;
    }

    /// <summary>
    /// Parses a JSON message from TVHeadend and buffers relevant data.
    /// </summary>
    private void ProcessMessage(string jsonString)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonString);
            var root = doc.RootElement;

            if (root.TryGetProperty("messages", out var messages))
            {
                foreach (var msg in messages.EnumerateArray())
                {
                    ProcessNotification(msg);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Comet message");
        }
    }

    /// <summary>
    /// Processes a single notification from TVHeadend.
    /// </summary>
    private void ProcessNotification(JsonElement notification)
    {
        if (!notification.TryGetProperty("notificationClass", out var classElement))
        {
            return;
        }

        var className = classElement.GetString() ?? string.Empty;

        if (className == "logmessage")
        {
            if (notification.TryGetProperty("logtxt", out var logElement))
            {
                var logText = logElement.GetString() ?? string.Empty;
                AddLog(logText);
            }
        }
        else if (className == "diskspaceUpdate")
        {
            UpdateDiskSpace(notification);
        }
    }

    /// <summary>
    /// Adds a log message to the in-memory buffer and persists to SQLite.
    /// Internal for unit testing of the persistence pipeline.
    /// </summary>
    /// <param name="text">The raw TVHeadend log line.</param>
    internal void AddLog(string text)
    {
        // TVHeadend comet log lines can embed a persistent ?auth=<token>; sanitize once here so the
        // token never reaches the in-memory buffer, the legacy SQLite table, or the admin dashboard.
        text = Logging.LogSanitizer.Sanitize(text);

        var now = DateTime.UtcNow;
        var msg = new LogMessage
        {
            Timestamp = now,
            Text = text,
        };

        lock (_logLock)
        {
            _logBuffer.Add(msg);
            if (_logBuffer.Count > MaxBufferSize)
            {
                _logBuffer.RemoveRange(0, _logBuffer.Count - MaxBufferSize);
            }
        }

        // Persist to legacy SQLite table asynchronously
        PersistLogEntry(now, text);

        // Feed parsed TVHeadend log to the unified PluginLogService. Pass the receipt time so the
        // stored timestamp is accurate UTC — TVHeadend's embedded local timestamps have no offset and
        // would otherwise be stored skewed (appearing hours in the future).
        _pluginLogService?.EnqueueTvHeadendLog(text, now);
    }

    /// <summary>
    /// Queues a log entry for persistence to the legacy SQLite table. The hot path only enqueues;
    /// a single background flusher batches entries and serializes writes through the
    /// <see cref="DatabaseWriteCoordinator"/> so bursts of Comet log lines no longer spawn one
    /// uncoordinated connection per line.
    /// </summary>
    private void PersistLogEntry(DateTime timestampUtc, string text)
    {
        var dbOpts = GetDbContextOptions();
        if (dbOpts == null)
        {
            return;
        }

        // Bounded queue: drop new entries when full — the legacy table is a best-effort mirror.
        if (_persistQueue.Count >= MaxPersistQueueSize)
        {
            return;
        }

        _persistQueue.Enqueue(new TvheadendLogEntry
        {
            TimestampUtc = timestampUtc,
            Text = text.Length > MaxLogTextLength ? text[..MaxLogTextLength] : text,
        });

        SchedulePersistFlush(dbOpts);
    }

    /// <summary>
    /// Schedules the single-flight background flush of the persistence queue.
    /// Only one flusher runs at a time; producers merely enqueue.
    /// </summary>
    private void SchedulePersistFlush(DbContextOptions<ViewingSessionContext> dbOpts)
    {
        if (Interlocked.CompareExchange(ref _persistFlushScheduled, 1, 0) == 0)
        {
            _persistFlushTask = Task.Run(() => FlushPersistQueueAsync(dbOpts));
        }
    }

    /// <summary>
    /// Drains the persistence queue in batches, serializing each write through the
    /// <see cref="DatabaseWriteCoordinator"/> when available so this path cannot race
    /// other plugin writers on the shared SQLite file.
    /// </summary>
    private async Task FlushPersistQueueAsync(DbContextOptions<ViewingSessionContext> dbOpts)
    {
        try
        {
            while (!_persistQueue.IsEmpty)
            {
                var batch = new List<TvheadendLogEntry>(PersistFlushBatchSize);
                while (batch.Count < PersistFlushBatchSize && _persistQueue.TryDequeue(out var entry))
                {
                    batch.Add(entry);
                }

                if (batch.Count == 0)
                {
                    break;
                }

                IDisposable? writeLock = null;
                if (_writeCoordinator != null)
                {
                    writeLock = await _writeCoordinator.AcquireWriteAsync().ConfigureAwait(false);
                }

                try
                {
                    using var db = new ViewingSessionContext(dbOpts);
                    db.TvheadendLogEntries.AddRange(batch);
                    await db.SaveChangesAsync().ConfigureAwait(false);
                }
                finally
                {
                    writeLock?.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist TVHeadend log entries to SQLite");
        }
        finally
        {
            Volatile.Write(ref _persistFlushScheduled, 0);

            // Close the race where a producer enqueued after the drain but before the
            // single-flight flag was cleared: that producer saw the flag set and skipped
            // scheduling, so re-check here.
            if (!_persistQueue.IsEmpty)
            {
                SchedulePersistFlush(dbOpts);
            }
        }
    }

    /// <summary>
    /// Updates the disk space information.
    /// </summary>
    private void UpdateDiskSpace(JsonElement notification)
    {
        try
        {
            var update = new DiskSpaceUpdate
            {
                Timestamp = DateTime.UtcNow,
                TotalDiskSpace = notification.TryGetProperty("totaldiskspace", out var total)
                    ? total.GetInt64() : 0,
                UsedDiskSpace = notification.TryGetProperty("useddiskspace", out var used)
                    ? used.GetInt64() : 0,
                FreeDiskSpace = notification.TryGetProperty("freediskspace", out var free)
                    ? free.GetInt64() : 0,
            };

            lock (_diskLock)
            {
                _lastDiskSpaceUpdate = update;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse disk space update");
        }
    }

    /// <summary>
    /// Asynchronously stops the service. Preferred over <see cref="Dispose"/> because it avoids
    /// blocking the disposing thread with a sync-over-async bridge.
    /// </summary>
    /// <returns>A task representing the asynchronous dispose operation.</returns>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        // Normal host shutdown already ran IHostedService.StopAsync, so this is a guarded no-op.
        // The bounded wait protects the disposing thread from blocking indefinitely otherwise.
        if (!StopAsync().Wait(DisposeTimeout))
        {
            _logger.LogWarning("Timed out waiting for the Comet service to stop during dispose");
        }
    }
}
