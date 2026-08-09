// Snapshot of the in-process mediainfo cache counters since plugin start.

namespace Jellyfin.Plugin.TvHeadendApi.Service.Stream;

/// <summary>
/// Immutable snapshot of the mediainfo cache counters accumulated since plugin start.
/// Mirrors the OpenTelemetry counters (<c>tvh.cache.*</c>) so the dashboard can show
/// them without a metrics listener.
/// </summary>
/// <param name="Hits">Cache hits — an existing, matching cache file was used (includes stream-build reuse hits).</param>
/// <param name="Misses">Cache misses — no cache file existed for the channel.</param>
/// <param name="Mismatches">Cache mismatches — a file existed but did not match the effective profile.</param>
/// <param name="Invalidations">Cache invalidations — stale or unreadable files deleted.</param>
public sealed record MediaInfoCacheCounters(long Hits, long Misses, long Mismatches, long Invalidations);
