// Completed stream session record — persisted to SQLite after stream finalization.

using System;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Metrics;

/// <summary>
/// Immutable record of a completed relay stream session.
/// Written to SQLite when a session transitions to Completed or Failed.
/// </summary>
public sealed class CompletedStreamSession
{
    /// <summary>Gets or sets the unique session identifier.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets when this session started (UTC).</summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>Gets or sets when this session ended (UTC).</summary>
    public DateTime EndedAtUtc { get; set; }

    /// <summary>Gets or sets the TVHeadend channel UUID.</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>Gets or sets the channel display name.</summary>
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>Gets or sets the derived client name.</summary>
    public string ClientName { get; set; } = string.Empty;

    /// <summary>Gets or sets the SHA-256 hash of the client IP.</summary>
    public string ClientIpHash { get; set; } = string.Empty;

    /// <summary>Gets or sets the HTTP method.</summary>
    public string RequestMethod { get; set; } = "GET";

    // ── Duration ─────────────────────────────────────────────────────

    /// <summary>Gets or sets total wall-clock duration in ms.</summary>
    public double TotalDurationMs { get; set; }

    /// <summary>Gets or sets streaming session duration in ms (first byte to last byte).</summary>
    public double SessionDurationMs { get; set; }

    // ── Transfer ─────────────────────────────────────────────────────

    /// <summary>Gets or sets total bytes sent to client.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Gets or sets rolling bitrate at end (bits/s).</summary>
    public double RollingBitrate { get; set; }

    /// <summary>Gets or sets average bitrate over entire session (bits/s).</summary>
    public double AverageBitrate { get; set; }

    /// <summary>Gets or sets peak bitrate observed (bits/s).</summary>
    public double PeakBitrate { get; set; }

    // ── Latency ──────────────────────────────────────────────────────

    /// <summary>Gets or sets startup latency (request → first byte to client) in ms.</summary>
    public double StartupLatencyMs { get; set; }

    /// <summary>Gets or sets upstream TCP connect latency in ms.</summary>
    public double? UpstreamConnectLatencyMs { get; set; }

    /// <summary>Gets or sets upstream headers latency in ms.</summary>
    public double? UpstreamHeadersLatencyMs { get; set; }

    /// <summary>Gets or sets upstream first byte latency in ms.</summary>
    public double? UpstreamFirstByteLatencyMs { get; set; }

    /// <summary>Gets or sets downstream first byte latency in ms.</summary>
    public double? DownstreamFirstByteLatencyMs { get; set; }

    /// <summary>Gets or sets 95th percentile write latency in ms.</summary>
    public double? P95WriteLatencyMs { get; set; }

    // ── Status ───────────────────────────────────────────────────────

    /// <summary>Gets or sets the upstream status description.</summary>
    public string UpstreamStatus { get; set; } = string.Empty;

    /// <summary>Gets or sets the downstream status description.</summary>
    public string DownstreamStatus { get; set; } = string.Empty;

    // ── Range/Compatibility ──────────────────────────────────────────

    /// <summary>Gets or sets a value indicating whether the client sent a Range header.</summary>
    public bool RangeRequested { get; set; }

    /// <summary>Gets or sets a value indicating whether upstream indicated range support.</summary>
    public bool RangeSupported { get; set; }

    /// <summary>Gets or sets a value indicating whether Content-Range was present.</summary>
    public bool ContentRangePresent { get; set; }

    /// <summary>Gets or sets a value indicating whether Accept-Ranges was present.</summary>
    public bool AcceptRangesPresent { get; set; }

    // ── Outcome ──────────────────────────────────────────────────────

    /// <summary>Gets or sets the ended-by classification.</summary>
    public StreamEndedBy EndedBy { get; set; }

    /// <summary>Gets or sets the failure reason (null if none).</summary>
    public string? FailureReason { get; set; }

    /// <summary>Gets or sets the final outcome classification.</summary>
    public StreamFinalOutcome FinalOutcome { get; set; }

    /// <summary>Gets or sets a value indicating whether this was a normal disconnect (not a failure).</summary>
    public bool NormalDisconnect { get; set; }

    // ── Identity ─────────────────────────────────────────────────────

    /// <summary>Gets or sets the sanitized User-Agent string.</summary>
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>Gets or sets the SHA-256 hash of the remote endpoint.</summary>
    public string RemoteEndpointHash { get; set; } = string.Empty;
}
