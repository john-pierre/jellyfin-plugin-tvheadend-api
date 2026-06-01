// ILoggerProvider that intercepts log calls for plugin-namespace categories
// and enqueues them to PluginLogService for SQLite persistence.

using System;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Logging;

/// <summary>
/// Logger provider that captures log entries from all plugin-namespace loggers
/// and persists them to SQLite via <see cref="PluginLogService"/>.
/// Only intercepts categories starting with "Jellyfin.Plugin.TvHeadendApi".
/// </summary>
/// <remarks>
/// Honours the <see cref="PluginConfiguration.PluginLogLevelOverride"/> setting: when set to a
/// concrete level (not <see cref="PluginLogLevel.JellyfinDefault"/>), entries below that level are
/// dropped before persistence. This raises the minimum level of plugin entries captured to the
/// dashboard independently of Jellyfin's global log level; it cannot capture entries more verbose
/// than Jellyfin's configured level, because those never reach this provider.
/// </remarks>
[ProviderAlias("TvHeadendApiPlugin")]
internal sealed class PluginLogPersistenceProvider : ILoggerProvider
{
    private const string PluginNamespacePrefix = "Jellyfin.Plugin.TvHeadendApi";

    private readonly PluginLogService _logService;
    private readonly ConfigurationProvider _configProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginLogPersistenceProvider"/> class.
    /// </summary>
    /// <param name="logService">The plugin log service for SQLite persistence.</param>
    /// <param name="configProvider">Provides the live plugin configuration for the level override.</param>
    public PluginLogPersistenceProvider(PluginLogService logService, ConfigurationProvider configProvider)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
    {
        if (categoryName.StartsWith(PluginNamespacePrefix, StringComparison.Ordinal))
        {
            return new PersistenceLogger(_logService, _configProvider, categoryName);
        }

        return NullLogger.Instance;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to dispose — PluginLogService lifetime is managed by DI.
    }

    /// <summary>
    /// Logger that enqueues entries to <see cref="PluginLogService"/>, applying the configured
    /// plugin-specific level override (Jellyfin's framework has already applied global filtering).
    /// </summary>
    private sealed class PersistenceLogger : ILogger
    {
        private readonly PluginLogService _logService;
        private readonly ConfigurationProvider _configProvider;
        private readonly string _categoryName;

        public PersistenceLogger(PluginLogService logService, ConfigurationProvider configProvider, string categoryName)
        {
            _logService = logService;
            _configProvider = configProvider;
            _categoryName = categoryName;
        }

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        /// <summary>
        /// Returns true when the level passes the configured plugin override (or no override is set).
        /// </summary>
        public bool IsEnabled(LogLevel logLevel)
        {
            var overrideLevel = _configProvider.Configuration?.PluginLogLevelOverride ?? PluginLogLevel.JellyfinDefault;
            return PluginLogLevelPolicy.ShouldPersist(logLevel, overrideLevel);
        }

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
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
