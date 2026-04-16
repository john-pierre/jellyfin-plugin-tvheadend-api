using System;
using System.Text;
using Jellyfin.Plugin.TvHeadendApi.Configuration;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Helper;

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

        // Credentials are sent via HTTP header � no auth data embedded in the URL.
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
