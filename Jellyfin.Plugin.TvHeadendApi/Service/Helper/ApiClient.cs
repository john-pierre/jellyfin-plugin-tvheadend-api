using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Helper;

/// <summary>
/// Provides centralized access to TVHeadend configuration, URL creation, and HTTP client setup.
/// </summary>
internal sealed class ApiClient : IApiClient
{
    /// <inheritdoc />
    public PluginConfiguration? GetCurrentConfiguration()
    {
        return Plugin.Instance?.Configuration;
    }

    /// <inheritdoc />
    public HttpClient BuildHttpClient(PluginConfiguration config)
    {
        return HttpClientFactory.Create(config);
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
