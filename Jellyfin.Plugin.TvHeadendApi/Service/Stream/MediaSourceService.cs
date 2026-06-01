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
    private readonly IPlaybackContextAccessor _playbackContextAccessor;
    private readonly IApiClient _tvheadendApiClient;
    private readonly IUrlBuilder _tvheadendUrlBuilder;
    private readonly Relay.IRelayUrlBuilder _relayUrlBuilder;
    private readonly IMediaInfoCacheService _mediaInfoCacheService;

    public MediaSourceService(
        ILogger<MediaSourceService> logger,
        IProfileContainerResolver streamProfileContainerResolver,
        IStreamingProfileResolver streamingProfileResolver,
        IPlaybackContextAccessor playbackContextAccessor,
        IApiClient tvheadendApiClient,
        IUrlBuilder tvheadendUrlBuilder,
        Relay.IRelayUrlBuilder relayUrlBuilder,
        IMediaInfoCacheService mediaInfoCacheService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _streamProfileContainerResolver = streamProfileContainerResolver ?? throw new ArgumentNullException(nameof(streamProfileContainerResolver));
        _streamingProfileResolver = streamingProfileResolver ?? throw new ArgumentNullException(nameof(streamingProfileResolver));
        _playbackContextAccessor = playbackContextAccessor ?? throw new ArgumentNullException(nameof(playbackContextAccessor));
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
        // The context is enriched with the requesting client/device/user so that
        // client-, device- and user-scoped rules can actually match.
        var profileContext = await _playbackContextAccessor.CreateContextAsync(channelId, cancellationToken).ConfigureAwait(false);
        var resolution = _streamingProfileResolver.Resolve(profileContext);
        var effectiveProfile = resolution.EffectiveTvHeadendProfile;

        var streamUrl = await BuildStreamUrlAsync(channelId, effectiveProfile, profileContext, resolution, config, cancellationToken).ConfigureAwait(false);

        // Container and cache metadata MUST be resolved from the EFFECTIVE profile, not the
        // global one — otherwise an override that changes the container produces mismatched
        // metadata and Jellyfin pushes the client off Direct Play.
        var container = await _streamProfileContainerResolver.ResolveContainerAsync(config, effectiveProfile, cancellationToken).ConfigureAwait(false);
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

        var profileSnapshot = await _streamProfileContainerResolver.ResolveProfileSnapshotAsync(config, effectiveProfile, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Builds the stream URL handed to the client according to the configured delivery mode.
    /// <para>
    /// <see cref="StreamDeliveryMode.DirectToTvheadend"/> points the client straight at TVHeadend
    /// (auth token appended) — fastest and needs no reachable Jellyfin host — but requires a
    /// configured auth token (or anonymous access). When neither is available we transparently
    /// fall back to the secure relay so streams never break.
    /// </para>
    /// </summary>
    private async Task<string> BuildStreamUrlAsync(
        string channelId,
        string effectiveProfile,
        StreamingProfileContext profileContext,
        StreamingProfileResolutionResult resolution,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var canUseDirect = config.StreamDeliveryMode == StreamDeliveryMode.DirectToTvheadend
            && (config.AllowAnonymousAccess || !string.IsNullOrWhiteSpace(config.AuthToken));

        if (canUseDirect)
        {
            var endpoint = string.IsNullOrWhiteSpace(effectiveProfile)
                ? $"stream/channel/{Uri.EscapeDataString(channelId)}"
                : $"stream/channel/{Uri.EscapeDataString(channelId)}?profile={Uri.EscapeDataString(effectiveProfile)}";
            return _tvheadendUrlBuilder.BuildResourceUrl(config, endpoint);
        }

        if (config.StreamDeliveryMode == StreamDeliveryMode.DirectToTvheadend)
        {
            _logger.LogWarning(
                "Direct-to-TVHeadend delivery requested for channel {ChannelId} but no auth token is configured and anonymous access is off. Falling back to relay.",
                channelId);
        }

        // Route stream through the plugin's relay endpoint so TVHeadend credentials
        // stay server-side and internal URLs are never exposed to clients.
        // When relay token security is enabled, issue a scoped token embedded in the URL.
        return await _relayUrlBuilder.BuildTokenizedStreamRelayUrlAsync(
            channelId,
            effectiveProfile,
            profileContext.UserId,
            null,
            resolution.EffectivePlaybackMode.ToString(),
            cancellationToken).ConfigureAwait(false);
    }

    private PluginConfiguration GetConfig()
    {
        return _tvheadendApiClient.GetCurrentConfiguration()
            ?? throw new InvalidOperationException("Plugin configuration is not available.");
    }
}
