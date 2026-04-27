using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Stream;

/// <summary>
/// Result of a bulk cache warmup operation.
/// </summary>
/// <param name="TotalChannels">Total number of channels discovered.</param>
/// <param name="Warmed">Number of channels whose caches were newly written.</param>
/// <param name="AlreadyCached">Number of channels that already had valid caches.</param>
/// <param name="Failed">Number of channels where cache writing failed.</param>
/// <param name="Errors">Per-channel error messages (if any).</param>
public sealed record CacheWarmupResult(int TotalChannels, int Warmed, int AlreadyCached, int Failed, List<string> Errors);
