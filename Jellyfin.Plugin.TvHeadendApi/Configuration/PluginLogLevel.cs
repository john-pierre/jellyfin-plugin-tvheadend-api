namespace Jellyfin.Plugin.TvHeadendApi.Configuration;

/// <summary>
/// Plugin-specific log level override. Controls log verbosity for this plugin only,
/// without affecting Jellyfin's global logging configuration.
/// </summary>
public enum PluginLogLevel
{
    /// <summary>Follow Jellyfin's global logging configuration (default).</summary>
    JellyfinDefault = 0,

    /// <summary>Most verbose — includes all trace-level messages.</summary>
    Trace = 1,

    /// <summary>Diagnostic information useful for troubleshooting.</summary>
    Debug = 2,

    /// <summary>General operational messages.</summary>
    Information = 3,

    /// <summary>Potential issues that do not prevent operation.</summary>
    Warning = 4,

    /// <summary>Failures requiring attention.</summary>
    Error = 5,

    /// <summary>Severe failures that require immediate attention.</summary>
    Critical = 6,

    /// <summary>Suppress all plugin log output.</summary>
    None = 7,
}
