// Relay request metric entity — one row per completed or failed relay request.
// Single persistent truth for stream telemetry: the former parallel
// completed_stream_sessions pipeline was merged into this table.

using System;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Relay;

/// <summary>
/// Persisted summary of a single relay request (image or stream).
/// Stored in SQLite as an append-only row per completed or failed request.
/// For streams this row also carries the session-level enrichment (session ID,
/// channel/client names, outcome classification) that used to live in the
/// removed <c>completed_stream_sessions</c> table.
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

    // ── Session identity (streams) ──────────────────────────────────

    /// <summary>Gets or sets the in-memory session ID for streams. Null for images.</summary>
    public string? SessionId { get; set; }

    /// <summary>Gets or sets the channel display name if known at record time.</summary>
    public string? ChannelName { get; set; }

    /// <summary>Gets or sets the derived client name (e.g. "Infuse", "Jellyfin Web").</summary>
    public string? ClientName { get; set; }

    /// <summary>Gets or sets the sanitized User-Agent string.</summary>
    public string? UserAgent { get; set; }

    // ── Timing ──────────────────────────────────────────────────────

    /// <summary>Gets or sets total request duration in milliseconds. For streams this equals watch time.</summary>
    public double TotalDurationMs { get; set; }

    /// <summary>Gets or sets time until upstream response headers arrived in ms.</summary>
    public double? UpstreamHeadersDurationMs { get; set; }

    /// <summary>Gets or sets time until first byte was read from upstream in ms.</summary>
    public double? FirstByteFromUpstreamDurationMs { get; set; }

    /// <summary>Gets or sets time until first byte was written to the Jellyfin client in ms.</summary>
    public double? FirstByteToClientDurationMs { get; set; }

    /// <summary>Gets or sets startup latency for streams (request → first byte to client) in ms.</summary>
    public double? StartupLatencyMs { get; set; }

    // ── Transfer ────────────────────────────────────────────────────

    /// <summary>Gets or sets total bytes sent to the client.</summary>
    public long BytesSent { get; set; }

    /// <summary>Gets or sets average bytes per second (bytes/s) over the whole request if derivable.</summary>
    public double? AverageBytesPerSecond { get; set; }

    /// <summary>
    /// Gets or sets the peak observed bitrate in bits per second — the maximum of the
    /// ~10-second rolling window samples measured during the stream. Null for images and
    /// for streams that ended before any sample was taken.
    /// </summary>
    public double? PeakBitrate { get; set; }

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

    /// <summary>
    /// Gets or sets the stream final outcome classification
    /// (<see cref="StreamFinalOutcome"/> name). Null for images.
    /// </summary>
    public string? StreamFinalOutcome { get; set; }

    /// <summary>Gets or sets a value indicating whether the stream ended as a normal disconnect. Null for images.</summary>
    public bool? NormalDisconnect { get; set; }

    // ── Cache (images) ──────────────────────────────────────────────

    /// <summary>Gets or sets the cache status for image requests.</summary>
    public string CacheStatus { get; set; } = nameof(RelayCacheStatus.NotApplicable);

    /// <summary>Gets or sets the on-disk image cache lookup duration in ms (images only).</summary>
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

    /// <summary>Gets or sets how the stream ended (<see cref="StreamEndedBy"/> name). Null for images.</summary>
    public string? EndedBy { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the stream failed to deliver a first byte within
    /// 5 seconds: the request failed AND either the first byte took longer than 5000 ms or no
    /// byte was ever delivered while the request lasted at least 5 seconds.
    /// </summary>
    public bool StartupFailedWithin5Seconds { get; set; }

    /// <summary>Gets or sets the count of active streams when this stream started.</summary>
    public int? ParallelActiveStreamCountAtStart { get; set; }

    // ── Zapping telemetry (streams) ─────────────────────────────────

    /// <summary>Gets or sets the effective TVHeadend profile used for the stream.</summary>
    public string? EffectiveProfile { get; set; }

    /// <summary>Gets or sets which level of the profile hierarchy resolved the effective profile.</summary>
    public string? ResolutionSource { get; set; }

    /// <summary>
    /// Gets or sets the mediainfo cache outcome recorded during stream setup:
    /// <c>hit</c>, <c>miss</c>, <c>mismatch</c>, <c>restored</c>, or <c>unknown</c>.
    /// </summary>
    public string? MediaInfoCacheStatus { get; set; }

    /// <summary>Gets or sets the media source build (stream setup) duration in ms measured at token issue.</summary>
    public double? StreamSetupMs { get; set; }
}
