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

        // IPv6 literals must be bracketed to form a valid URI authority.
        var host = config.Host;
        if (!string.IsNullOrEmpty(host) && host.Contains(':', StringComparison.Ordinal) && host[0] != '[')
        {
            host = $"[{host}]";
        }

        return $"{(config.UseSSL ? "https" : "http")}://{host}:{config.Port}";
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
        output = MaskValue(output, config.AuthToken);
        output = MaskValue(output, config.Password);
        output = MaskValue(output, config.Username);
        return output;
    }

    /// <summary>
    /// Masks both the raw and URL-encoded forms of a sensitive value.
    /// URL-encoded credentials can appear in logged URLs when special characters are present.
    /// </summary>
    private static string MaskValue(string output, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return output;
        }

        output = output.Replace(value, MaskReplacement, StringComparison.Ordinal);
        var encoded = Uri.EscapeDataString(value);
        if (!string.Equals(encoded, value, StringComparison.Ordinal))
        {
            output = output.Replace(encoded, MaskReplacement, StringComparison.Ordinal);
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
