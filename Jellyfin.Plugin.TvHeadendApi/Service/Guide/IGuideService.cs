using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Guide;

/// <summary>
/// Encapsulates TVHeadend read-only guide and channel operations.
/// </summary>
public interface IGuideService
{
    /// <summary>
    /// Gets all enabled channels from TVHeadend.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Channel information for all enabled channels.</returns>
    Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets EPG programs for a channel within the given time range.
    /// </summary>
    /// <param name="channelId">Channel UUID.</param>
    /// <param name="startDateUtc">Start of the time range (UTC).</param>
    /// <param name="endDateUtc">End of the time range (UTC).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Program entries for the channel.</returns>
    Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Gets EPG content type mappings from TVHeadend.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Dictionary mapping content type IDs to their descriptions.</returns>
    Task<Dictionary<int, string>> GetContentTypesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets channel tag mappings from TVHeadend.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Dictionary mapping tag UUIDs to their display names.</returns>
    Task<Dictionary<string, string>> GetChannelTagsAsync(CancellationToken cancellationToken);
}
