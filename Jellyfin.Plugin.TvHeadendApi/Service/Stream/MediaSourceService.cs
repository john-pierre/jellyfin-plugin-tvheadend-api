using System;
using System.Collections.Concurrent;
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
    /// <summary>
    /// How long a built stream URL is reused across Jellyfin's two-step playback start.
    /// Jellyfin core always calls <c>GetChannelStreamMediaSources</c> (enumerate for
    /// PlaybackInfo) followed by <c>GetChannelStream</c> (open) for every channel start;
    /// without sharing, each call would mint its own relay token (a serialized SQLite
    /// write) and redo the mediainfo cache reconciliation on the latency-sensitive
    /// channel-switch path — and the first token would never be consumed.
    /// The window only needs to cover the enumerate→open round trip and stays far below
    /// the relay token lifetime.
    /// </summary>
    private static readonly TimeSpan StreamBuildReuseWindow = TimeSpan.FromSeconds(10);

    private readonly ILogger<MediaSourceService> _logger;
    private readonly IProfileContainerResolver _streamProfileContainerResolver;
    private readonly IStreamingProfileResolver _streamingProfileResolver;
    private readonly IPlaybackContextAccessor _playbackContextAccessor;
    private readonly IApiClient _tvheadendApiClient;
    private readonly IUrlBuilder _tvheadendUrlBuilder;
    private readonly Relay.IRelayUrlBuilder _relayUrlBuilder;
    private readonly IMediaInfoCacheService _mediaInfoCacheService;
    private readonly Relay.IRelayTokenService? _relayTokenService;

    /// <summary>
    /// Recently built stream artifacts keyed by channel/user/profile, reused within
    /// <see cref="StreamBuildReuseWindow"/>. The service is registered as a singleton,
    /// so this cache spans the enumerate→open call pair of a playback start.
    /// </summary>
    private readonly ConcurrentDictionary<string, CachedStreamBuild> _recentStreamBuilds = new(StringComparer.Ordinal);

    public MediaSourceService(
        ILogger<MediaSourceService> logger,
        IProfileContainerResolver streamProfileContainerResolver,
        IStreamingProfileResolver streamingProfileResolver,
        IPlaybackContextAccessor playbackContextAccessor,
        IApiClient tvheadendApiClient,
        IUrlBuilder tvheadendUrlBuilder,
        Relay.IRelayUrlBuilder relayUrlBuilder,
        IMediaInfoCacheService mediaInfoCacheService,
        Relay.IRelayTokenService? relayTokenService = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _streamProfileContainerResolver = streamProfileContainerResolver ?? throw new ArgumentNullException(nameof(streamProfileContainerResolver));
        _streamingProfileResolver = streamingProfileResolver ?? throw new ArgumentNullException(nameof(streamingProfileResolver));
        _playbackContextAccessor = playbackContextAccessor ?? throw new ArgumentNullException(nameof(playbackContextAccessor));
        _tvheadendApiClient = tvheadendApiClient ?? throw new ArgumentNullException(nameof(tvheadendApiClient));
        _tvheadendUrlBuilder = tvheadendUrlBuilder ?? throw new ArgumentNullException(nameof(tvheadendUrlBuilder));
        _relayUrlBuilder = relayUrlBuilder ?? throw new ArgumentNullException(nameof(relayUrlBuilder));
        _mediaInfoCacheService = mediaInfoCacheService ?? throw new ArgumentNullException(nameof(mediaInfoCacheService));
        _relayTokenService = relayTokenService;
    }

    public async Task<MediaSourceInfo> GetChannelStreamAsync(string channelId, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        var built = await BuildMediaSourceInfoAsync(channelId, config, cancellationToken).ConfigureAwait(false);
        return built.MediaSource;
    }

    public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSourcesAsync(string channelId, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        var built = await BuildMediaSourceInfoAsync(channelId, config, cancellationToken).ConfigureAwait(false);
        var mediaSource = built.MediaSource;

        _logger.LogInformation(
            "Mediainfo cache key on media-source request: Provider={Provider}, ItemType={ItemType}, SourceIdFromMediaSource={SourceIdFromMediaSource}, CacheFile={CacheFile}",
            "Jellyfin.LiveTv.LiveTvMediaSourceProvider",
            "LiveTvChannel",
            mediaSource.Id ?? string.Empty,
            built.CacheFileName);

        return new List<MediaSourceInfo> { mediaSource };
    }

    private async Task<BuiltMediaSource> BuildMediaSourceInfoAsync(string channelId, PluginConfiguration config, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        // Self-measure the whole media source build — this IS the "stream setup" the
        // 1–2s zapping goal is about (profile resolution, token issue, cache reconciliation).
        var setupTimer = System.Diagnostics.Stopwatch.StartNew();

        // Resolve the effective streaming profile using the hierarchical resolver.
        // The context is enriched with the requesting client/device/user so that
        // client-, device- and user-scoped rules can actually match.
        var profileContext = await _playbackContextAccessor.CreateContextAsync(channelId, cancellationToken).ConfigureAwait(false);
        var resolution = _streamingProfileResolver.Resolve(profileContext);
        var effectiveProfile = resolution.EffectiveTvHeadendProfile;

        // Reuse the expensive artifacts (relay token/stream URL, container lookup, cache
        // reconciliation, cache file name) built moments ago for the same channel, user, and
        // effective profile — see StreamBuildReuseWindow for why. The MediaSourceInfo itself
        // is always constructed fresh because Jellyfin core mutates the returned instance.
        var buildKey = string.Join('|', channelId, profileContext.UserId ?? string.Empty, effectiveProfile);
        if (_recentStreamBuilds.TryGetValue(buildKey, out var recent) && !recent.IsExpired)
        {
            // The reconciled cache state is reused — the mediainfo cache was effectively
            // used, so this counts as a hit for the cache counters.
            _mediaInfoCacheService.RecordStreamBuildReuseHit();
            return new BuiltMediaSource(CreateMediaSourceInfo(channelId, config, recent.StreamUrl, recent.Container), recent.CacheFileName);
        }

        var (streamUrl, rawToken) = await BuildStreamUrlAsync(channelId, effectiveProfile, profileContext, resolution, config, cancellationToken).ConfigureAwait(false);

        // Container and cache metadata MUST be resolved from the EFFECTIVE profile, not the
        // global one — otherwise an override that changes the container produces mismatched
        // metadata and Jellyfin pushes the client off Direct Play.
        var container = await _streamProfileContainerResolver.ResolveContainerAsync(config, effectiveProfile, cancellationToken).ConfigureAwait(false);
        var profileSnapshot = await _streamProfileContainerResolver.ResolveProfileSnapshotAsync(config, effectiveProfile, cancellationToken).ConfigureAwait(false);

        // Cache is always active when probing is enabled — the old per-field toggles are deprecated.
        var cacheEnabled = config.SupportsProbing;
        var cacheStatus = await _mediaInfoCacheService.EnsureMediaInfoCacheStateAsync(
            channelId,
            streamUrl,
            profileSnapshot,
            cacheEnabled,
            cacheEnabled,
            cancellationToken).ConfigureAwait(false);

        // Compute the diagnostic cache file name once per build; the media source ID handed
        // to Jellyfin is always the channel ID, so the source ID equals the channel ID here.
        var cacheFileName = _mediaInfoCacheService.BuildChannelCacheFileName(channelId, channelId);

        PruneExpiredStreamBuilds();
        _recentStreamBuilds[buildKey] = new CachedStreamBuild(streamUrl, container, cacheFileName, DateTimeOffset.UtcNow + StreamBuildReuseWindow);

        setupTimer.Stop();
        var setupMs = setupTimer.Elapsed.TotalMilliseconds;
        Metrics.MetricService.StreamSetupCount.Add(1);
        Metrics.MetricService.StreamSetupDuration.Record(setupMs);

        // Attach the zapping telemetry (resolution source, cache outcome, setup duration) to
        // the issued relay token so the relay controller can copy it into the request metric
        // when the stream actually starts. Best-effort — never fail the playback path.
        if (rawToken != null && _relayTokenService != null)
        {
            try
            {
                await _relayTokenService.AttachStreamTelemetryAsync(
                    rawToken,
                    resolution.Source.ToString(),
                    cacheStatus.ToString().ToLowerInvariant(),
                    setupMs,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to attach stream telemetry to the relay token for channel {ChannelId}.", channelId);
            }
        }

        return new BuiltMediaSource(CreateMediaSourceInfo(channelId, config, streamUrl, container), cacheFileName);
    }

    /// <summary>
    /// Constructs the <see cref="MediaSourceInfo"/> handed to Jellyfin. Always returns a
    /// fresh instance — Jellyfin core mutates the object (IDs, live stream state), so a
    /// cached instance must never be returned twice.
    /// </summary>
    private static MediaSourceInfo CreateMediaSourceInfo(string channelId, PluginConfiguration config, string streamUrl, string container)
    {
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

        return mediaSource;
    }

    /// <summary>
    /// Drops expired reuse entries so the dictionary stays bounded by the set of
    /// channels/users active within the reuse window.
    /// </summary>
    private void PruneExpiredStreamBuilds()
    {
        foreach (var entry in _recentStreamBuilds)
        {
            if (entry.Value.IsExpired)
            {
                _recentStreamBuilds.TryRemove(entry.Key, out _);
            }
        }
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
    private async Task<(string StreamUrl, string? RawToken)> BuildStreamUrlAsync(
        string channelId,
        string effectiveProfile,
        StreamingProfileContext profileContext,
        StreamingProfileResolutionResult resolution,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        // RelayEnabled is a hard kill-switch: every relay endpoint answers 503 while it is off.
        // Handing out a relay URL in that state produced a stream URL that could only ever fail,
        // so a disabled relay forces the direct path exactly like DirectToTvheadend does.
        var relayUnavailable = !config.RelayEnabled;
        var directRequested = config.StreamDeliveryMode == StreamDeliveryMode.DirectToTvheadend || relayUnavailable;
        var haveTvheadendCredentials = config.AllowAnonymousAccess || !string.IsNullOrWhiteSpace(config.AuthToken);

        if (directRequested && haveTvheadendCredentials)
        {
            var endpoint = string.IsNullOrWhiteSpace(effectiveProfile)
                ? $"stream/channel/{Uri.EscapeDataString(channelId)}"
                : $"stream/channel/{Uri.EscapeDataString(channelId)}?profile={Uri.EscapeDataString(effectiveProfile)}";
            return (_tvheadendUrlBuilder.BuildResourceUrl(config, endpoint), null);
        }

        if (relayUnavailable)
        {
            _logger.LogError(
                "Relay is disabled and no TVHeadend auth token is configured (anonymous access is off), so channel {ChannelId} has no usable stream URL. Enable the relay service, or set an auth token / allow anonymous access to stream directly from TVHeadend.",
                channelId);
        }
        else if (config.StreamDeliveryMode == StreamDeliveryMode.DirectToTvheadend)
        {
            _logger.LogWarning(
                "Direct-to-TVHeadend delivery requested for channel {ChannelId} but no auth token is configured and anonymous access is off. Falling back to relay.",
                channelId);
        }

        // Route stream through the plugin's relay endpoint so TVHeadend credentials
        // stay server-side and internal URLs are never exposed to clients.
        // When relay token security is enabled, issue a scoped token embedded in the URL.
        var tokenized = await _relayUrlBuilder.BuildTokenizedStreamRelayUrlDetailedAsync(
            channelId,
            effectiveProfile,
            profileContext.UserId,
            null,
            resolution.EffectivePlaybackMode.ToString(),
            cancellationToken).ConfigureAwait(false);
        return (tokenized.Url, tokenized.RawToken);
    }

    private PluginConfiguration GetConfig()
    {
        return _tvheadendApiClient.GetCurrentConfiguration()
            ?? throw new InvalidOperationException("Plugin configuration is not available.");
    }

    /// <summary>
    /// Result of a media source build: the freshly constructed media source plus the
    /// mediainfo cache file name computed during the build (reused for diagnostics
    /// logging instead of recomputing the hash chain).
    /// </summary>
    private sealed record BuiltMediaSource(MediaSourceInfo MediaSource, string CacheFileName);

    /// <summary>
    /// Expensive per-build artifacts cached for <see cref="StreamBuildReuseWindow"/>.
    /// </summary>
    private sealed record CachedStreamBuild(string StreamUrl, string Container, string CacheFileName, DateTimeOffset ExpiresAt)
    {
        /// <summary>
        /// Gets a value indicating whether the entry has outlived the reuse window.
        /// </summary>
        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    }
}
