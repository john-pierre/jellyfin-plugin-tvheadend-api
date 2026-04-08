using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Images;

/// <summary>
/// Proxies image requests from Jellyfin to TVHeadend without exposing backend credentials.
/// </summary>
public interface IImageProxyService
{
    /// <summary>
    /// Proxies a TVHeadend image path and returns an MVC action result for the API endpoint.
    /// </summary>
    /// <param name="imagePath">TVHeadend image path relative to backend root.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An action result containing either image data or an error response.</returns>
    Task<IActionResult> ProxyImageAsync(string? imagePath, CancellationToken cancellationToken);
}
