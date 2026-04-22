// Relay service interface — defines high-performance proxy operations for TVHeadend resources.

using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// High-performance relay service that proxies requests to TVHeadend
/// and streams responses back without buffering.
/// </summary>
public interface IRelayService
{
    /// <summary>
    /// Relays an image (icon/logo/thumbnail) request to TVHeadend.
    /// Uses aggressive timeouts suitable for small resources.
    /// </summary>
    /// <param name="upstreamPath">Relative TVHeadend path (e.g. <c>imagecache/123</c>).</param>
    /// <param name="cancellationToken">Cancellation token — cancels upstream when client disconnects.</param>
    /// <returns>A <see cref="RelayResult"/> with the upstream response.</returns>
    Task<RelayResult> RelayImageAsync(string upstreamPath, CancellationToken cancellationToken);

    /// <summary>
    /// Relays a live stream request to TVHeadend.
    /// Uses long timeouts suitable for indefinite streaming.
    /// </summary>
    /// <param name="channelId">TVHeadend channel UUID.</param>
    /// <param name="profile">Optional streaming profile override.</param>
    /// <param name="cancellationToken">Cancellation token — cancels upstream when client disconnects.</param>
    /// <returns>A <see cref="RelayResult"/> with the upstream response.</returns>
    Task<RelayResult> RelayStreamAsync(string channelId, string? profile, CancellationToken cancellationToken);
}
