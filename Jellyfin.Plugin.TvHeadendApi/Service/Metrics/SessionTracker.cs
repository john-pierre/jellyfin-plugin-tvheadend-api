// Session lifecycle tracker — manages session creation, updates, and finalization with correct outcome classification.

using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Metrics;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Metrics;

/// <summary>
/// Central session lifecycle manager for relay streaming telemetry.
/// Creates sessions on stream start, updates during streaming,
/// and finalizes with correct outcome classification on stream end.
/// Thread-safe — all operations are designed for concurrent access.
/// </summary>
public sealed class SessionTracker
{
    private readonly ActiveSessionStore _activeStore;
    private readonly MetricsWriter _metricsWriter;
    private readonly ILogger<SessionTracker> _logger;
    private readonly Guide.ChannelNameCache? _channelNameCache;

    internal SessionTracker(
        ActiveSessionStore activeStore,
        MetricsWriter metricsWriter,
        ILogger<SessionTracker> logger,
        Guide.ChannelNameCache? channelNameCache = null)
    {
        _activeStore = activeStore ?? throw new ArgumentNullException(nameof(activeStore));
        _metricsWriter = metricsWriter ?? throw new ArgumentNullException(nameof(metricsWriter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _channelNameCache = channelNameCache;
    }

    /// <summary>Gets the number of currently active sessions.</summary>
    public int ActiveCount => _activeStore.Count;

    /// <summary>
    /// Creates and registers a new active session. Called immediately when a stream request arrives.
    /// The session is visible in real-time dashboards from this point.
    /// </summary>
    /// <param name="channelId">TVHeadend channel UUID.</param>
    /// <param name="requestMethod">HTTP method (GET, HEAD).</param>
    /// <param name="userAgent">Raw User-Agent header.</param>
    /// <param name="remoteIp">Remote client IP address (will be hashed).</param>
    /// <param name="rangeRequested">Whether a Range header was present.</param>
    /// <returns>The session ID for tracking through the request lifecycle.</returns>
    public string StartSession(
        string channelId,
        string requestMethod,
        string? userAgent,
        string? remoteIp,
        bool rangeRequested)
    {
        var sessionId = GenerateSessionId();
        var now = DateTime.UtcNow;

        var session = new ActiveStreamSession
        {
            SessionId = sessionId,
            StartedAtUtc = now,
            LastUpdateUtc = now,
            ChannelId = channelId ?? string.Empty,
            ChannelName = _channelNameCache?.GetName(channelId) ?? string.Empty,
            RequestMethod = requestMethod ?? "GET",
            UserAgent = SanitizeUserAgent(userAgent),
            ClientName = DeriveClientName(userAgent),
            ClientIpHash = HashValue(remoteIp),
            RemoteEndpointHash = HashValue(remoteIp),
            RangeRequested = rangeRequested,
            StreamState = StreamLifecycleState.Starting,
        };

        _activeStore.Add(session);
        _metricsWriter.WriteEvent(new RelayEvent
        {
            TimestampUtc = now,
            SessionId = sessionId,
            EventType = "session_started",
            Severity = "info",
            Message = $"Stream session started for channel {channelId}",
        });

        _logger.LogDebug("Session {SessionId} started for channel {ChannelId}", sessionId, channelId);
        return sessionId;
    }

    /// <summary>
    /// Marks the session as Active after first byte is sent to client.
    /// Records startup latency.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="startupLatencyMs">Time from request start to first byte in ms.</param>
    public void MarkFirstByteSent(string sessionId, double startupLatencyMs)
    {
        var session = _activeStore.Get(sessionId);
        if (session == null)
        {
            return;
        }

        session.StreamState = StreamLifecycleState.Active;
        session.StartupLatencyMs = startupLatencyMs;
        session.LastUpdateUtc = DateTime.UtcNow;

        _metricsWriter.WriteEvent(new RelayEvent
        {
            TimestampUtc = DateTime.UtcNow,
            SessionId = sessionId,
            EventType = "first_byte_sent",
            Severity = "info",
            Message = $"First byte sent to client in {startupLatencyMs:F1}ms",
        });
    }

    /// <summary>
    /// Periodic update during streaming — called every 5–10 seconds.
    /// Updates bytes, bitrate, and last-update timestamp.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="bytesSent">Total bytes sent so far.</param>
    /// <param name="rollingBitrate">Current rolling bitrate in bits/s.</param>
    /// <param name="averageBitrate">Average bitrate in bits/s.</param>
    /// <param name="peakBitrate">Peak observed bitrate in bits/s.</param>
    public void UpdateSession(string sessionId, long bytesSent, double rollingBitrate, double averageBitrate, double peakBitrate)
    {
        var session = _activeStore.Get(sessionId);
        if (session == null)
        {
            return;
        }

        session.BytesSent = bytesSent;
        session.RollingBitrate = rollingBitrate;
        session.AverageBitrate = averageBitrate;
        session.PeakBitrate = peakBitrate;
        session.LastUpdateUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Finalizes a session with the correct outcome classification.
    /// Removes from active store and persists to completed sessions table.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="totalBytes">Total bytes transferred.</param>
    /// <param name="totalDurationMs">Total wall-clock duration in ms.</param>
    /// <param name="sessionDurationMs">Stream session duration in ms.</param>
    /// <param name="endedBy">How the stream ended.</param>
    /// <param name="firstByteSent">Whether first byte was sent to client.</param>
    /// <param name="startupLatencyMs">Startup latency in ms.</param>
    /// <param name="upstreamHeadersLatencyMs">Upstream headers latency in ms.</param>
    /// <param name="upstreamFirstByteLatencyMs">Upstream first byte latency in ms.</param>
    /// <param name="downstreamFirstByteLatencyMs">Downstream first byte latency in ms.</param>
    /// <param name="rollingBitrate">Rolling bitrate in bits/s.</param>
    /// <param name="averageBitrate">Average bitrate in bits/s.</param>
    /// <param name="peakBitrate">Peak bitrate in bits/s.</param>
    /// <param name="upstreamStatus">Upstream status description.</param>
    /// <param name="downstreamStatus">Downstream status description.</param>
    /// <param name="rangeSupported">Whether range was supported by upstream.</param>
    /// <param name="contentRangePresent">Whether Content-Range was present.</param>
    /// <param name="acceptRangesPresent">Whether Accept-Ranges was present.</param>
    public void FinalizeSession(
        string sessionId,
        long totalBytes,
        double totalDurationMs,
        double sessionDurationMs,
        StreamEndedBy endedBy,
        bool firstByteSent,
        double startupLatencyMs,
        double? upstreamHeadersLatencyMs,
        double? upstreamFirstByteLatencyMs,
        double? downstreamFirstByteLatencyMs,
        double rollingBitrate,
        double averageBitrate,
        double peakBitrate,
        string? upstreamStatus,
        string? downstreamStatus,
        bool rangeSupported,
        bool contentRangePresent,
        bool acceptRangesPresent)
    {
        var session = _activeStore.Remove(sessionId);
        if (session == null)
        {
            _logger.LogDebug("Session {SessionId} not found in active store during finalization", sessionId);
            return;
        }

        var finalOutcome = ClassifyOutcome(endedBy, firstByteSent);
        var normalDisconnect = IsNormalDisconnect(finalOutcome);
        var failureReason = DeriveFailureReason(endedBy, finalOutcome);
        var now = DateTime.UtcNow;

        var completed = new CompletedStreamSession
        {
            SessionId = sessionId,
            StartedAtUtc = session.StartedAtUtc,
            EndedAtUtc = now,
            ChannelId = session.ChannelId,
            ChannelName = session.ChannelName,
            ClientName = session.ClientName,
            ClientIpHash = session.ClientIpHash,
            RequestMethod = session.RequestMethod,
            TotalDurationMs = totalDurationMs,
            SessionDurationMs = sessionDurationMs,
            TotalBytes = totalBytes,
            RollingBitrate = rollingBitrate,
            AverageBitrate = averageBitrate,
            PeakBitrate = peakBitrate,
            StartupLatencyMs = startupLatencyMs,
            UpstreamHeadersLatencyMs = upstreamHeadersLatencyMs,
            UpstreamFirstByteLatencyMs = upstreamFirstByteLatencyMs,
            DownstreamFirstByteLatencyMs = downstreamFirstByteLatencyMs,
            UpstreamStatus = upstreamStatus ?? string.Empty,
            DownstreamStatus = downstreamStatus ?? string.Empty,
            RangeRequested = session.RangeRequested,
            RangeSupported = rangeSupported,
            ContentRangePresent = contentRangePresent,
            AcceptRangesPresent = acceptRangesPresent,
            EndedBy = endedBy,
            FailureReason = failureReason,
            FinalOutcome = finalOutcome,
            NormalDisconnect = normalDisconnect,
            UserAgent = session.UserAgent,
            RemoteEndpointHash = session.RemoteEndpointHash,
        };

        _metricsWriter.WriteCompletedSession(completed);
        _metricsWriter.WriteEvent(new RelayEvent
        {
            TimestampUtc = now,
            SessionId = sessionId,
            EventType = "session_ended",
            Severity = normalDisconnect ? "info" : "warning",
            Message = $"Session ended: {endedBy} → {finalOutcome} (bytes={totalBytes}, duration={totalDurationMs:F0}ms)",
            UpstreamStatus = upstreamStatus,
            DownstreamStatus = downstreamStatus,
            BytesSentSnapshot = totalBytes,
        });

        _logger.LogDebug(
            "Session {SessionId} finalized: outcome={Outcome}, endedBy={EndedBy}, bytes={Bytes}, normal={Normal}",
            sessionId,
            finalOutcome,
            endedBy,
            totalBytes,
            normalDisconnect);
    }

    /// <summary>
    /// Classifies the final outcome based on how the stream ended and whether data was sent.
    /// </summary>
    /// <param name="endedBy">How the stream ended.</param>
    /// <param name="firstByteSent">Whether first byte was successfully sent to client.</param>
    /// <returns>The classified final outcome.</returns>
    public static StreamFinalOutcome ClassifyOutcome(StreamEndedBy endedBy, bool firstByteSent)
    {
        return endedBy switch
        {
            StreamEndedBy.UpstreamEof => StreamFinalOutcome.Completed,
            StreamEndedBy.ClientDisconnectAfterFirstByte => StreamFinalOutcome.NormalDisconnect,
            StreamEndedBy.UserStopOrChannelSwitch => StreamFinalOutcome.NormalDisconnect,
            StreamEndedBy.StartupCancelledBeforeFirstByte => StreamFinalOutcome.StartupFailed,
            StreamEndedBy.UpstreamTimeout => StreamFinalOutcome.UpstreamFailed,
            StreamEndedBy.UpstreamHttpError => StreamFinalOutcome.UpstreamFailed,
            StreamEndedBy.DownstreamWriteError => StreamFinalOutcome.DownstreamFailed,
            StreamEndedBy.PluginShutdown => firstByteSent ? StreamFinalOutcome.NormalDisconnect : StreamFinalOutcome.Failed,
            StreamEndedBy.UnexpectedException => StreamFinalOutcome.Failed,
            StreamEndedBy.Unknown => StreamFinalOutcome.Failed,
            _ => StreamFinalOutcome.Failed,
        };
    }

    /// <summary>
    /// Determines the correct <see cref="StreamEndedBy"/> value based on exception type and stream state.
    /// </summary>
    /// <param name="exception">The exception that ended the stream, or null if clean end.</param>
    /// <param name="firstByteSent">Whether first byte was successfully sent to client.</param>
    /// <param name="upstreamStatusCode">The upstream HTTP status code, if any.</param>
    /// <returns>The classified end reason.</returns>
    public static StreamEndedBy ClassifyEndReason(Exception? exception, bool firstByteSent, int? upstreamStatusCode)
    {
        if (exception is OperationCanceledException)
        {
            return firstByteSent
                ? StreamEndedBy.ClientDisconnectAfterFirstByte
                : StreamEndedBy.StartupCancelledBeforeFirstByte;
        }

        if (exception is System.IO.IOException)
        {
            return StreamEndedBy.DownstreamWriteError;
        }

        if (exception is System.Net.Http.HttpRequestException)
        {
            return StreamEndedBy.UpstreamTimeout;
        }

        if (exception is TaskCanceledException && !firstByteSent)
        {
            return StreamEndedBy.UpstreamTimeout;
        }

        if (upstreamStatusCode.HasValue)
        {
            var code = upstreamStatusCode.Value;
            if (code == 401 || code == 403 || code == 404 || code >= 500)
            {
                return StreamEndedBy.UpstreamHttpError;
            }
        }

        if (exception != null)
        {
            return StreamEndedBy.UnexpectedException;
        }

        return StreamEndedBy.UpstreamEof;
    }

    private static bool IsNormalDisconnect(StreamFinalOutcome outcome)
    {
        return outcome is StreamFinalOutcome.Completed or StreamFinalOutcome.NormalDisconnect;
    }

    private static string? DeriveFailureReason(StreamEndedBy endedBy, StreamFinalOutcome outcome)
    {
        if (IsNormalDisconnect(outcome))
        {
            return null;
        }

        return endedBy.ToString();
    }

    private static string GenerateSessionId()
    {
        return Guid.NewGuid().ToString("N")[..16];
    }

    private static string HashValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string SanitizeUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return string.Empty;
        }

        // Truncate to 256 chars max for storage efficiency.
        return userAgent.Length > 256 ? userAgent[..256] : userAgent;
    }

    /// <summary>
    /// Derives a human-readable client name from the User-Agent string.
    /// </summary>
    /// <param name="userAgent">The raw User-Agent header value.</param>
    /// <returns>A human-readable client name.</returns>
    internal static string DeriveClientName(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return "Unknown";
        }

        var ua = userAgent.AsSpan();
        if (ua.Contains("Infuse", StringComparison.OrdinalIgnoreCase))
        {
            return "Infuse";
        }

        if (ua.Contains("Swiftfin", StringComparison.OrdinalIgnoreCase))
        {
            return "Swiftfin";
        }

        if (ua.Contains("Jellyfin Mobile", StringComparison.OrdinalIgnoreCase))
        {
            return "Jellyfin Mobile";
        }

        if (ua.Contains("Jellyfin", StringComparison.OrdinalIgnoreCase))
        {
            return "Jellyfin Web";
        }

        if (ua.Contains("ExoPlayer", StringComparison.OrdinalIgnoreCase) || ua.Contains("Android", StringComparison.OrdinalIgnoreCase))
        {
            return "Android Client";
        }

        if (ua.Contains("AppleCoreMedia", StringComparison.OrdinalIgnoreCase) || ua.Contains("AVPlayer", StringComparison.OrdinalIgnoreCase))
        {
            return "Apple AVPlayer";
        }

        if (ua.Contains("VLC", StringComparison.OrdinalIgnoreCase))
        {
            return "VLC";
        }

        if (ua.Contains("mpv", StringComparison.OrdinalIgnoreCase))
        {
            return "mpv";
        }

        if (ua.Contains("Kodi", StringComparison.OrdinalIgnoreCase))
        {
            return "Kodi";
        }

        if (ua.Contains("FireTV", StringComparison.OrdinalIgnoreCase) || ua.Contains("Fire TV", StringComparison.OrdinalIgnoreCase))
        {
            return "Fire TV";
        }

        return "Unknown";
    }
}
