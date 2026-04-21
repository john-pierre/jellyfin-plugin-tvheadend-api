using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Comet;

/// <summary>
/// Manages the TVHeadend Comet WebSocket connection and buffers operational snapshots for the admin UI.
/// </summary>
internal sealed class CometService : IHostedService, ICometSnapshotReader, IDisposable
{
    private const int MaxBufferSize = 200;
    internal const string WebSocketSubProtocol = "tvheadend-comet";

    private readonly ILogger<CometService> _logger;
    private readonly IApiClient _apiClient;
    private readonly IUrlBuilder _urlBuilder;
    private readonly PluginConfigurationProvider _configProvider;
    private readonly List<LogMessage> _logBuffer = new();
    private readonly object _logLock = new();
    private readonly object _diskLock = new();

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _receiveLoopTask;

    // Buffers (thread-safe, bounded)
    private DiskSpaceUpdate? _lastDiskSpaceUpdate;

    public CometService(
        ILogger<CometService> logger,
        IApiClient apiClient,
        IUrlBuilder urlBuilder,
        PluginConfigurationProvider configProvider)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _urlBuilder = urlBuilder ?? throw new ArgumentNullException(nameof(urlBuilder));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
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
    async Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        var config = _configProvider.Configuration;
        if (config == null || string.IsNullOrWhiteSpace(config.Host))
        {
            _logger.LogDebug("Comet service startup skipped: no TvHeadend host configured");
            return;
        }

        try
        {
            await StartAsync(config, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Log but don't rethrow — service should still start even if Comet fails
            _logger.LogWarning(ex, "Failed to start Comet service on plugin startup");
        }
    }

    /// <summary>
    /// Stops the Comet service during plugin shutdown.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the asynchronous shutdown operation.</returns>
    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the background receive loop for the WebSocket.
    /// </summary>
    private async Task StartAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (_socket != null)
        {
            _logger.LogWarning("Comet service already started");
            return;
        }

        try
        {
            var wsUri = BuildWebSocketUri(config, _urlBuilder);
            _socket = new ClientWebSocket();
            _socket.Options.AddSubProtocol(WebSocketSubProtocol);

            using var httpClient = _apiClient.CreateApiHttpClient(config);
            await _socket.ConnectAsync(wsUri, httpClient, cancellationToken).ConfigureAwait(false);

            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _receiveLoopTask = ReceiveLoopAsync(_cancellationTokenSource.Token);

            _logger.LogInformation(
                "TVHeadend Comet service started, listening on {WebSocketUrl}",
                _urlBuilder.MaskSensitiveData(wsUri.ToString(), config));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Comet service");
            throw;
        }
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

            if (_socket != null)
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Service stopped",
                        CancellationToken.None).ConfigureAwait(false);
                }

                _socket.Dispose();
                _socket = null;
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
    /// <param name="cancellationToken">Cancellation token to stop the receive loop.</param>
    /// <returns>A task representing the background receive operation.</returns>
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_socket == null)
        {
            return;
        }

        var buffer = new byte[4096];
        var stringBuilder = new StringBuilder();

        try
        {
            while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                stringBuilder.Clear();

                // Read message (may be chunked)
                WebSocketReceiveResult? result = null;
                do
                {
                    result = await _socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        cancellationToken).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        stringBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("WebSocket close frame received");
                        await _socket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Closing",
                            CancellationToken.None).ConfigureAwait(false);
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
        catch (OperationCanceledException)
        {
            // Expected when stopping
            _logger.LogDebug("Comet receive loop cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Comet receive loop");
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
    /// Adds a log message to the buffer.
    /// </summary>
    private void AddLog(string text)
    {
        var msg = new LogMessage
        {
            Timestamp = DateTime.UtcNow,
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
