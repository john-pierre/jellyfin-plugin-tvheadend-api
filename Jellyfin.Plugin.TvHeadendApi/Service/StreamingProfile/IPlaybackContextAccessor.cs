using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;

/// <summary>
/// Builds a <see cref="StreamingProfileContext"/> for the current playback request,
/// enriching it with the requesting client/device/user identity when an HTTP request
/// context is available (e.g. during a Jellyfin <c>PlaybackInfo</c> call).
/// </summary>
public interface IPlaybackContextAccessor
{
    /// <summary>
    /// Creates a profile-resolution context for the given channel, populated with the
    /// requesting client name, device name and user id when they can be resolved from the
    /// current HTTP request. Falls back to a channel-only context outside a request.
    /// </summary>
    /// <param name="channelId">The TVHeadend channel UUID being tuned.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The populated streaming-profile context.</returns>
    Task<StreamingProfileContext> CreateContextAsync(string? channelId, CancellationToken cancellationToken);
}
