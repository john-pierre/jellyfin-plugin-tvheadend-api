using System;
using Jellyfin.Plugin.TvHeadendApi.Configuration;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Resilience;

/// <summary>
/// Defines timeout categories for TVHeadend API operations.
/// Each category has a sensible default that can be overridden via configuration.
/// </summary>
public enum OperationType
{
    /// <summary>Health check / ping — very short.</summary>
    Health,

    /// <summary>Image or logo fetch — short.</summary>
    Image,

    /// <summary>EPG, channel list, metadata queries — medium.</summary>
    Metadata,

    /// <summary>Stream startup (headers only) — short-to-medium.</summary>
    StreamStartup,

    /// <summary>Background refresh tasks — medium with cancellation support.</summary>
    BackgroundRefresh,
}
