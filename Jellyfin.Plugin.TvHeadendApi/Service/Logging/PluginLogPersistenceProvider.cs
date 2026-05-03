// ILoggerProvider that intercepts log calls for plugin-namespace categories
// and enqueues them to PluginLogService for SQLite persistence.

using System;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Logging;

/// <summary>
/// Logger provider that captures log entries from all plugin-namespace loggers
/// and persists them to SQLite via <see cref="PluginLogService"/>.
/// Only intercepts categories starting with "Jellyfin.Plugin.TvHeadendApi".
/// </summary>
[ProviderAlias("TvHeadendApiPlugin")]
internal sealed class PluginLogPersistenceProvider : ILoggerProvider
{
    private const string PluginNamespacePrefix = "Jellyfin.Plugin.TvHeadendApi";

    private readonly PluginLogService _logService;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginLogPersistenceProvider"/> class.
    /// </summary>
    /// <param name="logService">The plugin log service for SQLite persistence.</param>
    public PluginLogPersistenceProvider(PluginLogService logService)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
    {
        if (categoryName.StartsWith(PluginNamespacePrefix, StringComparison.Ordinal))
        {
            return new PersistenceLogger(_logService, categoryName);
        }

        return NullLogger.Instance;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to dispose — PluginLogService lifetime is managed by DI.
    }

    /// <summary>
    /// Logger that enqueues entries to <see cref="PluginLogService"/> without filtering.
    /// Filtering is handled by the logging framework based on Jellyfin's configuration.
    /// </summary>
    private sealed class PersistenceLogger : ILogger
    {
        private readonly PluginLogService _logService;
        private readonly string _categoryName;

        public PersistenceLogger(PluginLogService logService, string categoryName)
        {
            _logService = logService;
            _categoryName = categoryName;
        }

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        /// <summary>
        /// Always returns true — the logging framework applies level filtering before calling Log.
        /// This provider only persists; it does not filter.
        /// </summary>
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.None)
            {
                return;
            }

            try
            {
                var message = formatter(state, exception);
                _logService.EnqueuePluginLog(
                    level: logLevel.ToString(),
                    category: _categoryName,
                    message: message,
                    exception: exception?.ToString(),
                    eventId: eventId.Id != 0 ? eventId.ToString() : null);
            }
            catch
            {
                // Never let persistence errors affect the logging pipeline.
            }
        }
    }

    /// <summary>
    /// No-op logger for non-plugin categories.
    /// </summary>
    private sealed class NullLogger : ILogger
    {
        public static readonly NullLogger Instance = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }
}
