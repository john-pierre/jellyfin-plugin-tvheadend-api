using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;

/// <summary>
/// Resolves TVHeadend stream profile metadata from profile list and idnode details.
/// </summary>
internal interface ITvheadendStreamProfileResolver
{
    Task<IReadOnlyList<TvheadendStreamProfileReference>> GetProfilesAsync(
        HttpClient httpClient,
        string baseUrl,
        string webRoot,
        CancellationToken cancellationToken);

    Task<TvheadendStreamProfileDetails?> GetProfileDetailsByUuidAsync(
        HttpClient httpClient,
        string baseUrl,
        string webRoot,
        string profileUuid,
        string profileName,
        CancellationToken cancellationToken);
}
