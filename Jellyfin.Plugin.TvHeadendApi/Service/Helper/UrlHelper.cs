using System;
using Jellyfin.Plugin.TvHeadendApi.Configuration;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Helper;

/// <summary>
/// Builds normalized TVHeadend base and endpoint URLs.
/// </summary>
internal static class UrlHelper
{
    public static string GetWebRoot(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.Webroot))
        {
            return "/";
        }

        // Ensure the webroot always starts with / and ends with /
        // e.g. "tvh" -> "/tvh/", "/tvh" -> "/tvh/", "tvh/" -> "/tvh/", "/" -> "/"
        var trimmedWebroot = config.Webroot.Trim('/');
        return string.IsNullOrEmpty(trimmedWebroot) ? "/" : "/" + trimmedWebroot + "/";
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
