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

    /// <summary>
    /// Gets or sets startup latency percentiles grouped by mediainfo cache status
    /// (<c>hit</c>, <c>miss</c>, <c>mismatch</c>, <c>restored</c>, <c>unknown</c>) —
    /// the warm-vs-cold-cache zapping breakdown.
    /// </summary>
    public Dictionary<string, LatencyPercentiles> StartupLatencyByCacheStatus { get; set; } = new();

    /// <summary>Gets or sets startup latency percentiles grouped by effective TVHeadend profile.</summary>
    public Dictionary<string, LatencyPercentiles> StartupLatencyByProfile { get; set; } = new();

    /// <summary>
    /// Gets or sets the percentage (0–100) of Live TV sessions in the last 24 hours that
    /// played via Direct Play (from Jellyfin viewing statistics). Null when no sessions.
    /// </summary>
    public double? DirectPlayPercent24h { get; set; }

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

    /// <summary>Gets or sets channels that had stream failures in the selected range (worst first).</summary>
    public IReadOnlyList<ProblemChannel> ProblemChannels { get; set; } = Array.Empty<ProblemChannel>();

    // Token statistics intentionally live in RelayTokenStatistics (GET /TvHeadendApi/Dashboard/Tokens);
    // the token counter fields that used to sit here were never written by any code path.
}
