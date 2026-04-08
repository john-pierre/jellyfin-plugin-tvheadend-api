using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Images;

/// <summary>
/// Fetches TVHeadend image payloads using plugin configuration.
/// </summary>
internal sealed class TvheadendImageGateway : ITvheadendImageGateway
{
    private readonly ITvheadendApiClient _tvheadendApiClient;
    private readonly ITvheadendUrlBuilder _tvheadendUrlBuilder;

    public TvheadendImageGateway(ITvheadendApiClient tvheadendApiClient, ITvheadendUrlBuilder tvheadendUrlBuilder)
    {
        _tvheadendApiClient = tvheadendApiClient ?? throw new ArgumentNullException(nameof(tvheadendApiClient));
        _tvheadendUrlBuilder = tvheadendUrlBuilder ?? throw new ArgumentNullException(nameof(tvheadendUrlBuilder));
    }

    public async Task<HttpResponseMessage> FetchImageAsync(string imagePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        var config = _tvheadendApiClient.GetCurrentConfiguration()
            ?? throw new InvalidOperationException("Plugin configuration is not available.");

        var cleanPath = imagePath.TrimStart('/');
        var imageUrl = _tvheadendUrlBuilder.BuildUrl(config, cleanPath, "parameter");
        using var httpClient = _tvheadendApiClient.CreateHttpClient(config);
        using var upstream = await httpClient.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        var detached = new HttpResponseMessage(upstream.StatusCode)
        {
            ReasonPhrase = upstream.ReasonPhrase,
            Version = upstream.Version,
        };

        foreach (var header in upstream.Headers)
        {
            detached.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (upstream.Content != null)
        {
            var body = await upstream.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            detached.Content = new ByteArrayContent(body);
            foreach (var header in upstream.Content.Headers)
            {
                detached.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return detached;
    }
}
