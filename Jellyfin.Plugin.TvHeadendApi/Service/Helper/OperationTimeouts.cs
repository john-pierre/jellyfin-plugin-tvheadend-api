// Timeout policies for different TVHeadend operation types.

using System;
using Jellyfin.Plugin.TvHeadendApi.Configuration;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Helper;

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

/// <summary>
/// Provides timeout durations per <see cref="OperationType"/>.
/// Falls back to defaults when no configuration is available.
/// </summary>
internal static class OperationTimeouts
{
    /// <summary>Default timeout for health/ping operations.</summary>
    internal static readonly TimeSpan Health = TimeSpan.FromSeconds(3);

    /// <summary>Default timeout for image/logo requests.</summary>
    internal static readonly TimeSpan Image = TimeSpan.FromSeconds(5);

    /// <summary>Default timeout for metadata operations (channels, EPG, profiles).</summary>
    internal static readonly TimeSpan Metadata = TimeSpan.FromSeconds(10);

    /// <summary>Default timeout for stream startup (headers read).</summary>
    internal static readonly TimeSpan StreamStartup = TimeSpan.FromSeconds(8);

    /// <summary>Default timeout for background refresh tasks.</summary>
    internal static readonly TimeSpan BackgroundRefresh = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets the timeout for the given operation type using defaults.
    /// </summary>
    /// <param name="type">The operation type.</param>
    /// <returns>The configured timeout duration.</returns>
    internal static TimeSpan GetTimeout(OperationType type) => type switch
    {
        OperationType.Health => Health,
        OperationType.Image => Image,
        OperationType.Metadata => Metadata,
        OperationType.StreamStartup => StreamStartup,
        OperationType.BackgroundRefresh => BackgroundRefresh,
        _ => Metadata,
    };

    /// <summary>
    /// Gets the timeout for the given operation type, respecting user-configured overrides.
    /// </summary>
    /// <param name="type">The operation type.</param>
    /// <param name="config">The plugin configuration (may be null).</param>
    /// <returns>The timeout duration from configuration or the default.</returns>
    internal static TimeSpan GetTimeout(OperationType type, PluginConfiguration? config)
    {
        if (config == null)
        {
            return GetTimeout(type);
        }

        return type switch
        {
            OperationType.Health => SafeSeconds(config.HealthTimeoutSeconds, Health),
            OperationType.Image => SafeSeconds(config.ImageTimeoutSeconds, Image),
            OperationType.Metadata => SafeSeconds(config.MetadataTimeoutSeconds, Metadata),
            OperationType.StreamStartup => SafeSeconds(config.StreamStartupTimeoutSeconds, StreamStartup),
            OperationType.BackgroundRefresh => SafeSeconds(config.BackgroundRefreshTimeoutSeconds, BackgroundRefresh),
            _ => SafeSeconds(config.MetadataTimeoutSeconds, Metadata),
        };
    }

    private static TimeSpan SafeSeconds(int seconds, TimeSpan fallback) =>
        seconds > 0 ? TimeSpan.FromSeconds(seconds) : fallback;
}
