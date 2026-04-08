using Jellyfin.Plugin.TvHeadendApi.Configuration;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;

/// <summary>
/// Builds TVHeadend URLs and masks sensitive values for logging.
/// </summary>
internal interface ITvheadendUrlBuilder
{
    /// <summary>
    /// Builds a fully qualified URL for a TVHeadend endpoint.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="endpoint">Relative endpoint.</param>
    /// <param name="authMethod">Auth strategy: header, url, or parameter.</param>
    /// <returns>Absolute URL.</returns>
    string BuildUrl(PluginConfiguration config, string endpoint, string authMethod = "header");

    /// <summary>
    /// Masks auth token and credentials in a string before logging.
    /// </summary>
    /// <param name="input">Input text.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>Masked text.</returns>
    string MaskSensitiveData(string input, PluginConfiguration config);
}
