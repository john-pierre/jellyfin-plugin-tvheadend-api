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
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Stream;

/// <summary>
/// Handles stream URL and media source construction for live channels.
/// </summary>
internal sealed class MediaSourceService : IMediaSourceService
{
    private const char StreamIdDelimiter = '_';
    private const string OrchestratorServiceName = "TvHeadendApi";
    private const string InternalChannelVersionNumber = "4";

    private readonly ILogger<MediaSourceService> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IProfileContainerResolver _streamProfileContainerResolver;
    private readonly IApiClient _tvheadendApiClient;
    private readonly IUrlBuilder _tvheadendUrlBuilder;

    public MediaSourceService(
        ILogger<MediaSourceService> logger,
        ILibraryManager libraryManager,
        IProfileContainerResolver streamProfileContainerResolver,
        IApiClient tvheadendApiClient,
        IUrlBuilder tvheadendUrlBuilder)
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
        var internalChannelId = GetInternalChannelId(channelId);
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

        // Append streaming profile parameter, then apply auth token as query parameter.
        // The auth token is required for direct playback from clients.
        var streamUrl = _tvheadendUrlBuilder.BuildUrlWithParameterAuth(config, $"stream/channel/{channelId}?profile={config.StreamingProfile}");
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

        var profileSnapshot = await _streamProfileContainerResolver.ResolveProfileSnapshotAsync(config, cancellationToken).ConfigureAwait(false);
        await TryLogProfileAndCacheMatchAsync(channelId, profileSnapshot).ConfigureAwait(false);

        if (config.EnableMediaInfoCacheWrite)
        {
            await TryWriteMediaInfoCacheAsync(channelId, streamUrl, container).ConfigureAwait(false);
        }

        return mediaSource;
    }

    private async Task TryLogProfileAndCacheMatchAsync(string channelId, ProfileSnapshot profileSnapshot)
    {
        try
        {
            _logger.LogInformation(
                "TVHeadend stream profile for channel {ChannelId}: Profile={ProfileName}, Uuid={ProfileUuid}, Class={ProfileClass}, Container={Container}, VideoCodecRef={VideoCodecRef}, AudioCodecRef={AudioCodecRef}, VideoCodec={VideoCodec}, AudioCodec={AudioCodec}",
                channelId,
                profileSnapshot.ProfileName,
                profileSnapshot.ProfileUuid,
                profileSnapshot.ProfileClass,
                profileSnapshot.Container,
                profileSnapshot.VideoCodecReference,
                profileSnapshot.AudioCodecReference,
                profileSnapshot.VideoCodec,
                profileSnapshot.AudioCodec);

            var cacheSnapshot = await TryGetMediainfoCacheSnapshotAsync(channelId).ConfigureAwait(false);
            if (cacheSnapshot == null)
            {
                _logger.LogInformation("No mediainfo cache entry found for channel {ChannelId}.", channelId);
                return;
            }

            _logger.LogInformation(
                "Mediainfo cache for channel {ChannelId}: CacheFile={CacheFile}, Profile={ProfileName}, VideoCodec={VideoCodec}, AudioCodec={AudioCodec}",
                channelId,
                cacheSnapshot.CacheFilePath,
                cacheSnapshot.ProfileName,
                cacheSnapshot.VideoCodec,
                cacheSnapshot.AudioCodec);

            var profileMatches = string.Equals(profileSnapshot.ProfileName, cacheSnapshot.ProfileName, StringComparison.OrdinalIgnoreCase);
            var videoCodecMatches = string.Equals(profileSnapshot.VideoCodec, cacheSnapshot.VideoCodec, StringComparison.OrdinalIgnoreCase);
            var audioCodecMatches = string.Equals(profileSnapshot.AudioCodec, cacheSnapshot.AudioCodec, StringComparison.OrdinalIgnoreCase);
            var allMatch = profileMatches && videoCodecMatches && audioCodecMatches;

            _logger.LogInformation(
                "TVHeadend profile and mediainfo cache comparison for channel {ChannelId}: IsMatch={IsMatch}, ProfileMatch={ProfileMatch}, VideoCodecMatch={VideoCodecMatch}, AudioCodecMatch={AudioCodecMatch}",
                channelId,
                allMatch,
                profileMatches,
                videoCodecMatches,
                audioCodecMatches);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compare TVHeadend profile with mediainfo cache for channel {ChannelId}.", channelId);
        }
    }

    private async Task<CacheSnapshot?> TryGetMediainfoCacheSnapshotAsync(string channelId)
    {
        var cachePath = Plugin.Instance?.CachePath;
        if (string.IsNullOrWhiteSpace(cachePath))
        {
            return null;
        }

        var providerTypeFullName = "Jellyfin.LiveTv.LiveTvMediaSourceProvider";
        var itemTypeName = "LiveTvChannel";
        var internalChannelId = GetInternalChannelId(channelId);
        var itemIdN = internalChannelId.ToString("N", CultureInfo.InvariantCulture);
        var cacheFileName = BuildMediainfoCacheFileName(providerTypeFullName, itemTypeName, itemIdN, channelId);
        var cacheFilePath = Path.Combine(cachePath, "mediainfo", cacheFileName);

        if (!File.Exists(cacheFilePath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(cacheFilePath).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var path = root.TryGetProperty("Path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String
            ? pathElement.GetString()
            : null;
        var profileName = ExtractQueryParameter(path, "profile");
        var videoCodec = ExtractCodecFromMediaStreams(root, "Video");
        var audioCodec = ExtractCodecFromMediaStreams(root, "Audio");

        return new CacheSnapshot(profileName, videoCodec, audioCodec, cacheFilePath);
    }

    private static string? ExtractCodecFromMediaStreams(JsonElement root, string streamType)
    {
        if (!root.TryGetProperty("MediaStreams", out var mediaStreams) || mediaStreams.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var stream in mediaStreams.EnumerateArray())
        {
            var type = stream.TryGetProperty("Type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;
            if (!string.Equals(type, streamType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return stream.TryGetProperty("Codec", out var codecElement) && codecElement.ValueKind == JsonValueKind.String
                ? codecElement.GetString()
                : null;
        }

        return null;
    }

    private static string? ExtractQueryParameter(string? url, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(parameterName))
        {
            return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var query = uri.Query;
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var splitIndex = pair.IndexOf('=', StringComparison.Ordinal);
            if (splitIndex <= 0)
            {
                continue;
            }

            var key = pair.Substring(0, splitIndex);
            if (!string.Equals(key, parameterName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = pair.Substring(splitIndex + 1);
            return Uri.UnescapeDataString(value);
        }

        return null;
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
            var internalChannelId = GetInternalChannelId(channelId);
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

    private Guid GetInternalChannelId(string externalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        // Mirrors Jellyfin.LiveTv.LiveTvDtoService.GetInternalChannelId.
        var name = OrchestratorServiceName + externalId + InternalChannelVersionNumber;
        return _libraryManager.GetNewItemId(name.ToLowerInvariant(), typeof(LiveTvChannel));
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

    private sealed record CacheSnapshot(
        string? ProfileName,
        string? VideoCodec,
        string? AudioCodec,
        string? CacheFilePath);
}
