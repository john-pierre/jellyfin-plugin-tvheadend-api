// Completed stream session DTO — read model built from the relay_request_metric table.

using System;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Metrics;

/// <summary>
/// Read model of a completed relay stream session, returned by the session-detail API.
/// Built from the consolidated <c>relay_request_metric</c> table — there is no separate
/// completed-sessions store anymore.
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

    /// <summary>Gets or sets the HTTP method.</summary>
    public string RequestMethod { get; set; } = "GET";

    // ── Duration / Transfer ─────────────────────────────────────────

    /// <summary>Gets or sets total wall-clock duration in ms (equals watch time for streams).</summary>
    public double TotalDurationMs { get; set; }

    /// <summary>Gets or sets total bytes sent to the client.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Gets or sets the average bitrate over the entire session in bits per second.</summary>
    public double AverageBitrate { get; set; }

    /// <summary>Gets or sets the peak observed bitrate in bits per second (max of rolling window samples).</summary>
    public double? PeakBitrate { get; set; }

    // ── Latency ─────────────────────────────────────────────────────

    /// <summary>Gets or sets startup latency (request → first byte to client) in ms.</summary>
    public double StartupLatencyMs { get; set; }

    /// <summary>Gets or sets upstream headers latency in ms.</summary>
    public double? UpstreamHeadersLatencyMs { get; set; }

    /// <summary>Gets or sets upstream first byte latency in ms.</summary>
    public double? UpstreamFirstByteLatencyMs { get; set; }

    /// <summary>Gets or sets downstream first byte latency (first byte written to the client) in ms.</summary>
    public double? DownstreamFirstByteLatencyMs { get; set; }

    // ── Compatibility ───────────────────────────────────────────────

    /// <summary>Gets or sets a value indicating whether the client sent a Range header.</summary>
    public bool RangeRequested { get; set; }

    // ── Outcome ─────────────────────────────────────────────────────

    /// <summary>Gets or sets the ended-by classification.</summary>
    public StreamEndedBy EndedBy { get; set; }

    /// <summary>Gets or sets the failure reason (null if none).</summary>
    public string? FailureReason { get; set; }

    /// <summary>Gets or sets the final outcome classification.</summary>
    public StreamFinalOutcome FinalOutcome { get; set; }

    /// <summary>Gets or sets a value indicating whether this was a normal disconnect (not a failure).</summary>
    public bool NormalDisconnect { get; set; }

    // ── Identity / Zapping telemetry ────────────────────────────────

    /// <summary>Gets or sets the sanitized User-Agent string.</summary>
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>Gets or sets the effective TVHeadend profile used for the stream.</summary>
    public string? EffectiveProfile { get; set; }

    /// <summary>Gets or sets which level of the profile hierarchy resolved the effective profile.</summary>
    public string? ResolutionSource { get; set; }

    /// <summary>Gets or sets the mediainfo cache outcome recorded during stream setup.</summary>
    public string? MediaInfoCacheStatus { get; set; }

    /// <summary>Gets or sets the media source build (stream setup) duration in ms.</summary>
    public double? StreamSetupMs { get; set; }
}
