using System;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Logging;

/// <summary>
/// A transparent <see cref="ILogger{T}"/> decorator that the plugin registers in place of the default
/// <c>ILogger&lt;&gt;</c>. It forwards every call unchanged to the real (Serilog-backed) logger, and
/// additionally persists entries from plugin-namespace categories to <see cref="PluginLogService"/>
/// so they appear in the dashboard log view.
/// </summary>
/// <remarks>
/// This is the only reliable way to capture plugin logs under Jellyfin's logging model: Jellyfin builds
/// its <c>SerilogLoggerFactory</c> at host startup with <c>writeToProviders:false</c>, so a plugin-registered
/// <see cref="ILoggerProvider"/> is never wired into the pipeline and receives nothing. Decorating
/// <c>ILogger&lt;&gt;</c> at the DI level intercepts the loggers the plugin's own services actually use.
/// Non-plugin (host) categories are forwarded only — never persisted — so host logging behaviour is
/// unchanged. <see cref="PluginLogService"/> is resolved lazily to avoid a construction cycle (it itself
/// depends on <c>ILogger&lt;PluginLogService&gt;</c>).
/// </remarks>
/// <typeparam name="T">The category type.</typeparam>
internal sealed class PluginPersistingLogger<T> : ILogger<T>
{
    private const string PluginNamespacePrefix = "Jellyfin.Plugin.TvHeadendApi";

    private static readonly string CategoryName = typeof(T).FullName ?? typeof(T).Name;
    private static readonly bool IsPluginCategory = CategoryName.StartsWith(PluginNamespacePrefix, StringComparison.Ordinal);

    private readonly ILogger _inner;
    private readonly ConfigurationProvider _configProvider;
    private readonly IServiceProvider _serviceProvider;
    private PluginLogService? _logService;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginPersistingLogger{T}"/> class.
    /// </summary>
    /// <param name="loggerFactory">The host logger factory used to obtain the real inner logger.</param>
    /// <param name="configProvider">Provides the live plugin configuration.</param>
    /// <param name="serviceProvider">Used to resolve <see cref="PluginLogService"/> lazily.</param>
    public PluginPersistingLogger(ILoggerFactory loggerFactory, ConfigurationProvider configProvider, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _inner = loggerFactory.CreateLogger(CategoryName);
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => _inner.BeginScope(state);

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        // Always forward to the real logger first — host logging behaviour is never altered.
        _inner.Log(logLevel, eventId, state, exception, formatter);

        if (!IsPluginCategory)
        {
            return;
        }

        try
        {
            var config = _configProvider.Configuration;
            if (config != null && !config.StorePluginLogsInSqlite)
            {
                return;
            }

            var overrideLevel = config?.PluginLogLevelOverride ?? PluginLogLevel.JellyfinDefault;
            if (!PluginLogLevelPolicy.ShouldPersist(logLevel, overrideLevel))
            {
                return;
            }

            var service = _logService ??= _serviceProvider.GetService(typeof(PluginLogService)) as PluginLogService;
            if (service == null)
            {
                return;
            }

            service.EnqueuePluginLog(
                level: logLevel.ToString(),
                category: CategoryName,
                message: formatter(state, exception),
                exception: exception?.ToString(),
                eventId: eventId.Id != 0 ? eventId.ToString() : null);
        }
        catch
        {
            // Persistence is best-effort — never let it disturb the logging pipeline.
        }
    }
}
