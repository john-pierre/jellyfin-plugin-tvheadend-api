using Jellyfin.Plugin.TvHeadendApi.Configuration;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Infrastructure;

/// <summary>
/// Builds TVHeadend URLs and masks sensitive values for logging.
/// </summary>
internal interface IUrlBuilder
{
    /// <summary>
    /// Builds a URL using HTTP header authentication (credentials sent via header, not in URL).
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="endpoint">Relative endpoint.</param>
    /// <returns>Absolute URL without embedded credentials.</returns>
    string BuildUrlWithHeaderAuth(PluginConfiguration config, string endpoint);

    /// <summary>
    /// Builds a URL with credentials embedded in the URL (user:pass@ syntax).
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="endpoint">Relative endpoint.</param>
    /// <returns>Absolute URL with credentials embedded.</returns>
    string BuildUrlWithUrlAuth(PluginConfiguration config, string endpoint);

    /// <summary>
    /// Builds a URL with the auth token appended as a query parameter.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="endpoint">Relative endpoint.</param>
    /// <returns>Absolute URL with auth token as query parameter.</returns>
    string BuildUrlWithParameterAuth(PluginConfiguration config, string endpoint);

    /// <summary>
    /// Masks auth token and credentials in a string before logging.
    /// </summary>
    /// <param name="input">Input text.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>Masked text.</returns>
    string MaskSensitiveData(string input, PluginConfiguration config);
}
