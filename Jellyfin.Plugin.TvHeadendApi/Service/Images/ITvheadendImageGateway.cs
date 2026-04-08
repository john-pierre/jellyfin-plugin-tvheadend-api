using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Images;

/// <summary>
/// Provides authenticated access to TVHeadend image endpoints.
/// </summary>
public interface ITvheadendImageGateway
{
    /// <summary>
    /// Downloads a TVHeadend image response for the given relative image path.
    /// </summary>
    /// <param name="imagePath">Relative TVH image path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Raw HTTP response from TVHeadend.</returns>
    Task<HttpResponseMessage> FetchImageAsync(string imagePath, CancellationToken cancellationToken);
}
