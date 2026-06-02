// Dashboard DTO for relay observability — aggregated metrics returned by the dashboard API.

using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Relay;

/// <summary>
/// Aggregated relay metrics for the dashboard. Immutable snapshot built by the service layer.
/// </summary>
#pragma warning disable CA2227 // Collection properties should be read only — DTO for JSON serialization
public sealed class RelayMetricsSummary
{
    // ── General ─────────────────────────────────────────────────────

    /// <summary>Gets or sets the time range description (e.g. "last 24 hours").</summary>
    public string TimeRange { get; set; } = string.Empty;

    /// <summary>Gets or sets total relay requests in the time range.</summary>
    public long TotalRequests { get; set; }

    /// <summary>Gets or sets successful relay requests.</summary>
    public long SuccessfulRequests { get; set; }

    /// <summary>Gets or sets failed relay requests.</summary>
    public long FailedRequests { get; set; }

    /// <summary>Gets or sets cancelled relay requests.</summary>
    public long CancelledRequests { get; set; }

    /// <summary>Gets or sets the success rate (0–100).</summary>
    public double SuccessRate { get; set; }

    // ── Latency ─────────────────────────────────────────────────────

    /// <summary>Gets or sets latency percentiles for total duration.</summary>
    public LatencyPercentiles TotalDuration { get; set; } = new();

    /// <summary>Gets or sets latency percentiles for startup latency (streams).</summary>
    public LatencyPercentiles StartupLatency { get; set; } = new();

    /// <summary>Gets or sets average upstream connect duration in ms.</summary>
    public double? AvgUpstreamConnectMs { get; set; }

    /// <summary>Gets or sets average upstream headers duration in ms.</summary>
    public double? AvgUpstreamHeadersMs { get; set; }

    /// <summary>Gets or sets average first byte from upstream in ms.</summary>
    public double? AvgFirstByteFromUpstreamMs { get; set; }

    /// <summary>Gets or sets average first byte to client in ms.</summary>
    public double? AvgFirstByteToClientMs { get; set; }

    // ── Bandwidth ───────────────────────────────────────────────────

    /// <summary>Gets or sets total bytes transferred.</summary>
    public long TotalBytesTransferred { get; set; }

    /// <summary>Gets or sets average bytes per request.</summary>
    public double AvgBytesPerRequest { get; set; }

    /// <summary>Gets or sets current active relay streams (live state).</summary>
    public int ActiveStreams { get; set; }

    // ── Stream Health ───────────────────────────────────────────────

    /// <summary>Gets or sets startup failures within 5 seconds.</summary>
    public long StartupFailuresWithin5s { get; set; }

    /// <summary>Gets or sets average stream session duration in ms.</summary>
    public double? AvgStreamSessionDurationMs { get; set; }

    /// <summary>Gets or sets the stream ended-by reason distribution.</summary>
    public Dictionary<string, long> StreamEndedByDistribution { get; set; } = new();

    // ── Image Cache ─────────────────────────────────────────────────

    /// <summary>Gets or sets total image requests.</summary>
    public long ImageRequests { get; set; }

    /// <summary>Gets or sets cache hit count.</summary>
    public long CacheHits { get; set; }

    /// <summary>Gets or sets cache miss count.</summary>
    public long CacheMisses { get; set; }

    /// <summary>Gets or sets cache revalidated count.</summary>
    public long CacheRevalidated { get; set; }

    /// <summary>Gets or sets negative cache hit count.</summary>
    public long NegativeCacheHits { get; set; }

    /// <summary>Gets or sets cache hit ratio (0–100).</summary>
    public double CacheHitRatio { get; set; }

    /// <summary>Gets or sets a value indicating whether the on-disk image cache is enabled.</summary>
    public bool ImageCacheEnabled { get; set; }

    /// <summary>Gets or sets the number of image files currently in the on-disk cache.</summary>
    public long ImageCacheFileCount { get; set; }

    /// <summary>Gets or sets the total on-disk size of the image cache, in bytes.</summary>
    public long ImageCacheBytes { get; set; }

    /// <summary>Gets or sets the image-cache retention period in days.</summary>
    public int ImageCacheRetentionDays { get; set; }

    /// <summary>Gets or sets the number of image-cache write/read errors since startup (0 = healthy).</summary>
    public long ImageCacheErrors { get; set; }

    /// <summary>Gets or sets 304 response count.</summary>
    public long NotModified304Count { get; set; }

    // ── Errors ──────────────────────────────────────────────────────

    /// <summary>Gets or sets top failure reasons with counts.</summary>
    public Dictionary<string, long> TopFailureReasons { get; set; } = new();

    /// <summary>Gets or sets upstream status code distribution.</summary>
    public Dictionary<int, long> UpstreamStatusDistribution { get; set; } = new();

    /// <summary>Gets or sets recent errors (newest first, max 20).</summary>
    public IReadOnlyList<RelayErrorEntry> RecentErrors { get; set; } = Array.Empty<RelayErrorEntry>();

    /// <summary>Gets or sets slowest requests (max 20).</summary>
    public IReadOnlyList<RelaySlowestEntry> SlowestRequests { get; set; } = Array.Empty<RelaySlowestEntry>();

    // ── Trend ───────────────────────────────────────────────────────

    /// <summary>Gets or sets hourly trend buckets.</summary>
    public IReadOnlyList<RelayTrendBucket> HourlyTrend { get; set; } = Array.Empty<RelayTrendBucket>();

    /// <summary>Gets or sets the trend bucket granularity for the selected range: "hour" or "day".</summary>
    public string TrendGranularity { get; set; } = "hour";

    // ── Token Security ───────────────────────────────────────────────

    /// <summary>Gets or sets channels that had stream failures in the selected range (worst first).</summary>
    public IReadOnlyList<ProblemChannel> ProblemChannels { get; set; } = Array.Empty<ProblemChannel>();

    /// <summary>Gets or sets total relay tokens issued.</summary>
    public long TokensIssuedTotal { get; set; }

    /// <summary>Gets or sets stream tokens issued.</summary>
    public long StreamTokensIssued { get; set; }

    /// <summary>Gets or sets image tokens issued.</summary>
    public long ImageTokensIssued { get; set; }

    /// <summary>Gets or sets total token validations.</summary>
    public long TokenValidationsTotal { get; set; }

    /// <summary>Gets or sets total token validation failures.</summary>
    public long TokenValidationFailures { get; set; }

    /// <summary>Gets or sets token failures grouped by reason.</summary>
    public Dictionary<string, long> TokenFailuresByReason { get; set; } = new();
}

/// <summary>
/// Latency percentile values in milliseconds.
/// </summary>
public sealed class LatencyPercentiles
{
    /// <summary>Gets or sets the average in ms.</summary>
    public double Avg { get; set; }

    /// <summary>Gets or sets the median (p50) in ms.</summary>
    public double Median { get; set; }

    /// <summary>Gets or sets the 95th percentile in ms.</summary>
    public double P95 { get; set; }

    /// <summary>Gets or sets the 99th percentile in ms.</summary>
    public double P99 { get; set; }
}

/// <summary>
/// A recent relay error for the dashboard error table.
/// </summary>
public sealed class RelayErrorEntry
{
    /// <summary>Gets or sets the UTC timestamp.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Gets or sets the relay type.</summary>
    public string RelayType { get; set; } = string.Empty;

    /// <summary>Gets or sets the failure reason.</summary>
    public string FailureReason { get; set; } = string.Empty;

    /// <summary>Gets or sets the upstream status code.</summary>
    public int? UpstreamStatusCode { get; set; }

    /// <summary>Gets or sets the total duration in ms.</summary>
    public double TotalDurationMs { get; set; }

    /// <summary>Gets or sets the channel ID.</summary>
    public string? ChannelId { get; set; }
}

/// <summary>
/// A slow relay request for the dashboard slowest-requests table.
/// </summary>
public sealed class RelaySlowestEntry
{
    /// <summary>Gets or sets the UTC timestamp.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Gets or sets the relay type.</summary>
    public string RelayType { get; set; } = string.Empty;

    /// <summary>Gets or sets total duration in ms. For streams this is the watch time, not a slowness signal.</summary>
    public double TotalDurationMs { get; set; }

    /// <summary>
    /// Gets or sets the startup latency in ms (time until first byte reached the client).
    /// This is the meaningful "slowness" for streams; <c>null</c> when no byte was ever delivered.
    /// </summary>
    public double? StartupLatencyMs { get; set; }

    /// <summary>Gets or sets bytes sent.</summary>
    public long BytesSent { get; set; }

    /// <summary>Gets or sets the final outcome.</summary>
    public string FinalOutcome { get; set; } = string.Empty;

    /// <summary>Gets or sets the channel ID.</summary>
    public string? ChannelId { get; set; }
}

/// <summary>
/// A single hourly trend bucket for charts.
/// </summary>
public sealed class RelayTrendBucket
{
    /// <summary>Gets or sets the bucket hour (UTC).</summary>
    public DateTime HourUtc { get; set; }

    /// <summary>Gets or sets total requests in this hour.</summary>
    public long Requests { get; set; }

    /// <summary>Gets or sets the number of stream relay requests in this bucket.</summary>
    public long StreamRequests { get; set; }

    /// <summary>Gets or sets the peak concurrent stream count observed in this bucket.</summary>
    public int PeakConcurrentStreams { get; set; }

    /// <summary>Gets or sets failed requests in this hour.</summary>
    public long Failures { get; set; }

    /// <summary>Gets or sets average startup latency in ms.</summary>
    public double? AvgStartupLatencyMs { get; set; }

    /// <summary>Gets or sets total bytes transferred.</summary>
    public long BytesTransferred { get; set; }

    /// <summary>Gets or sets cache hit ratio (0–100).</summary>
    public double? CacheHitRatio { get; set; }
}

/// <summary>
/// A channel that experienced stream relay failures, for the dashboard "problem channels" view.
/// </summary>
public sealed class ProblemChannel
{
    /// <summary>Gets or sets the channel display name (falls back to the UUID if not yet known).</summary>
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>Gets or sets the TVHeadend channel UUID.</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of failed stream requests for this channel in the range.</summary>
    public long FailedRequests { get; set; }

    /// <summary>Gets or sets the total stream requests for this channel in the range.</summary>
    public long TotalRequests { get; set; }

    /// <summary>Gets or sets the most recent failure reason for this channel.</summary>
    public string LastFailureReason { get; set; } = string.Empty;
}
