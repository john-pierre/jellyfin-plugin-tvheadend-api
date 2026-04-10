using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;

#pragma warning disable CS1591

namespace Jellyfin.Plugin.TvHeadendApi.Service.Guide;

/// <summary>
/// Encapsulates TVH read-only guide and channel operations.
/// </summary>
public interface IGuideService
{
    Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken);

    Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken);

    Task<Dictionary<int, string>> GetContentTypesAsync(CancellationToken cancellationToken);

    Task<Dictionary<string, string>> GetChannelTagsAsync(CancellationToken cancellationToken);
}

#pragma warning restore CS1591
