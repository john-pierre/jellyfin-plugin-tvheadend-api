using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Statistics;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Jellyfin.Plugin.TvHeadendApi.Service.Statistics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Comet;

/// <summary>
/// Manages the TVHeadend Comet WebSocket connection and buffers operational snapshots for the admin UI.
/// Implements exponential backoff reconnect and persists TVHeadend logs to SQLite.
/// </summary>
internal sealed class CometService : IHostedService, ICometSnapshotReader, IDisposable
{
    private const int MaxBufferSize = 200;
    private const int MaxLogTextLength = 2048;
    internal const string WebSocketSubProtocol = "tvheadend-comet";

    private readonly ILogger<CometService> _logger;
    private readonly IApiClient _apiClient;
    private readonly IUrlBuilder _urlBuilder;
    private readonly PluginConfigurationProvider _configProvider;
    private readonly DbContextOptions<ViewingSessionContext>? _dbContextOptions;
    private readonly PluginLogService? _pluginLogService;
    private readonly List<LogMessage> _logBuffer = new();
    private readonly object _logLock = new();
    private readonly object _diskLock = new();

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _receiveLoopTask;
    private int _reconnectAttempt;

    // Buffers (thread-safe, bounded)
    private DiskSpaceUpdate? _lastDiskSpaceUpdate;

    public CometService(
        ILogger<CometService> logger,
        IApiClient apiClient,
        IUrlBuilder urlBuilder,
        PluginConfigurationProvider configProvider,
        DbContextOptions<ViewingSessionContext>? dbContextOptions = null,
        PluginLogService? pluginLogService = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _urlBuilder = urlBuilder ?? throw new ArgumentNullException(nameof(urlBuilder));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _dbContextOptions = dbContextOptions;
        _pluginLogService = pluginLogService;
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
    public IReadOnlyList<TvhLogEntry> GetLogHistory(int count = 500, DateTime? sinceUtc = null)
    {
        if (_dbContextOptions == null)
        {
            return Array.Empty<TvhLogEntry>();
        }

        try
        {
            using var db = new ViewingSessionContext(_dbContextOptions);
            IQueryable<TvhLogEntry> query = db.TvhLogEntries.AsNoTracking();
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
            return Array.Empty<TvhLogEntry>();
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

                await ConnectAndReceiveAsync(config, cancellationToken).ConfigureAwait(false);

                // Normal close — reset backoff
                _reconnectAttempt = 0;
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

    private TimeSpan CalculateBackoffDelay()
    {
        var config = _configProvider.Configuration;
        var baseDelay = config?.CometReconnectBaseDelaySeconds > 0
            ? config.CometReconnectBaseDelaySeconds
            : 2;
        var maxDelay = config?.CometReconnectMaxDelaySeconds > 0
            ? config.CometReconnectMaxDelaySeconds
            : 120;

        // Exponential backoff with jitter: base * 2^attempt + random jitter
        var exponentialSeconds = baseDelay * Math.Pow(2, Math.Min(_reconnectAttempt - 1, 6));
        var jitter = Random.Shared.NextDouble() * baseDelay;
        var totalSeconds = Math.Min(exponentialSeconds + jitter, maxDelay);

        return TimeSpan.FromSeconds(totalSeconds);
    }

    /// <summary>
    /// Connects and runs the receive loop. Returns on disconnect/error.
    /// </summary>
    private async Task ConnectAndReceiveAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        var wsUri = BuildWebSocketUri(config, _urlBuilder);
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(WebSocketSubProtocol);

        using var httpClient = _apiClient.CreateApiHttpClient(config);
        await socket.ConnectAsync(wsUri, httpClient, cancellationToken).ConfigureAwait(false);

        _reconnectAttempt = 0;
        _logger.LogInformation(
            "TVHeadend Comet service connected to {WebSocketUrl}",
            _urlBuilder.MaskSensitiveData(wsUri.ToString(), config));

        await ReceiveLoopAsync(socket, cancellationToken).ConfigureAwait(false);
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
    /// </summary>
    /// <returns>A task representing the asynchronous stop operation.</returns>
    public async Task StopAsync()
    {
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
            }

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
    /// <returns>A task representing the background receive operation.</returns>
    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var stringBuilder = new StringBuilder();

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

                    return;
                }
            }
            while (result != null && !result.EndOfMessage);

            if (stringBuilder.Length > 0)
            {
                ProcessMessage(stringBuilder.ToString());
            }
        }
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
    /// </summary>
    private void AddLog(string text)
    {
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

        // Feed parsed TVHeadend log to the unified PluginLogService
        _pluginLogService?.EnqueueTvHeadendLog(text);
    }

    /// <summary>
    /// Persists a log entry to SQLite in a fire-and-forget manner.
    /// </summary>
    private void PersistLogEntry(DateTime timestampUtc, string text)
    {
        if (_dbContextOptions == null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                using var db = new ViewingSessionContext(_dbContextOptions);
                db.TvhLogEntries.Add(new TvhLogEntry
                {
                    TimestampUtc = timestampUtc,
                    Text = text.Length > MaxLogTextLength ? text[..MaxLogTextLength] : text,
                });
                db.SaveChanges();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to persist TVHeadend log entry to SQLite");
            }
        });
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

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }
}
