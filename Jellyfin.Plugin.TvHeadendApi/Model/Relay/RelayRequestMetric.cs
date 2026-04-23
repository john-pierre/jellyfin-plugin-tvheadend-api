// Relay request metric entity — one row per completed or failed relay request.

using System;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Relay;

/// <summary>
/// Persisted summary of a single relay request (image or stream).
/// Stored in SQLite as an append-only row per completed or failed request.
/// </summary>
public sealed class RelayRequestMetric
{
    /// <summary>Gets or sets the database primary key.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets the UTC timestamp when this row was created.</summary>
    public DateTime CreatedAtUtc { get; set; }

    // ── Classification ──────────────────────────────────────────────

    /// <summary>Gets or sets the relay type (image or stream).</summary>
    public string RelayType { get; set; } = string.Empty;

    /// <summary>Gets or sets the media kind (logo, programImage, liveTvStream, etc.).</summary>
    public string MediaKind { get; set; } = string.Empty;

    /// <summary>Gets or sets the image source type (channel_logo, epg_image, etc.). Null for streams.</summary>
    public string? ImageSourceType { get; set; }

    /// <summary>Gets or sets the channel ID if available.</summary>
    public string? ChannelId { get; set; }

    // ── Timing ──────────────────────────────────────────────────────

    /// <summary>Gets or sets total request duration in milliseconds.</summary>
    public double TotalDurationMs { get; set; }

    /// <summary>Gets or sets time to establish upstream TCP connection in ms.</summary>
    public double? UpstreamConnectDurationMs { get; set; }

    /// <summary>Gets or sets time until upstream response headers arrived in ms.</summary>
    public double? UpstreamHeadersDurationMs { get; set; }

    /// <summary>Gets or sets time until first byte was read from upstream in ms.</summary>
    public double? FirstByteFromUpstreamDurationMs { get; set; }

    /// <summary>Gets or sets time until first byte was written to the Jellyfin client in ms.</summary>
    public double? FirstByteToClientDurationMs { get; set; }

    /// <summary>Gets or sets startup latency for streams (request → first byte to client) in ms.</summary>
    public double? StartupLatencyMs { get; set; }

    /// <summary>Gets or sets stream session duration in ms. Null for images.</summary>
    public double? SessionDurationMs { get; set; }

    // ── Transfer ────────────────────────────────────────────────────

    /// <summary>Gets or sets total bytes sent to the client.</summary>
    public long BytesSent { get; set; }

    /// <summary>Gets or sets average bytes per second if derivable.</summary>
    public double? AverageBytesPerSecond { get; set; }

    // ── Status / Outcome ────────────────────────────────────────────

    /// <summary>Gets or sets the upstream HTTP status code.</summary>
    public int? UpstreamStatusCode { get; set; }

    /// <summary>Gets or sets the HTTP status code returned to the client.</summary>
    public int ClientStatusCode { get; set; }

    /// <summary>Gets or sets the final outcome (success / failure / cancelled).</summary>
    public string FinalOutcome { get; set; } = string.Empty;

    /// <summary>Gets or sets the failure reason classification.</summary>
    public string FailureReason { get; set; } = nameof(RelayFailureReason.None);

    /// <summary>Gets or sets a value indicating whether the client cancelled the request.</summary>
    public bool ClientCancelled { get; set; }

    /// <summary>Gets or sets a value indicating whether the upstream timed out.</summary>
    public bool UpstreamTimedOut { get; set; }

    // ── Cache (images) ──────────────────────────────────────────────

    /// <summary>Gets or sets the cache status for image requests.</summary>
    public string CacheStatus { get; set; } = nameof(RelayCacheStatus.NotApplicable);

    /// <summary>Gets or sets cache lookup duration in ms.</summary>
    public double? CacheLookupDurationMs { get; set; }

    /// <summary>Gets or sets a value indicating whether the response had an ETag.</summary>
    public bool HadEtag { get; set; }

    /// <summary>Gets or sets a value indicating whether the response had a Last-Modified header.</summary>
    public bool HadLastModified { get; set; }

    /// <summary>Gets or sets a value indicating whether upstream returned 304 Not Modified.</summary>
    public bool WasNotModified304 { get; set; }

    // ── Request metadata ────────────────────────────────────────────

    /// <summary>Gets or sets the Content-Type of the response.</summary>
    public string? ContentType { get; set; }

    /// <summary>Gets or sets a value indicating whether a Range request header was present.</summary>
    public bool WasRangeRequest { get; set; }

    /// <summary>Gets or sets a value indicating whether the response had a Content-Length header.</summary>
    public bool HasContentLength { get; set; }

    /// <summary>Gets or sets the Content-Length value if known.</summary>
    public long? ContentLength { get; set; }

    /// <summary>Gets or sets the HTTP method (GET, HEAD, etc.).</summary>
    public string RequestMethod { get; set; } = "GET";

    // ── Stream-specific ─────────────────────────────────────────────

    /// <summary>Gets or sets how the stream ended. Null for images.</summary>
    public string? EndedBy { get; set; }

    /// <summary>Gets or sets a value indicating whether startup failed within 5 seconds.</summary>
    public bool StartupFailedWithin5Seconds { get; set; }

    /// <summary>Gets or sets the count of active streams when this stream started.</summary>
    public int? ParallelActiveStreamCountAtStart { get; set; }
}
