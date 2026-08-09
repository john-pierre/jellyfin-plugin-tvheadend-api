using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// Builds Jellyfin-local relay URLs for images and streams.
/// Clients receive these URLs instead of direct TVHeadend URLs,
/// ensuring credentials stay server-side and internal URLs are hidden.
/// </summary>
public interface IRelayUrlBuilder
{
    /// <summary>
    /// Builds a relay URL for an image (channel logo, EPG image, thumbnail).
    /// </summary>
    /// <param name="tvhImagePath">Relative TVHeadend image path (e.g. <c>imagecache/123</c>).</param>
    /// <returns>Jellyfin relay URL like <c>http://jellyfin:8096/api/tvheadend/images/imagecache/123</c>.</returns>
    string BuildImageRelayUrl(string tvhImagePath);

    /// <summary>
    /// Builds a relay URL for a live TV stream.
    /// </summary>
    /// <param name="channelId">TVHeadend channel UUID.</param>
    /// <param name="profile">Optional streaming profile name.</param>
    /// <returns>Jellyfin relay URL like <c>http://jellyfin:8096/api/tvheadend/stream/{channelId}?profile=pass</c>.</returns>
    string BuildStreamRelayUrl(string channelId, string? profile);

    /// <summary>
    /// Returns the Jellyfin base URL currently used for relay (auto-detected or override).
    /// </summary>
    /// <returns>Base URL string.</returns>
    string GetEffectiveBaseUrl();

    /// <summary>
    /// Builds a tokenized relay URL for a live TV stream.
    /// Issues a scoped short-lived token and appends it to the URL.
    /// </summary>
    /// <param name="channelId">TVHeadend channel UUID.</param>
    /// <param name="profile">Optional streaming profile name.</param>
    /// <param name="userId">Optional Jellyfin user ID.</param>
    /// <param name="deviceId">Optional device/client identifier.</param>
    /// <param name="playbackMode">Optional playback mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Relay URL with embedded token.</returns>
    Task<string> BuildTokenizedStreamRelayUrlAsync(
        string channelId,
        string? profile,
        string? userId,
        string? deviceId,
        string? playbackMode,
        CancellationToken cancellationToken);

    /// <summary>
    /// Builds a tokenized relay URL for a live TV stream and also returns the raw token,
    /// so the caller can attach stream-setup telemetry to it after the media source build
    /// completes. Returns a <c>null</c> token when token security is disabled.
    /// </summary>
    /// <param name="channelId">TVHeadend channel UUID.</param>
    /// <param name="profile">Optional streaming profile name.</param>
    /// <param name="userId">Optional Jellyfin user ID.</param>
    /// <param name="deviceId">Optional device/client identifier.</param>
    /// <param name="playbackMode">Optional playback mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The relay URL together with the raw token (if any).</returns>
    Task<TokenizedStreamUrl> BuildTokenizedStreamRelayUrlDetailedAsync(
        string channelId,
        string? profile,
        string? userId,
        string? deviceId,
        string? playbackMode,
        CancellationToken cancellationToken);

    /// <summary>
    /// Builds a tokenized relay URL for an image resource.
    /// Issues a scoped token and appends it to the URL.
    /// </summary>
    /// <param name="imageId">Image resource identifier.</param>
    /// <param name="mediaKind">Optional media kind.</param>
    /// <param name="userId">Optional Jellyfin user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Relay URL with embedded token.</returns>
    Task<string> BuildTokenizedImageRelayUrlAsync(
        string imageId,
        MediaKind? mediaKind,
        string? userId,
        CancellationToken cancellationToken);
}
