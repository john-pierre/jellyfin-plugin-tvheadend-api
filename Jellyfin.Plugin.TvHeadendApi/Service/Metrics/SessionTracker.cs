// Session lifecycle tracker — manages the in-memory session state with correct outcome classification.

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Metrics;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Metrics;

/// <summary>
/// Central session lifecycle manager for relay streaming telemetry.
/// Creates sessions on stream start, updates them while streaming, and removes them on
/// finalization. Sessions live ONLY in memory — persistence happens exactly once per
/// request through the relay timing context (<c>relay_request_metric</c> table), so this
/// tracker never writes to the database.
/// Thread-safe — all operations are designed for concurrent access.
/// </summary>
public sealed class SessionTracker
{
    private readonly ActiveSessionStore _activeStore;
    private readonly ILogger<SessionTracker> _logger;
    private readonly Guide.ChannelNameCache? _channelNameCache;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionTracker"/> class.
    /// </summary>
    /// <param name="activeStore">The in-memory active session store.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="channelNameCache">Optional channel-name cache for display-name enrichment.</param>
    public SessionTracker(
        ActiveSessionStore activeStore,
        ILogger<SessionTracker> logger,
        Guide.ChannelNameCache? channelNameCache = null)
    {
        _activeStore = activeStore ?? throw new ArgumentNullException(nameof(activeStore));
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
    /// <returns>The registered active session (caller reads its identity fields for telemetry).</returns>
    public ActiveStreamSession StartSession(
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
        _logger.LogDebug("Session {SessionId} started for channel {ChannelId}", sessionId, channelId);
        return session;
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
    }

    /// <summary>
    /// Periodic update during streaming — called every 5–10 seconds.
    /// Updates bytes, bitrates, and last-update timestamp.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="bytesSent">Total bytes sent so far.</param>
    /// <param name="rollingBitrate">Rolling bitrate over the last ~10 seconds in bits per second.</param>
    /// <param name="averageBitrate">Cumulative average bitrate since stream start in bits per second.</param>
    /// <param name="peakBitrate">Peak of the rolling bitrate samples in bits per second.</param>
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
    /// Finalizes a session: removes it from the active store and returns the outcome
    /// classification so the caller can stamp it onto the persisted relay metric.
    /// This method performs NO persistence — the relay metric row is the single
    /// persistent record of the stream.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="endedBy">How the stream ended.</param>
    /// <param name="firstByteSent">Whether the first byte was sent to the client.</param>
    /// <returns>The removed session with its classified outcome, or <c>null</c> when unknown.</returns>
    public FinalizedStreamSession? FinalizeSession(string sessionId, StreamEndedBy endedBy, bool firstByteSent)
    {
        var session = _activeStore.Remove(sessionId);
        if (session == null)
        {
            _logger.LogDebug("Session {SessionId} not found in active store during finalization", sessionId);
            return null;
        }

        var finalOutcome = StreamOutcomeClassifier.ClassifyOutcome(endedBy, firstByteSent);
        var normalDisconnect = StreamOutcomeClassifier.IsNormalDisconnect(finalOutcome);

        _logger.LogDebug(
            "Session {SessionId} finalized: outcome={Outcome}, endedBy={EndedBy}, bytes={Bytes}, normal={Normal}",
            sessionId,
            finalOutcome,
            endedBy,
            session.BytesSent,
            normalDisconnect);

        return new FinalizedStreamSession(session, endedBy, finalOutcome, normalDisconnect);
    }

    /// <summary>
    /// Classifies the final outcome based on how the stream ended and whether data was sent.
    /// </summary>
    /// <param name="endedBy">How the stream ended.</param>
    /// <param name="firstByteSent">Whether first byte was successfully sent to client.</param>
    /// <returns>The classified final outcome.</returns>
    public static StreamFinalOutcome ClassifyOutcome(StreamEndedBy endedBy, bool firstByteSent)
        => StreamOutcomeClassifier.ClassifyOutcome(endedBy, firstByteSent);

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

/// <summary>
/// Result of finalizing an in-memory stream session: the removed session plus its
/// classified outcome, handed back to the relay controller for metric stamping.
/// </summary>
/// <param name="Session">The removed active session (identity and last known counters).</param>
/// <param name="EndedBy">How the stream ended.</param>
/// <param name="FinalOutcome">The classified final outcome.</param>
/// <param name="NormalDisconnect">Whether the end counts as normal Live TV behavior.</param>
public sealed record FinalizedStreamSession(
    ActiveStreamSession Session,
    StreamEndedBy EndedBy,
    StreamFinalOutcome FinalOutcome,
    bool NormalDisconnect);
