using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Helper;

/// <summary>
/// Provides HTTP client creation and request execution for TVHeadend API calls.
/// URL construction is handled by <see cref="IUrlBuilder"/>; this interface
/// is responsible only for authentication-aware HTTP client lifecycle and
/// raw HTTP operations (GET string, GET stream, POST form).
/// </summary>
public interface IApiClient
{
    /// <summary>
    /// Gets the current plugin configuration if available.
    /// </summary>
    /// <returns>The current plugin configuration, or <see langword="null"/> if the plugin instance is unavailable.</returns>
    PluginConfiguration? GetCurrentConfiguration();

    /// <summary>
    /// Creates an HTTP client configured for TVHeadend API requests.
    /// <para>
    /// When <see cref="PluginConfiguration.AllowAnonymousAccess"/> is <c>false</c> and
    /// credentials are configured, the returned client includes a proactive Basic
    /// Authorization header and a <see cref="System.Net.CredentialCache"/> fallback
    /// for Digest auth negotiation. The handler is created explicitly (not cast from
    /// factory internals) to remain safe under .NET 9.
    /// </para>
    /// <para>
    /// When anonymous access is enabled, the client is obtained from
    /// <see cref="IHttpClientFactory"/> with connection pooling and resilience.
    /// </para>
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>A configured HTTP client instance.</returns>
    HttpClient CreateApiHttpClient(PluginConfiguration config);

    /// <summary>
    /// Executes a GET request and returns the response body as a string.
    /// </summary>
    /// <param name="httpClient">Configured HTTP client.</param>
    /// <param name="url">Absolute request URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Response body as a string.</returns>
    Task<string> GetStringAsync(HttpClient httpClient, string url, CancellationToken cancellationToken);

    /// <summary>
    /// Executes a GET request and returns the response as an in-memory stream.
    /// </summary>
    /// <param name="httpClient">Configured HTTP client.</param>
    /// <param name="url">Absolute request URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Response stream (caller owns the stream).</returns>
    Task<global::System.IO.Stream> GetStreamAsync(HttpClient httpClient, string url, CancellationToken cancellationToken);

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
