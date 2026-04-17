using System;
using Jellyfin.Plugin.TvHeadendApi.Configuration;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Helper;

/// <summary>
/// Provides access to the plugin cache path without direct <see cref="Plugin.Instance"/> coupling.
/// </summary>
internal sealed class CachePathProvider
{
    private readonly Func<string?> _resolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="CachePathProvider"/> class.
    /// </summary>
    /// <param name="resolver">Delegate that resolves the current cache path.</param>
    public CachePathProvider(Func<string?> resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>Gets the current plugin cache path, or <c>null</c> if unavailable.</summary>
    public string? Path => _resolver();
}

/// <summary>
/// Provides access to the plugin data folder path without direct <see cref="Plugin.Instance"/> coupling.
/// </summary>
internal sealed class DataFolderPathProvider
{
    private readonly Func<string?> _resolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="DataFolderPathProvider"/> class.
    /// </summary>
    /// <param name="resolver">Delegate that resolves the current data folder path.</param>
    public DataFolderPathProvider(Func<string?> resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>Gets the current plugin data folder path, or <c>null</c> if unavailable.</summary>
    public string? Path => _resolver();
}

/// <summary>
/// Provides access to the current <see cref="PluginConfiguration"/> without direct <see cref="Plugin.Instance"/> coupling.
/// </summary>
internal sealed class PluginConfigurationProvider
{
    private readonly Func<PluginConfiguration?> _resolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfigurationProvider"/> class.
    /// </summary>
    /// <param name="resolver">Delegate that resolves the current configuration.</param>
    public PluginConfigurationProvider(Func<PluginConfiguration?> resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>Gets the current plugin configuration, or <c>null</c> if unavailable.</summary>
    public PluginConfiguration? Configuration => _resolver();
}

/// <summary>
/// Provides a mechanism to persist configuration changes without direct <see cref="Plugin.Instance"/> coupling.
/// </summary>
internal sealed class PluginConfigurationSaver
{
    private readonly Action<Action<PluginConfiguration>> _saver;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfigurationSaver"/> class.
    /// </summary>
    /// <param name="saver">Delegate that applies a mutation to the configuration and saves it.</param>
    public PluginConfigurationSaver(Action<Action<PluginConfiguration>> saver)
    {
        _saver = saver ?? throw new ArgumentNullException(nameof(saver));
    }

    /// <summary>
    /// Applies the given mutation to the plugin configuration and persists the change.
    /// </summary>
    /// <param name="mutate">Action that modifies the configuration.</param>
    public void Save(Action<PluginConfiguration> mutate) => _saver(mutate);
}
