using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Helper;

/// <summary>
/// Provides centralized <see cref="Meter"/> and instruments for plugin observability.
/// Uses the built-in <see cref="System.Diagnostics.Metrics"/> API (no external dependencies).
/// </summary>
/// <remarks>
/// Metrics can be consumed by any .NET metrics listener (e.g., dotnet-counters, OpenTelemetry, Prometheus).
/// See <c>docs/guides/observability.md</c> for usage instructions.
/// </remarks>
internal static class PluginMetrics
{
    /// <summary>
    /// Meter name used for all plugin instruments. Pass this to <c>dotnet-counters</c> or OTEL exporters.
    /// </summary>
    internal const string MeterName = "Jellyfin.Plugin.TvHeadendApi";

    private static readonly Meter PluginMeter = new(MeterName, "1.0.0");

    /// <summary>
    /// ActivitySource for distributed tracing spans.
    /// </summary>
    internal static readonly ActivitySource ActivitySource = new(MeterName);

    /// <summary>
    /// Counts total TVHeadend API calls.
    /// </summary>
    internal static readonly Counter<long> ApiCallCount =
        PluginMeter.CreateCounter<long>("tvh.api.calls", "calls", "Total TVHeadend HTTP API calls");

    /// <summary>
    /// Records TVHeadend API call duration in milliseconds.
    /// </summary>
    internal static readonly Histogram<double> ApiCallDuration =
        PluginMeter.CreateHistogram<double>("tvh.api.duration", "ms", "TVHeadend API call duration");

    /// <summary>
    /// Counts channel stream setup requests.
    /// </summary>
    internal static readonly Counter<long> StreamSetupCount =
        PluginMeter.CreateCounter<long>("tvh.stream.setup", "requests", "Channel stream setup requests");

    /// <summary>
    /// Records stream setup duration in milliseconds (including cache warm-up).
    /// </summary>
    internal static readonly Histogram<double> StreamSetupDuration =
        PluginMeter.CreateHistogram<double>("tvh.stream.setup.duration", "ms", "Stream setup duration including cache");

    /// <summary>
    /// Counts mediainfo cache hits.
    /// </summary>
    internal static readonly Counter<long> CacheHitCount =
        PluginMeter.CreateCounter<long>("tvh.cache.hit", "hits", "Mediainfo cache hits");

    /// <summary>
    /// Counts mediainfo cache misses (new cache file written).
    /// </summary>
    internal static readonly Counter<long> CacheMissCount =
        PluginMeter.CreateCounter<long>("tvh.cache.miss", "misses", "Mediainfo cache misses");

    /// <summary>
    /// Counts mediainfo cache invalidations (stale file deleted).
    /// </summary>
    internal static readonly Counter<long> CacheInvalidationCount =
        PluginMeter.CreateCounter<long>("tvh.cache.invalidation", "invalidations", "Mediainfo cache invalidations");

    /// <summary>
    /// Records EPG fetch duration in milliseconds.
    /// </summary>
    internal static readonly Histogram<double> EpgFetchDuration =
        PluginMeter.CreateHistogram<double>("tvh.epg.fetch.duration", "ms", "EPG data fetch duration");

    /// <summary>
    /// Counts channel list fetches.
    /// </summary>
    internal static readonly Counter<long> ChannelFetchCount =
        PluginMeter.CreateCounter<long>("tvh.channels.fetch", "calls", "Channel list fetch count");
}
