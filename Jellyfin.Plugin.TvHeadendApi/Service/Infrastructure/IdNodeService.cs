using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Infrastructure;

/// <summary>
/// Default idnode API implementation used by higher-level services.
/// </summary>
internal sealed class IdNodeService : IIdNodeService
{
    public Task<JsonDocument> LoadIdNodeByUuidAsync(
        HttpClient httpClient,
        string baseUrl,
        string webRoot,
        string uuid,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(webRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(uuid);

        var loadUrl = $"{baseUrl}{webRoot}api/idnode/load?uuid={Uri.EscapeDataString(uuid)}";
        return LoadJsonFromGetAsync(httpClient, loadUrl, cancellationToken);
    }

    public Task<JsonDocument> LoadDvrConfigsAsync(
        HttpClient httpClient,
        string baseUrl,
        string webRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(webRoot);

        var loadUrl = $"{baseUrl}{webRoot}api/idnode/load";
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("enum", "1"),
            new KeyValuePair<string, string>("class", "dvrconfig"),
        });

        return LoadJsonFromPostAsync(httpClient, loadUrl, content, cancellationToken);
    }

    private static async Task<JsonDocument> LoadJsonFromGetAsync(HttpClient httpClient, string url, CancellationToken cancellationToken)
    {
        var response = await httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(response);
    }

    private static async Task<JsonDocument> LoadJsonFromPostAsync(HttpClient httpClient, string url, HttpContent content, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(body);
    }
}
