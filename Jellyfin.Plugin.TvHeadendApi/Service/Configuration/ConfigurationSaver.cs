using System;
using Jellyfin.Plugin.TvHeadendApi.Configuration;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Configuration;

/// <summary>
/// Persists plugin-configuration mutations without direct <see cref="Plugin.Instance"/> coupling.
/// </summary>
internal sealed class ConfigurationSaver
{
    private readonly Action<Action<PluginConfiguration>> _saver;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationSaver"/> class.
    /// </summary>
    /// <param name="saver">Delegate that applies a mutation to the configuration and saves it.</param>
    public ConfigurationSaver(Action<Action<PluginConfiguration>> saver)
    {
        _saver = saver ?? throw new ArgumentNullException(nameof(saver));
    }

    /// <summary>
    /// Applies the given mutation to the plugin configuration and persists the change.
    /// </summary>
    /// <param name="mutate">Action that modifies the configuration.</param>
    public void Save(Action<PluginConfiguration> mutate) => _saver(mutate);
}
