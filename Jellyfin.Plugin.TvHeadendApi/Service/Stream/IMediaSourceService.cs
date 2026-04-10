using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;

#pragma warning disable CS1591

namespace Jellyfin.Plugin.TvHeadendApi.Service.Stream;

/// <summary>
/// Builds media sources for live TV stream requests.
/// </summary>
public interface IMediaSourceService
{
    Task<MediaSourceInfo> GetChannelStreamAsync(string channelId, CancellationToken cancellationToken);

    Task<List<MediaSourceInfo>> GetChannelStreamMediaSourcesAsync(string channelId, CancellationToken cancellationToken);
}

#pragma warning restore CS1591
