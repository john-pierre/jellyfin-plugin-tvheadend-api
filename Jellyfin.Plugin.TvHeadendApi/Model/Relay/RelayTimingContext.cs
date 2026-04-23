// In-memory relay timing snapshot — built during a relay request, then persisted as a summary row.

using System;
using System.Diagnostics;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Relay;

/// <summary>
/// Lightweight mutable timing context carried through a single relay request.
/// Captures Stopwatch-based timestamps at key milestones. Converted to a
/// <see cref="RelayRequestMetric"/> on completion.
/// <para>
/// Performance: uses a single <see cref="Stopwatch"/> instance and records
/// elapsed ticks at each milestone — no allocations in the hot path.
/// </para>
/// </summary>
public sealed class RelayTimingContext
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    /// <summary>Gets the UTC time this context was created (request start).</summary>
    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;

    // ── Milestone ticks (captured from _sw.ElapsedTicks) ────────────

    /// <summary>Gets or sets the elapsed ticks when upstream headers arrived.</summary>
    public long? UpstreamHeadersTicks { get; set; }

    /// <summary>Gets or sets the elapsed ticks when first byte was read from upstream.</summary>
    public long? FirstByteFromUpstreamTicks { get; set; }

    /// <summary>Gets or sets the elapsed ticks when first byte was written to client.</summary>
    public long? FirstByteToClientTicks { get; set; }

    // ── Final state ─────────────────────────────────────────────────

    /// <summary>Gets or sets total bytes sent to the client.</summary>
    public long BytesSent { get; set; }

    /// <summary>Gets or sets the relay type.</summary>
    public RelayType RelayType { get; set; }

    /// <summary>Gets or sets the media kind.</summary>
    public MediaKind MediaKind { get; set; } = MediaKind.Unknown;

    /// <summary>Gets or sets the image source type (images only).</summary>
    public ImageSourceType ImageSourceType { get; set; } = ImageSourceType.Unknown;

    /// <summary>Gets or sets the channel ID if applicable.</summary>
    public string? ChannelId { get; set; }

    /// <summary>Gets or sets the upstream HTTP status code.</summary>
    public int? UpstreamStatusCode { get; set; }

    /// <summary>Gets or sets the client-facing HTTP status code.</summary>
    public int ClientStatusCode { get; set; }

    /// <summary>Gets or sets the failure reason.</summary>
    public RelayFailureReason FailureReason { get; set; } = RelayFailureReason.None;

    /// <summary>Gets or sets a value indicating whether the client cancelled.</summary>
    public bool ClientCancelled { get; set; }

    /// <summary>Gets or sets a value indicating whether upstream timed out.</summary>
    public bool UpstreamTimedOut { get; set; }

    /// <summary>Gets or sets the cache status (images).</summary>
    public RelayCacheStatus CacheStatus { get; set; } = RelayCacheStatus.NotApplicable;

    /// <summary>Gets or sets a value indicating whether upstream response had ETag.</summary>
    public bool HadEtag { get; set; }

    /// <summary>Gets or sets a value indicating whether upstream response had Last-Modified.</summary>
    public bool HadLastModified { get; set; }

    /// <summary>Gets or sets a value indicating whether upstream returned 304.</summary>
    public bool WasNotModified304 { get; set; }

    /// <summary>Gets or sets the Content-Type.</summary>
    public string? ContentType { get; set; }

    /// <summary>Gets or sets a value indicating whether range request.</summary>
    public bool WasRangeRequest { get; set; }

    /// <summary>Gets or sets a value indicating whether Content-Length was present.</summary>
    public bool HasContentLength { get; set; }

    /// <summary>Gets or sets Content-Length if known.</summary>
    public long? ContentLength { get; set; }

    /// <summary>Gets or sets stream ended-by reason.</summary>
    public StreamEndedBy? EndedBy { get; set; }

    /// <summary>Gets or sets active streams at start (streams only).</summary>
    public int? ParallelActiveStreamCountAtStart { get; set; }

    /// <summary>Records the upstream-headers-arrived milestone.</summary>
    public void MarkUpstreamHeaders() => UpstreamHeadersTicks = _sw.ElapsedTicks;

    /// <summary>Records the first-byte-from-upstream milestone.</summary>
    public void MarkFirstByteFromUpstream() => FirstByteFromUpstreamTicks ??= _sw.ElapsedTicks;

    /// <summary>Records the first-byte-to-client milestone.</summary>
    public void MarkFirstByteToClient() => FirstByteToClientTicks ??= _sw.ElapsedTicks;

    /// <summary>
    /// Freezes timing and builds a <see cref="RelayRequestMetric"/> for persistence.
    /// </summary>
    /// <returns>A new metric instance with all timing data captured.</returns>
    public RelayRequestMetric ToMetric()
    {
        _sw.Stop();
        var totalMs = _sw.Elapsed.TotalMilliseconds;
        var tickFreq = (double)Stopwatch.Frequency;

        double? TicksToMs(long? ticks) => ticks.HasValue ? ticks.Value / tickFreq * 1000.0 : null;

        var startupLatency = TicksToMs(FirstByteToClientTicks);
        var sessionDuration = RelayType == RelayType.Stream ? totalMs : (double?)null;
        var avgBps = totalMs > 0 && BytesSent > 0 ? BytesSent / (totalMs / 1000.0) : (double?)null;

        var outcome = FailureReason == RelayFailureReason.None
            ? (ClientCancelled ? "cancelled" : "success")
            : "failure";

        return new RelayRequestMetric
        {
            CreatedAtUtc = StartedAtUtc,
            RelayType = this.RelayType.ToString().ToLowerInvariant(),
            MediaKind = this.MediaKind.ToString(),
            ImageSourceType = this.RelayType == RelayType.Image ? this.ImageSourceType.ToString() : null,
            ChannelId = this.ChannelId,
            TotalDurationMs = totalMs,
            UpstreamHeadersDurationMs = TicksToMs(UpstreamHeadersTicks),
            FirstByteFromUpstreamDurationMs = TicksToMs(FirstByteFromUpstreamTicks),
            FirstByteToClientDurationMs = TicksToMs(FirstByteToClientTicks),
            StartupLatencyMs = startupLatency,
            SessionDurationMs = sessionDuration,
            BytesSent = BytesSent,
            AverageBytesPerSecond = avgBps,
            UpstreamStatusCode = UpstreamStatusCode,
            ClientStatusCode = ClientStatusCode,
            FinalOutcome = outcome,
            FailureReason = this.FailureReason.ToString(),
            ClientCancelled = ClientCancelled,
            UpstreamTimedOut = UpstreamTimedOut,
            CacheStatus = CacheStatus.ToString(),
            HadEtag = HadEtag,
            HadLastModified = HadLastModified,
            WasNotModified304 = WasNotModified304,
            ContentType = ContentType,
            WasRangeRequest = WasRangeRequest,
            HasContentLength = HasContentLength,
            ContentLength = this.ContentLength,
            EndedBy = EndedBy?.ToString(),
            StartupFailedWithin5Seconds = startupLatency.HasValue && startupLatency > 5000 && outcome == "failure",
            ParallelActiveStreamCountAtStart = ParallelActiveStreamCountAtStart,
        };
    }
}
