using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Profile;

/// <summary>
/// Resolves TVHeadend stream profile metadata from profile list and idnode details.
/// </summary>
internal interface IProfileResolver
{
    Task<IReadOnlyList<ProfileReference>> GetProfilesAsync(
        HttpClient httpClient,
        string baseUrl,
        string webRoot,
        CancellationToken cancellationToken);

    Task<ProfileDetails?> GetProfileDetailsByUuidAsync(
        HttpClient httpClient,
        string baseUrl,
        string webRoot,
        string profileUuid,
        string profileName,
        CancellationToken cancellationToken);

    Task<ResolvedProfile?> ResolveProfileByNameAsync(
        HttpClient httpClient,
        string baseUrl,
        string webRoot,
        string profileName,
        CancellationToken cancellationToken);
}
