using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Auth;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Auth;

/// <summary>
/// Provides TVHeadend auth token workflows for create/refresh and format validation.
/// </summary>
public interface ITokenService
{
    /// <summary>
    /// Generates a token for the configured TVHeadend user and retries with refresh until an FFmpeg-safe token is found.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Generation result including the accepted token when successful.</returns>
    Task<AuthTokenGenerationResult> GenerateValidTokenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Generates a valid TVHeadend token and persists it to the plugin configuration.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Generation result including the stored token when successful.</returns>
    Task<AuthTokenGenerationResult> GenerateAndStoreTokenAsync(CancellationToken cancellationToken);
}
