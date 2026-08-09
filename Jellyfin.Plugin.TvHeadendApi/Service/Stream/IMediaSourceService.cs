using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Stream;

/// <summary>
/// Builds media sources for live TV stream requests.
/// </summary>
public interface IMediaSourceService
{
    /// <summary>
    /// Builds a <see cref="MediaSourceInfo"/> for the given channel.
    /// </summary>
    /// <param name="channelId">TVHeadend channel UUID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The media source info for the channel stream.</returns>
    Task<MediaSourceInfo> GetChannelStreamAsync(string channelId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the available media sources for the given channel.
    /// </summary>
    /// <param name="channelId">TVHeadend channel UUID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list containing the media source info for the channel.</returns>
    Task<List<MediaSourceInfo>> GetChannelStreamMediaSourcesAsync(string channelId, CancellationToken cancellationToken);
}
