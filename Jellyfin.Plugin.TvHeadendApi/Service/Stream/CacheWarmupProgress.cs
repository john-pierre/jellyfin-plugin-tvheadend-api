namespace Jellyfin.Plugin.TvHeadendApi.Service.Stream;

/// <summary>
/// Real-time progress update emitted during a bulk cache warmup operation.
/// </summary>
/// <param name="Current">1-based index of the channel currently being processed.</param>
/// <param name="Total">Total number of channels to process.</param>
/// <param name="ChannelName">Display name of the channel being probed.</param>
/// <param name="ChannelId">TVHeadend channel UUID.</param>
/// <param name="Status">Current step: <c>probing</c>, <c>cached</c>, <c>skipped</c>, or <c>failed</c>.</param>
/// <param name="Detail">Human-readable detail (e.g. codec info, error message).</param>
public sealed record CacheWarmupProgress(
    int Current,
    int Total,
    string ChannelName,
    string ChannelId,
    string Status,
    string? Detail);
