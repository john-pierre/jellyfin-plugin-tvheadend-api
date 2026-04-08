using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Streaming;

/// <summary>
/// Handles stream URL and media source construction for live channels.
/// </summary>
internal sealed class LiveStreamSourceService : ILiveStreamSourceService
{
    private const char StreamIdDelimiter = '_';

    private readonly ILogger<LiveStreamSourceService> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IStreamProfileContainerResolver _streamProfileContainerResolver;
    private readonly ITvheadendApiClient _tvheadendApiClient;
    private readonly ITvheadendUrlBuilder _tvheadendUrlBuilder;

    public LiveStreamSourceService(
        ILogger<LiveStreamSourceService> logger,
        ILibraryManager libraryManager,
        IStreamProfileContainerResolver streamProfileContainerResolver,
        ITvheadendApiClient tvheadendApiClient,
        ITvheadendUrlBuilder tvheadendUrlBuilder)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _streamProfileContainerResolver = streamProfileContainerResolver ?? throw new ArgumentNullException(nameof(streamProfileContainerResolver));
        _tvheadendApiClient = tvheadendApiClient ?? throw new ArgumentNullException(nameof(tvheadendApiClient));
        _tvheadendUrlBuilder = tvheadendUrlBuilder ?? throw new ArgumentNullException(nameof(tvheadendUrlBuilder));
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
        var internalChannelId = GetInternalChannelId("TvHeadendApi", channelId);
        var itemIdN = internalChannelId.ToString("N", CultureInfo.InvariantCulture);
        var sourceIdFromMediaSource = mediaSource.Id ?? string.Empty;
        var cacheFile = BuildMediainfoCacheFileName(providerTypeFullName, itemTypeName, itemIdN, sourceIdFromMediaSource);

        _logger.LogInformation(
            "Mediainfo cache key on media-source request: Provider={Provider}, ItemType={ItemType}, ItemId={ItemId}, SourceIdFromMediaSource={SourceIdFromMediaSource}, CacheFile={CacheFile}",
            providerTypeFullName,
            itemTypeName,
            itemIdN,
            sourceIdFromMediaSource,
            cacheFile);

        return new List<MediaSourceInfo> { mediaSource };
    }

    private async Task<MediaSourceInfo> BuildMediaSourceInfoAsync(string channelId, PluginConfiguration config, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        var streamUrl = _tvheadendUrlBuilder.BuildUrl(config, $"stream/channel/{channelId}?profile={config.StreamingProfile}", "url");
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
            AnalyzeDurationMs = config.AnalyzeDurationMs > 0 ? config.AnalyzeDurationMs : 200,
        };

        if (config.BufferMs > 0)
        {
            mediaSource.BufferMs = config.BufferMs;
        }

        if (config.EnableMediaInfoCacheWrite)
        {
            await TryWriteMediaInfoCacheAsync(channelId, streamUrl, container).ConfigureAwait(false);
        }

        return mediaSource;
    }

    private async Task TryWriteMediaInfoCacheAsync(string channelId, string streamUrl, string container)
    {
        try
        {
            if (!string.Equals(container, "mp4", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var cachePath = Plugin.Instance?.CachePath;
            if (string.IsNullOrWhiteSpace(cachePath))
            {
                return;
            }

            var providerTypeFullName = "Jellyfin.LiveTv.LiveTvMediaSourceProvider";
            var itemTypeName = "LiveTvChannel";
            var internalChannelId = GetInternalChannelId("TvHeadendApi", channelId);
            var itemIdN = internalChannelId.ToString("N", CultureInfo.InvariantCulture);
            var cacheFileName = BuildMediainfoCacheFileName(providerTypeFullName, itemTypeName, itemIdN, channelId);
            var mediaInfoDir = Path.Combine(cachePath, "mediainfo");
            var cacheFilePath = Path.Combine(mediaInfoDir, cacheFileName);
            if (File.Exists(cacheFilePath))
            {
                return;
            }

            if (!Directory.Exists(mediaInfoDir))
            {
                Directory.CreateDirectory(mediaInfoDir);
            }

            var cacheContent = new Dictionary<string, object?>
            {
                ["Protocol"] = "Http",
                ["Path"] = streamUrl,
                ["Type"] = "Default",
                ["Container"] = "mov,mp4,m4a,3gp,3g2,mj2",
                ["IsRemote"] = true,
                ["ReadAtNativeFramerate"] = false,
                ["IgnoreDts"] = false,
                ["SupportsTranscoding"] = true,
                ["SupportsDirectStream"] = true,
                ["SupportsDirectPlay"] = true,
                ["IsInfiniteStream"] = false,
                ["SupportsProbing"] = true,
                ["MediaStreams"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["Codec"] = "h264",
                        ["CodecTag"] = "avc1",
                        ["DisplayTitle"] = "720p H264 SDR",
                        ["IsDefault"] = true,
                        ["Height"] = 720,
                        ["Width"] = 1280,
                        ["AverageFrameRate"] = 25.0,
                        ["Type"] = "Video",
                        ["Index"] = 0,
                    },
                    new Dictionary<string, object?>
                    {
                        ["Codec"] = "aac",
                        ["CodecTag"] = "mp4a",
                        ["DisplayTitle"] = "AAC - Stereo",
                        ["BitRate"] = 128000,
                        ["Channels"] = 2,
                        ["SampleRate"] = 48000,
                        ["IsDefault"] = true,
                        ["Type"] = "Audio",
                        ["Index"] = 1,
                    },
                },
            };

            var json = JsonSerializer.Serialize(cacheContent);
            await File.WriteAllTextAsync(cacheFilePath, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write mediainfo cache file for channel {ChannelId}.", channelId);
        }
    }

    private Guid GetInternalChannelId(string serviceName, string externalId)
    {
        const string internalVersionNumber = "4";
        var name = serviceName + externalId + internalVersionNumber;
        var channelType = Type.GetType("MediaBrowser.Controller.LiveTv.LiveTvChannel, Jellyfin.Controller")
            ?? typeof(Jellyfin.Plugin.TvHeadendApi.Service.LiveTvService);
        return _libraryManager.GetNewItemId(name.ToLowerInvariant(), channelType);
    }

    private static string BuildMediainfoCacheFileName(string providerTypeOrHash, string itemTypeName, string itemIdN, string? sourceId)
    {
        static string GetJellyfinHashN(string value)
        {
            var bytes = Encoding.Unicode.GetBytes(value);
#pragma warning disable CA5351
            var hashBytes = MD5.HashData(bytes);
#pragma warning restore CA5351
            return new Guid(hashBytes).ToString("N");
        }

        static bool IsHex32(string value) => value.Length == 32 && value.All(Uri.IsHexDigit);

        var providerHash = IsHex32(providerTypeOrHash)
            ? providerTypeOrHash.ToLowerInvariant()
            : GetJellyfinHashN(providerTypeOrHash);

        var coreOpenToken = string.Join(StreamIdDelimiter, itemTypeName, itemIdN, sourceId ?? string.Empty);
        var openToken = string.Join(StreamIdDelimiter, providerHash, coreOpenToken);
        return GetJellyfinHashN(openToken) + ".json";
    }

    private PluginConfiguration GetConfig()
    {
        return _tvheadendApiClient.GetCurrentConfiguration()
            ?? throw new InvalidOperationException("Plugin configuration is not available.");
    }
}
