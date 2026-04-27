using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Stream;

/// <summary>
/// Handles stream URL and media source construction for live channels.
/// </summary>
internal sealed class MediaSourceService : IMediaSourceService
{
    private readonly ILogger<MediaSourceService> _logger;
    private readonly IProfileContainerResolver _streamProfileContainerResolver;
    private readonly IStreamingProfileResolver _streamingProfileResolver;
    private readonly IApiClient _tvheadendApiClient;
    private readonly IUrlBuilder _tvheadendUrlBuilder;
    private readonly Relay.IRelayUrlBuilder _relayUrlBuilder;
    private readonly IMediaInfoCacheService _mediaInfoCacheService;

    public MediaSourceService(
        ILogger<MediaSourceService> logger,
        IProfileContainerResolver streamProfileContainerResolver,
        IStreamingProfileResolver streamingProfileResolver,
        IApiClient tvheadendApiClient,
        IUrlBuilder tvheadendUrlBuilder,
        Relay.IRelayUrlBuilder relayUrlBuilder,
        IMediaInfoCacheService mediaInfoCacheService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _streamProfileContainerResolver = streamProfileContainerResolver ?? throw new ArgumentNullException(nameof(streamProfileContainerResolver));
        _streamingProfileResolver = streamingProfileResolver ?? throw new ArgumentNullException(nameof(streamingProfileResolver));
        _tvheadendApiClient = tvheadendApiClient ?? throw new ArgumentNullException(nameof(tvheadendApiClient));
        _tvheadendUrlBuilder = tvheadendUrlBuilder ?? throw new ArgumentNullException(nameof(tvheadendUrlBuilder));
        _relayUrlBuilder = relayUrlBuilder ?? throw new ArgumentNullException(nameof(relayUrlBuilder));
        _mediaInfoCacheService = mediaInfoCacheService ?? throw new ArgumentNullException(nameof(mediaInfoCacheService));
    }

    public Task<MediaSourceInfo> GetChannelStreamAsync(string channelId, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        return BuildMediaSourceInfoAsync(channelId, config, cancellationToken);
    }

    public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSourcesAsync(string channelId, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        var mediaSource = await BuildMediaSourceInfoAsync(channelId, config, cancellationToken).ConfigureAwait(false);

        var providerTypeFullName = "Jellyfin.LiveTv.LiveTvMediaSourceProvider";
        var itemTypeName = "LiveTvChannel";
        var sourceIdFromMediaSource = mediaSource.Id ?? string.Empty;
        var cacheFile = _mediaInfoCacheService.BuildChannelCacheFileName(channelId, sourceIdFromMediaSource);

        _logger.LogInformation(
            "Mediainfo cache key on media-source request: Provider={Provider}, ItemType={ItemType}, SourceIdFromMediaSource={SourceIdFromMediaSource}, CacheFile={CacheFile}",
            providerTypeFullName,
            itemTypeName,
            sourceIdFromMediaSource,
            cacheFile);

        return new List<MediaSourceInfo> { mediaSource };
    }

    private async Task<MediaSourceInfo> BuildMediaSourceInfoAsync(string channelId, PluginConfiguration config, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        // Resolve the effective streaming profile using the hierarchical resolver.
        var profileContext = new StreamingProfileContext { ChannelId = channelId };
        var resolution = _streamingProfileResolver.Resolve(profileContext);
        var effectiveProfile = resolution.EffectiveTvHeadendProfile;

        // Route stream through the plugin's relay endpoint so TVHeadend credentials
        // stay server-side and internal URLs are never exposed to clients.
        // When relay token security is enabled, issue a scoped token embedded in the URL.
        var streamUrl = await _relayUrlBuilder.BuildTokenizedStreamRelayUrlAsync(
            channelId, effectiveProfile, null, null, null, cancellationToken).ConfigureAwait(false);
        var container = await _streamProfileContainerResolver.ResolveContainerAsync(config, cancellationToken).ConfigureAwait(false);
        var mediaSource = new MediaSourceInfo
        {
            Id = channelId,
            Path = streamUrl,
            Name = $"LiveTV {channelId}",
            Protocol = MediaProtocol.Http,
            Container = container,
            IsRemote = true,
            SupportsDirectPlay = config.SupportsDirectPlay,
            SupportsDirectStream = config.SupportsDirectStream,
            SupportsTranscoding = config.SupportsTranscoding,
            IsInfiniteStream = config.IsInfiniteStream,
            IgnoreDts = config.IgnoreDts,
            FallbackMaxStreamingBitrate = config.FallbackMaxStreamingBitrate,
            UseMostCompatibleTranscodingProfile = config.SupportsTranscoding,
            RequiresOpening = true,
            RequiresClosing = true,
            ReadAtNativeFramerate = false,
            SupportsProbing = config.SupportsProbing,
            AnalyzeDurationMs = config.AnalyzeDurationMs,
        };

        if (config.BufferMs > 0)
        {
            mediaSource.BufferMs = config.BufferMs;
        }

        var profileSnapshot = await _streamProfileContainerResolver.ResolveProfileSnapshotAsync(config, cancellationToken).ConfigureAwait(false);

        // Cache is always active when probing is enabled — the old per-field toggles are deprecated.
        var cacheEnabled = config.SupportsProbing;
        await _mediaInfoCacheService.EnsureMediaInfoCacheStateAsync(
            channelId,
            streamUrl,
            profileSnapshot,
            cacheEnabled,
            cacheEnabled,
            cancellationToken).ConfigureAwait(false);

        return mediaSource;
    }

    private PluginConfiguration GetConfig()
    {
        return _tvheadendApiClient.GetCurrentConfiguration()
            ?? throw new InvalidOperationException("Plugin configuration is not available.");
    }
}
