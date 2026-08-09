using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Profile;

/// <summary>
/// Provides TVHeadend default profile provisioning workflows for the Jellyfin plugin.
/// </summary>
public interface IDefaultProfileService
{
    /// <summary>
    /// Creates or updates the recommended TVHeadend codec and streaming profiles used by the plugin.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The profile provisioning result.</returns>
    Task<ProfileDetectionResult> CreateProfileAsync(CancellationToken cancellationToken);
}
