using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi;

/// <summary>
/// Represents the Jellyfin plugin entry point and exposes embedded admin pages.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
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
        ArgumentNullException.ThrowIfNull(applicationHost);
        ArgumentNullException.ThrowIfNull(logger);
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the Jellyfin application cache directory path.
    /// Used by stream services to read and write Jellyfin media-info cache files.
    /// </summary>
    public string CachePath => ApplicationPaths.CachePath;

    /// <summary>
    /// Gets the display name of the plugin.
    /// </summary>
    public override string Name => "TvHeadendApi";

    /// <summary>
    /// Gets the stable plugin identifier.
    /// </summary>
    public override Guid Id => Guid.Parse("ae9f5148-d656-43ab-ab83-94192f9e840a");

    /// <summary>
    /// Returns the plugin pages available in the Jellyfin web UI.
    /// </summary>
    /// <returns>The configuration page and the dashboard page.</returns>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = this.GetType().Namespace;
        return new[]
        {
            new PluginPageInfo
            {
                Name = "TvHeadendApiConfig",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.ConfigPage.html", ns),
            },
            new PluginPageInfo
            {
                Name = "TvHeadendDashboard",
                DisplayName = "TvHeadend",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Page.DashboardPage.html", ns),
                EnableInMainMenu = true,
                MenuSection = "Live TV",
                MenuIcon = "live_tv",
            },

            // Chart.js is served from the plugin assembly instead of a CDN so the dashboard's
            // analytics section also renders on air-gapped or egress-filtered servers. Jellyfin
            // derives the content type from the resource path extension, so the ".js" suffix is
            // what makes this load as a script.
            new PluginPageInfo
            {
                Name = "TvHeadendChartJs",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Page.lib.chart.umd.min.js", ns),
            },
        };
    }
}
