using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Profiles;

/// <summary>
/// Provides TVHeadend profile provisioning workflows for the Jellyfin plugin.
/// </summary>
public interface IProfileProvisioningService
{
    /// <summary>
    /// Creates or updates the recommended TVHeadend codec and streaming profiles used by the plugin.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The profile provisioning result.</returns>
    Task<ProfileDetectionResult> CreateProfileAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Generates a new authentication token from TVHeadend and stores it in the plugin configuration.
    /// Requires TVHeadend admin privileges.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The generation result with the new auth token.</returns>
    Task<AuthTokenGenerationResult> GenerateAuthTokenAsync(CancellationToken cancellationToken);
}
