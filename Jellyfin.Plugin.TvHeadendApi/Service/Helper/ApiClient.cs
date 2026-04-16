using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Helper;

/// <summary>
/// Provides centralized access to TVHeadend configuration, URL creation, and HTTP client setup.
/// Uses <see cref="IHttpClientFactory"/> to manage HTTP client lifetimes and avoid socket exhaustion.
/// </summary>
internal sealed class ApiClient : IApiClient
{
    /// <summary>
    /// Named HTTP client for standard TVHeadend connections with certificate revocation checks.
    /// </summary>
    internal const string HttpClientName = "TvHeadend";

    /// <summary>
    /// Named HTTP client for TVHeadend connections that skip certificate validation (self-signed certs).
    /// </summary>
    internal const string HttpClientUnsafeName = "TvHeadendUnsafe";

    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory for creating managed client instances.</param>
    public ApiClient(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public PluginConfiguration? GetCurrentConfiguration()
    {
        return Plugin.Instance?.Configuration;
    }

    /// <inheritdoc />
    public HttpClient BuildHttpClient(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var clientName = config.UseSSL && config.IgnoreCertificateErrors
            ? HttpClientUnsafeName
            : HttpClientName;

        var client = _httpClientFactory.CreateClient(clientName);

        if (!config.AllowAnonymousAccess && !string.IsNullOrWhiteSpace(config.Username))
        {
            var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.Username}:{config.Password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", creds);
        }

        return client;
    }

    /// <inheritdoc />
    public string GetBaseUrl(PluginConfiguration config)
    {
        return UrlHelper.GetBaseUrl(config);
    }

    /// <inheritdoc />
    public string GetWebRoot(PluginConfiguration config)
    {
        return UrlHelper.GetWebRoot(config);
    }

    /// <inheritdoc />
    public string BuildUrl(PluginConfiguration config, string endpoint)
    {
        return UrlHelper.BuildEndpointUrl(config, endpoint);
    }

    /// <inheritdoc />
    public Task<string> GetStringAsync(HttpClient httpClient, string url, CancellationToken cancellationToken)
    {
        return httpClient.GetStringAsync(url, cancellationToken);
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> PostFormAsync(
        HttpClient httpClient,
        string url,
        IEnumerable<KeyValuePair<string, string>> formValues,
        CancellationToken cancellationToken)
    {
        var content = new FormUrlEncodedContent(formValues);
        return httpClient.PostAsync(url, content, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<global::System.IO.Stream> GetStreamAsync(HttpClient httpClient, string url, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var target = new MemoryStream();
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        target.Position = 0;
        return target;
    }
}
