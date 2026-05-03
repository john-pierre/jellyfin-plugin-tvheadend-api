using System;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Logging;

/// <summary>
/// An <see cref="ILogger"/> wrapper that applies a plugin-specific log level override.
/// When <see cref="PluginLogLevel.JellyfinDefault"/> is configured, it passes through
/// to the inner logger unchanged. Otherwise, it filters based on the configured override.
/// Also enqueues qualifying log entries to <see cref="PluginLogService"/> for persistence.
/// </summary>
internal sealed class PluginScopedLogger : ILogger
{
    private readonly ILogger _inner;
    private readonly ConfigurationProvider _configProvider;
    private readonly PluginLogService? _logService;
    private readonly string _categoryName;

    public PluginScopedLogger(
        ILogger inner,
        ConfigurationProvider configProvider,
        PluginLogService? logService,
        string categoryName)
    {
        _inner = inner;
        _configProvider = configProvider;
        _logService = logService;
        _categoryName = categoryName;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => _inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel)
    {
        var config = _configProvider.Configuration;
        if (config == null || config.PluginLogLevelOverride == PluginLogLevel.JellyfinDefault)
        {
            return _inner.IsEnabled(logLevel);
        }

        var minLevel = MapToLogLevel(config.PluginLogLevelOverride);
        return logLevel >= minLevel;
    }

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

        // Delegate to the real Jellyfin logger
        _inner.Log(logLevel, eventId, state, exception, formatter);

        // Note: Plugin log persistence to SQLite is handled by PluginLogPersistenceProvider
        // (registered as ILoggerProvider), which intercepts all plugin-namespace log calls
        // automatically. No need to enqueue here — that would cause duplicate entries.
    }

    private static LogLevel MapToLogLevel(PluginLogLevel pluginLevel)
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

/// <summary>
/// Factory that produces <see cref="PluginScopedLogger"/> instances for plugin categories.
/// Registered into DI so that services can request <see cref="IPluginLoggerFactory"/>
/// and obtain loggers with plugin-specific level override.
/// </summary>
internal sealed class PluginLoggerFactory : IPluginLoggerFactory
{
    private readonly ILoggerFactory _innerFactory;
    private readonly ConfigurationProvider _configProvider;
    private readonly PluginLogService? _logService;

    public PluginLoggerFactory(
        ILoggerFactory innerFactory,
        ConfigurationProvider configProvider,
        PluginLogService? logService = null)
    {
        _innerFactory = innerFactory ?? throw new ArgumentNullException(nameof(innerFactory));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _logService = logService;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
    {
        var inner = _innerFactory.CreateLogger(categoryName);
        return new PluginScopedLogger(inner, _configProvider, _logService, categoryName);
    }

    /// <inheritdoc />
    public ILogger<T> CreateLogger<T>()
    {
        var categoryName = typeof(T).FullName ?? typeof(T).Name;
        var logger = CreateLogger(categoryName);
        return new TypedLogger<T>(logger);
    }

    /// <summary>
    /// Typed ILogger wrapper so we can return <see cref="ILogger{T}"/>.
    /// </summary>
    /// <typeparam name="T">The type whose name is used as logger category.</typeparam>
    private sealed class TypedLogger<T> : ILogger<T>
    {
        private readonly ILogger _inner;

        public TypedLogger(ILogger inner) => _inner = inner;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => _inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
