using System;
using System.Text;
using Jellyfin.Plugin.TvHeadendApi.Configuration;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;

/// <summary>
/// Default implementation for TVHeadend URL/auth handling.
/// </summary>
internal sealed class TvheadendUrlBuilder : ITvheadendUrlBuilder
{
    /// <inheritdoc />
    public string BuildUrl(PluginConfiguration config, string endpoint, string authMethod = "header")
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var baseUrl = TvhUrlBuilder.BuildEndpointUrl(config, endpoint);
        if (config.AllowAnonymousAccess)
        {
            return baseUrl;
        }

        switch (authMethod.ToLowerInvariant())
        {
            case "url":
                var credentials = $"{Uri.EscapeDataString(config.Username)}:{Uri.EscapeDataString(config.Password)}";
                return baseUrl.Replace("http://", $"http://{credentials}@", StringComparison.Ordinal)
                    .Replace("https://", $"https://{credentials}@", StringComparison.Ordinal);
            case "parameter":
                if (string.IsNullOrWhiteSpace(config.AuthToken))
                {
                    return baseUrl;
                }

                var separator = baseUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
                return $"{baseUrl}{separator}auth={Uri.EscapeDataString(config.AuthToken)}";
            case "header":
            default:
                return baseUrl;
        }
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
