// Enum representing the health status of the plugin's SQLite database.

namespace Jellyfin.Plugin.TvHeadendApi.Service.Database;

/// <summary>
/// Represents the overall health status of the plugin's SQLite database.
/// </summary>
public enum DatabaseHealthStatus
{
    /// <summary>No health data available yet (startup).</summary>
    Unknown,

    /// <summary>Database is operational with all integrity checks passing.</summary>
    Healthy,

    /// <summary>Database is operational but experienced recent errors or slow queries.</summary>
    Degraded,

    /// <summary>Database file cannot be opened or connection failed.</summary>
    Unavailable,

    /// <summary>Integrity check detected corruption.</summary>
    Corrupt,

    /// <summary>Recovery is in progress (corrupt file moved aside, recreating schema).</summary>
    Recovering,

    /// <summary>Database was recovered from a corrupt state and is now operational.</summary>
    Recovered,
}
