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

    public Task<HttpResponseMessage> FetchImageAsync(string imagePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        var config = _tvheadendApiClient.GetCurrentConfiguration()
            ?? throw new InvalidOperationException("Plugin configuration is not available.");

        var cleanPath = imagePath.TrimStart('/');
        var imageUrl = _tvheadendUrlBuilder.BuildUrl(config, cleanPath, "url");
        var httpClient = _tvheadendApiClient.CreateHttpClient(config);
        return httpClient.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }
}
