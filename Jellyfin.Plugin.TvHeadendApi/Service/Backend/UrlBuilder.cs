using System;
using Jellyfin.Plugin.TvHeadendApi.Configuration;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Backend;

/// <summary>
/// Default implementation of <see cref="IUrlBuilder"/>.
/// Centralizes all TVHeadend URL construction and sensitive-data masking.
/// </summary>
internal sealed class UrlBuilder : IUrlBuilder
{
    private const string MaskReplacement = "***";

    /// <inheritdoc />
    public string GetBaseUrl(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return $"{(config.UseSSL ? "https" : "http")}://{config.Host}:{config.Port}";
    }

    /// <inheritdoc />
    public string GetWebRoot(PluginConfiguration config)
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

    /// <inheritdoc />
    public string BuildApiUrl(PluginConfiguration config, string endpoint)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        return BuildEndpointUrl(config, endpoint);
    }

    /// <inheritdoc />
    public string BuildResourceUrl(PluginConfiguration config, string endpoint)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var baseUrl = BuildEndpointUrl(config, endpoint);
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

        // Mask both raw and URL-encoded forms of sensitive values.
        // URL-encoded credentials can appear in logged URLs when special characters are present.
        if (!string.IsNullOrWhiteSpace(config.AuthToken))
        {
            output = output.Replace(config.AuthToken, MaskReplacement, StringComparison.Ordinal);
            var encoded = Uri.EscapeDataString(config.AuthToken);
            if (!string.Equals(encoded, config.AuthToken, StringComparison.Ordinal))
            {
                output = output.Replace(encoded, MaskReplacement, StringComparison.Ordinal);
            }
        }

        if (!string.IsNullOrWhiteSpace(config.Password))
        {
            output = output.Replace(config.Password, MaskReplacement, StringComparison.Ordinal);
            var encoded = Uri.EscapeDataString(config.Password);
            if (!string.Equals(encoded, config.Password, StringComparison.Ordinal))
            {
                output = output.Replace(encoded, MaskReplacement, StringComparison.Ordinal);
            }
        }

        if (!string.IsNullOrWhiteSpace(config.Username))
        {
            output = output.Replace(config.Username, MaskReplacement, StringComparison.Ordinal);
            var encoded = Uri.EscapeDataString(config.Username);
            if (!string.Equals(encoded, config.Username, StringComparison.Ordinal))
            {
                output = output.Replace(encoded, MaskReplacement, StringComparison.Ordinal);
            }
        }

        return output;
    }

    /// <summary>
    /// Builds a full endpoint URL from base URL, web root, and relative endpoint.
    /// </summary>
    private string BuildEndpointUrl(PluginConfiguration config, string endpoint)
    {
        var webRoot = GetWebRoot(config);
        return $"{GetBaseUrl(config)}{webRoot}{endpoint.TrimStart('/')}";
    }
}
