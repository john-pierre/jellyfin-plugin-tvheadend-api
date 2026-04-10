using System;
using System.Text;
using Jellyfin.Plugin.TvHeadendApi.Configuration;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Infrastructure;

/// <summary>
/// Builds normalized TVHeadend base and endpoint URLs.
/// Merged from UrlHelper – kept internal static for use by ApiClient.
/// </summary>
internal static class UrlHelper
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

/// <summary>
/// Default implementation for TVHeadend URL/auth handling.
/// </summary>
internal sealed class UrlBuilder : IUrlBuilder
{
    /// <inheritdoc />
    public string BuildUrlWithHeaderAuth(PluginConfiguration config, string endpoint)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        // Credentials are sent via HTTP header – no auth data embedded in the URL.
        return UrlHelper.BuildEndpointUrl(config, endpoint);
    }

    /// <inheritdoc />
    public string BuildUrlWithUrlAuth(PluginConfiguration config, string endpoint)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var baseUrl = UrlHelper.BuildEndpointUrl(config, endpoint);
        if (config.AllowAnonymousAccess)
        {
            return baseUrl;
        }

        var credentials = $"{Uri.EscapeDataString(config.Username)}:{Uri.EscapeDataString(config.Password)}";
        return baseUrl
            .Replace("http://", $"http://{credentials}@", StringComparison.Ordinal)
            .Replace("https://", $"https://{credentials}@", StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public string BuildUrlWithParameterAuth(PluginConfiguration config, string endpoint)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var baseUrl = UrlHelper.BuildEndpointUrl(config, endpoint);
        if (config.AllowAnonymousAccess || string.IsNullOrWhiteSpace(config.AuthToken))
        {
            return baseUrl;
        }

        var separator = baseUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{baseUrl}{separator}auth={Uri.EscapeDataString(config.AuthToken)}";
    }

    /// <inheritdoc />
    public string MaskSensitiveData(string input, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var output = input;
        if (!string.IsNullOrWhiteSpace(config.AuthToken))
        {
            output = output.Replace(config.AuthToken, "***", StringComparison.Ordinal);
        }

        if (!string.IsNullOrWhiteSpace(config.Username) && !string.IsNullOrWhiteSpace(config.Password))
        {
            var credentials = $"{config.Username}:{config.Password}";
            var encodedCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
            output = output.Replace(credentials, "***", StringComparison.Ordinal);
            output = output.Replace(encodedCredentials, "***", StringComparison.Ordinal);
        }

        return output;
    }
}
