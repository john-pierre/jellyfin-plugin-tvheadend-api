// In-memory representation of an active streaming session for real-time dashboard visibility.

using System;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Metrics;

/// <summary>
/// Represents an active relay streaming session.
/// Stored in-memory and periodically flushed to SQLite for crash recovery.
/// Provides real-time visibility into ongoing streams.
/// </summary>
public sealed class ActiveStreamSession
{
    /// <summary>Gets or sets the unique session identifier.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets when this session started (UTC).</summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>Gets or sets the last periodic update timestamp (UTC).</summary>
    public DateTime LastUpdateUtc { get; set; }

    /// <summary>Gets or sets the TVHeadend channel UUID.</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>Gets or sets the channel display name.</summary>
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>Gets or sets the derived client name (e.g. "Infuse", "Jellyfin Web").</summary>
    public string ClientName { get; set; } = string.Empty;

    /// <summary>Gets or sets the SHA-256 hash of the client IP for privacy.</summary>
    public string ClientIpHash { get; set; } = string.Empty;

    /// <summary>Gets or sets the HTTP method (GET, HEAD).</summary>
    public string RequestMethod { get; set; } = "GET";

    /// <summary>Gets or sets total bytes sent to client so far.</summary>
    public long BytesSent { get; set; }

    /// <summary>Gets or sets rolling bitrate in bits/second (last 10s window).</summary>
    public double RollingBitrate { get; set; }

    /// <summary>Gets or sets average bitrate in bits/second since stream start.</summary>
    public double AverageBitrate { get; set; }

    /// <summary>Gets or sets peak bitrate in bits/second observed during session.</summary>
    public double PeakBitrate { get; set; }

    /// <summary>Gets or sets startup latency (request → first byte to client) in ms.</summary>
    public double StartupLatencyMs { get; set; }

    /// <summary>Gets or sets the upstream connection status description.</summary>
    public string UpstreamStatus { get; set; } = string.Empty;

    /// <summary>Gets or sets the downstream client status description.</summary>
    public string DownstreamStatus { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the client sent a Range header.</summary>
    public bool RangeRequested { get; set; }

    /// <summary>Gets or sets a value indicating whether upstream indicated range support.</summary>
    public bool RangeSupported { get; set; }

    /// <summary>Gets or sets a value indicating whether a Content-Range header was present.</summary>
    public bool ContentRangePresent { get; set; }

    /// <summary>Gets or sets a value indicating whether Accept-Ranges header was present.</summary>
    public bool AcceptRangesPresent { get; set; }

    /// <summary>Gets or sets the current lifecycle state.</summary>
    public StreamLifecycleState StreamState { get; set; } = StreamLifecycleState.Starting;

    /// <summary>Gets or sets the sanitized User-Agent string.</summary>
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>Gets or sets the SHA-256 hash of the remote endpoint.</summary>
    public string RemoteEndpointHash { get; set; } = string.Empty;
}
