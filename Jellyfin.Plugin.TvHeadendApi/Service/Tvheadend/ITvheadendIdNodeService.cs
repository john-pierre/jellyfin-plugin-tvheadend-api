using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;

/// <summary>
/// Encapsulates TVHeadend idnode API access.
/// </summary>
internal interface ITvheadendIdNodeService
{
    Task<JsonDocument> LoadIdNodeByUuidAsync(
        HttpClient httpClient,
        string baseUrl,
        string webRoot,
        string uuid,
        CancellationToken cancellationToken);

    Task<JsonDocument> LoadDvrConfigsAsync(
        HttpClient httpClient,
        string baseUrl,
        string webRoot,
        CancellationToken cancellationToken);
}
