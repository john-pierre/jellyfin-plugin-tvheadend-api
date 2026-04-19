using Jellyfin.Plugin.TvHeadendApi.Configuration;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Helper;

/// <summary>
/// Builds TVHeadend URLs and masks sensitive values for logging.
/// Centralizes all URL construction: base URLs, API endpoint URLs, and
/// token-authenticated resource URLs (streams, icons).
/// </summary>
internal interface IUrlBuilder
{
    /// <summary>
    /// Gets the normalized TVHeadend base URL (scheme + host + port).
    /// Example: <c>https://tvh.local:9981</c>.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>The base URL without trailing slash.</returns>
    string GetBaseUrl(PluginConfiguration config);

    /// <summary>
    /// Gets the normalized TVHeadend web root path.
    /// Always starts and ends with <c>/</c>.
    /// Example: <c>/tvh/</c> or <c>/</c>.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>The normalized web root.</returns>
    string GetWebRoot(PluginConfiguration config);

    /// <summary>
    /// Builds a plain API endpoint URL without any authentication data.
    /// Used for standard TVHeadend API requests where authentication is
    /// handled via HTTP headers on the <see cref="System.Net.Http.HttpClient"/>.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="endpoint">Relative endpoint (e.g. <c>api/channel/grid</c>).</param>
    /// <returns>Absolute URL without credentials.</returns>
    string BuildApiUrl(PluginConfiguration config, string endpoint);

    /// <summary>
    /// Builds a resource URL with the auth token appended as <c>?auth=...</c> query parameter.
    /// Used for streaming endpoints, channel icons, and image URLs that are consumed
    /// by external clients (players, browsers) which cannot attach HTTP auth headers.
    /// <para>
    /// When anonymous access is enabled or no auth token is configured, the URL is
    /// returned without the <c>auth</c> parameter.
    /// </para>
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="endpoint">Relative endpoint (e.g. <c>stream/channel/123</c>).</param>
    /// <returns>Absolute URL with auth token as query parameter when applicable.</returns>
    string BuildResourceUrl(PluginConfiguration config, string endpoint);

    /// <summary>
    /// Masks auth token, username, and password in a string before logging.
    /// All sensitive values present in <paramref name="config"/> are replaced with <c>***</c>.
    /// </summary>
    /// <param name="input">Input text (typically a URL or response excerpt).</param>
    /// <param name="config">Plugin configuration providing the sensitive values.</param>
    /// <returns>Masked text safe for logging.</returns>
    string MaskSensitiveData(string input, PluginConfiguration config);
}
