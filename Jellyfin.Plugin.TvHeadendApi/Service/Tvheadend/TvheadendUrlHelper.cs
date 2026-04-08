using System;
using Jellyfin.Plugin.TvHeadendApi.Configuration;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;

/// <summary>
/// Builds normalized TVHeadend base and endpoint URLs.
/// </summary>
internal static class TvheadendUrlHelper
{
    public static string GetWebRoot(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return string.IsNullOrWhiteSpace(config.Webroot) ? "/" : config.Webroot.TrimEnd('/') + "/";
    }

    public static string GetBaseUrl(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return $"{(config.UseSSL ? "https" : "http")}://{config.Host}:{config.Port}";
    }

    public static string BuildEndpointUrl(PluginConfiguration config, string endpoint)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var webRoot = GetWebRoot(config);
        return $"{GetBaseUrl(config)}{webRoot}{endpoint.TrimStart('/')}";
    }
}
