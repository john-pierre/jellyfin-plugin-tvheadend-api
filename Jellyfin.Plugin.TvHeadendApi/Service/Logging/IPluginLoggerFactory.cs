using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Logging;

/// <summary>
/// Abstraction for creating plugin-scoped loggers with configurable level override.
/// </summary>
public interface IPluginLoggerFactory
{
    /// <summary>Creates a named logger with plugin-specific level override.</summary>
    /// <param name="categoryName">The logger category name.</param>
    /// <returns>A scoped logger instance.</returns>
    ILogger CreateLogger(string categoryName);

    /// <summary>Creates a typed logger with plugin-specific level override.</summary>
    /// <typeparam name="T">The type whose name is used as category.</typeparam>
    /// <returns>A scoped logger instance.</returns>
    ILogger<T> CreateLogger<T>();
}
