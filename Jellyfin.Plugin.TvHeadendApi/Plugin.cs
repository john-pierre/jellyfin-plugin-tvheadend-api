using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi;

/// <summary>
/// The main plugin class for the TVHeadEnd integration with Jellyfin.
/// This class initializes the plugin, manages its configuration, and handles WebSocket communication setup.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Logger instance for the plugin.
    /// </summary>
    private readonly ILogger<Plugin> _logger;

    /// <summary>
    /// The server application host instance for resolving dependencies and interacting with the server.
    /// </summary>
    private readonly IServerApplicationHost _applicationHost;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// This constructor sets up essential services like logging, configuration management, and dependency resolution.
    /// </summary>
    /// <param name="applicationPaths">Provides paths to the application environment.</param>
    /// <param name="xmlSerializer">Handles XML serialization for configuration files.</param>
    /// <param name="applicationHost">The server's application host for dependency resolution.</param>
    /// <param name="logger">Logger for logging messages related to the plugin.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        IServerApplicationHost applicationHost,
        ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        _applicationHost = applicationHost ?? throw new ArgumentNullException(nameof(applicationHost));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// This property provides a static reference to the plugin instance, making it accessible globally within the plugin's context.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the name of the plugin.
    /// This property provides a human-readable name that identifies the plugin within the Jellyfin server.
    /// The name is displayed in the Jellyfin web UI, allowing users to distinguish this plugin from others.
    /// </summary>
    public override string Name => "TvHeadendApi";

    /// <summary>
    /// Gets the unique identifier (GUID) for the plugin.
    /// This GUID serves as the unique key for the plugin, ensuring no conflicts with other plugins in the Jellyfin ecosystem.
    /// The GUID is essential for identifying this plugin during registration and configuration loading.
    /// </summary>
    public override Guid Id => Guid.Parse("ae9f5148-d656-43ab-ab83-94192f9e840a");

    /// <summary>
    /// Returns the list of plugin pages available in the Jellyfin web UI.
    /// This method defines the configuration page for the plugin, embedding the required HTML resource.
    /// </summary>
    /// <returns>
    /// An enumerable collection of <see cref="PluginPageInfo"/> objects that represent the plugin pages
    /// to be displayed in the Jellyfin web UI.
    /// </returns>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "TvHeadendApiConfig",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.ConfigPage.html", this.GetType().Namespace),
            },
        };
    }
}
