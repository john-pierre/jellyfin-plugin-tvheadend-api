using System;
using Jellyfin.Plugin.TvHeadendApi.Configuration;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Configuration;

/// <summary>
/// Provides access to the current <see cref="PluginConfiguration"/> without direct <see cref="Plugin.Instance"/> coupling.
/// </summary>
public sealed class ConfigurationProvider
{
    private readonly Func<PluginConfiguration?> _resolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationProvider"/> class.
    /// </summary>
    /// <param name="resolver">Delegate that resolves the current configuration.</param>
    public ConfigurationProvider(Func<PluginConfiguration?> resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>
    /// Gets the current plugin configuration, or <c>null</c> when the plugin instance is unavailable.
    /// </summary>
    public PluginConfiguration? Configuration => _resolver();
}
