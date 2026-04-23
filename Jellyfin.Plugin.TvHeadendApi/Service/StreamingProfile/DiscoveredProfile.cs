// Public DTO representing a discovered TVHeadend streaming profile.

namespace Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;

/// <summary>
/// A discovered TVHeadend streaming profile with its UUID and display name.
/// </summary>
public sealed record DiscoveredProfile(string Key, string Name);
