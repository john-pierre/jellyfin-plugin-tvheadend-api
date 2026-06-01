using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Logging;

/// <summary>
/// Pure decision logic for the plugin-specific log-level override
/// (<see cref="PluginConfiguration.PluginLogLevelOverride"/>). Shared by the
/// log-persistence pipeline and unit-tested in isolation.
/// </summary>
internal static class PluginLogLevelPolicy
{
    /// <summary>
    /// Determines whether a log entry of the given framework level should be persisted,
    /// given the configured plugin override.
    /// </summary>
    /// <param name="logLevel">The framework level of the entry.</param>
    /// <param name="overrideLevel">The configured plugin-specific override.</param>
    /// <returns>
    /// <c>true</c> to persist the entry. <see cref="PluginLogLevel.JellyfinDefault"/> persists
    /// everything Jellyfin's framework already delivered; any other value raises the minimum level.
    /// </returns>
    public static bool ShouldPersist(LogLevel logLevel, PluginLogLevel overrideLevel)
    {
        if (logLevel == LogLevel.None)
        {
            return false;
        }

        if (overrideLevel == PluginLogLevel.JellyfinDefault)
        {
            return true;
        }

        return logLevel >= MapToLogLevel(overrideLevel);
    }

    /// <summary>
    /// Maps the plugin-specific level enum to the framework <see cref="LogLevel"/>.
    /// </summary>
    /// <param name="pluginLevel">The plugin-specific level.</param>
    /// <returns>The equivalent framework <see cref="LogLevel"/>.</returns>
    public static LogLevel MapToLogLevel(PluginLogLevel pluginLevel)
    {
        return pluginLevel switch
        {
            PluginLogLevel.Trace => LogLevel.Trace,
            PluginLogLevel.Debug => LogLevel.Debug,
            PluginLogLevel.Information => LogLevel.Information,
            PluginLogLevel.Warning => LogLevel.Warning,
            PluginLogLevel.Error => LogLevel.Error,
            PluginLogLevel.Critical => LogLevel.Critical,
            PluginLogLevel.None => LogLevel.None,
            _ => LogLevel.None,
        };
    }
}
