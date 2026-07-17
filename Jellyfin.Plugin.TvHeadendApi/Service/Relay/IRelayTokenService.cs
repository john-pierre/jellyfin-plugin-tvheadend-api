// Service interface for relay token issuance, revocation, and cleanup.

using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// Issues, revokes, and manages relay tokens for secure public relay endpoints.
/// </summary>
public interface IRelayTokenService
{
    /// <summary>
    /// Issues a scoped stream relay token for the given channel.
    /// </summary>
    /// <param name="channelId">TVHeadend channel UUID.</param>
    /// <param name="userId">Optional Jellyfin user ID.</param>
    /// <param name="deviceId">Optional device/client identifier.</param>
    /// <param name="profile">Optional streaming profile name.</param>
    /// <param name="playbackMode">Optional playback mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The raw token string (never persisted — only the HMAC hash is stored).</returns>
    Task<string> IssueStreamTokenAsync(
        string channelId,
        string? userId,
        string? deviceId,
        string? profile,
        string? playbackMode,
        CancellationToken cancellationToken);

    /// <summary>
    /// Issues a scoped image relay token for the given image resource.
    /// </summary>
    /// <param name="imageId">Image resource identifier (e.g. imagecache path).</param>
    /// <param name="mediaKind">Optional media kind (logo, epg_image, thumbnail).</param>
    /// <param name="userId">Optional Jellyfin user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The raw token string (never persisted — only the HMAC hash is stored).</returns>
    Task<string> IssueImageTokenAsync(
        string imageId,
        MediaKind? mediaKind,
        string? userId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Attaches stream-setup telemetry to an already issued stream token. Called by the
    /// media source build after the mediainfo cache outcome and setup duration are known
    /// (both happen after token issuance because the cache embeds the tokenized URL).
    /// </summary>
    /// <param name="rawToken">The raw token returned by <see cref="IssueStreamTokenAsync"/>.</param>
    /// <param name="resolutionSource">Which level of the profile hierarchy resolved the profile.</param>
    /// <param name="mediaInfoCacheStatus">The mediainfo cache outcome (hit/miss/mismatch/restored/unknown).</param>
    /// <param name="streamSetupMs">The media source build duration in ms.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task AttachStreamTelemetryAsync(string rawToken, string? resolutionSource, string? mediaInfoCacheStatus, double? streamSetupMs, CancellationToken cancellationToken);

    /// <summary>
    /// Revokes an existing token by its raw value.
    /// </summary>
    /// <param name="rawToken">The raw token to revoke.</param>
    /// <param name="reason">Revocation reason.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task RevokeTokenAsync(string rawToken, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Removes expired and revoked tokens from the database.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of tokens cleaned up.</returns>
    Task<int> CleanupExpiredTokensAsync(CancellationToken cancellationToken);
}
