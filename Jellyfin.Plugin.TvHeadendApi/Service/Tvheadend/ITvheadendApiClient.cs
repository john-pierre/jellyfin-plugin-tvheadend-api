using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;

/// <summary>
/// Defines a minimal TVHeadend API client abstraction for configuration, URL building, and HTTP client creation.
/// </summary>
public interface ITvheadendApiClient
{
    /// <summary>
    /// Gets the current plugin configuration if available.
    /// </summary>
    /// <returns>The current plugin configuration, or <see langword="null"/> if the plugin instance is unavailable.</returns>
    PluginConfiguration? GetCurrentConfiguration();

    /// <summary>
    /// Creates a configured HTTP client for TVHeadend API calls.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>A configured HTTP client instance.</returns>
    HttpClient CreateHttpClient(PluginConfiguration config);

    /// <summary>
    /// Gets the normalized TVHeadend base URL.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>The normalized TVHeadend base URL.</returns>
    string GetBaseUrl(PluginConfiguration config);

    /// <summary>
    /// Gets the normalized TVHeadend web root.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>The normalized TVHeadend web root.</returns>
    string GetWebRoot(PluginConfiguration config);

    /// <summary>
    /// Builds a full TVHeadend endpoint URL.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="endpoint">Relative TVHeadend endpoint.</param>
    /// <returns>The full endpoint URL.</returns>
    string BuildUrl(PluginConfiguration config, string endpoint);

    /// <summary>
    /// Executes a GET request and returns the response body as a string.
    /// </summary>
    /// <param name="httpClient">Configured HTTP client.</param>
    /// <param name="url">Absolute request URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Response body as a string.</returns>
    Task<string> GetStringAsync(HttpClient httpClient, string url, CancellationToken cancellationToken);

    /// <summary>
    /// Executes a GET request and returns the response stream.
    /// </summary>
    /// <param name="httpClient">Configured HTTP client.</param>
    /// <param name="url">Absolute request URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Response stream.</returns>
    Task<Stream> GetStreamAsync(HttpClient httpClient, string url, CancellationToken cancellationToken);

    /// <summary>
    /// Executes a form POST request and returns the raw HTTP response.
    /// </summary>
    /// <param name="httpClient">Configured HTTP client.</param>
    /// <param name="url">Absolute request URL.</param>
    /// <param name="formValues">Form values to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The raw HTTP response message.</returns>
    Task<HttpResponseMessage> PostFormAsync(
        HttpClient httpClient,
        string url,
        IEnumerable<KeyValuePair<string, string>> formValues,
        CancellationToken cancellationToken);
}
