using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
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
    private readonly Func<string?> _cachePathResolver;

    public MediaSourceService(
        ILogger<MediaSourceService> logger,
        ILibraryManager libraryManager,
        IProfileContainerResolver streamProfileContainerResolver,
        IApiClient tvheadendApiClient,
        IUrlBuilder tvheadendUrlBuilder)
        : this(logger, libraryManager, streamProfileContainerResolver, tvheadendApiClient, tvheadendUrlBuilder, () => Plugin.Instance?.CachePath)
    {
    }

    internal MediaSourceService(
        ILogger<MediaSourceService> logger,
        ILibraryManager libraryManager,
        IProfileContainerResolver streamProfileContainerResolver,
        IApiClient tvheadendApiClient,
        IUrlBuilder tvheadendUrlBuilder,
        Func<string?> cachePathResolver)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _streamProfileContainerResolver = streamProfileContainerResolver ?? throw new ArgumentNullException(nameof(streamProfileContainerResolver));
        _tvheadendApiClient = tvheadendApiClient ?? throw new ArgumentNullException(nameof(tvheadendApiClient));
        _tvheadendUrlBuilder = tvheadendUrlBuilder ?? throw new ArgumentNullException(nameof(tvheadendUrlBuilder));
        _cachePathResolver = cachePathResolver ?? throw new ArgumentNullException(nameof(cachePathResolver));
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
        var encodedChannelId = Uri.EscapeDataString(channelId);
        var encodedProfile = Uri.EscapeDataString(config.StreamingProfile ?? string.Empty);
        var streamUrl = _tvheadendUrlBuilder.BuildUrlWithParameterAuth(config, $"stream/channel/{encodedChannelId}?profile={encodedProfile}");
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
        await EnsureMediaInfoCacheStateAsync(channelId, streamUrl, profileSnapshot, config.EnableMediaInfoCacheWrite, config.EnableMediaInfoCacheValidation, cancellationToken).ConfigureAwait(false);

        return mediaSource;
    }

    private async Task EnsureMediaInfoCacheStateAsync(string channelId, string streamUrl, ProfileSnapshot profileSnapshot, bool proactiveCacheEnabled, bool validationEnabled, CancellationToken cancellationToken)
    {
        try
        {
            var cacheSnapshot = await TryGetMediainfoCacheSnapshotAsync(channelId, cancellationToken).ConfigureAwait(false);
            if (cacheSnapshot == null)
            {
                if (proactiveCacheEnabled)
                {
                    await TryWriteMediaInfoCacheAsync(channelId, streamUrl, profileSnapshot, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            if (!validationEnabled)
            {
                return;
            }

            var profileMatches = string.Equals(profileSnapshot.ProfileName, cacheSnapshot.ProfileName, StringComparison.OrdinalIgnoreCase);
            var videoCodecMatches = string.Equals(profileSnapshot.VideoCodec, cacheSnapshot.VideoCodec, StringComparison.OrdinalIgnoreCase);
            var audioCodecMatches = string.Equals(profileSnapshot.AudioCodec, cacheSnapshot.AudioCodec, StringComparison.OrdinalIgnoreCase);
            var containerMatches = string.Equals(profileSnapshot.Container, cacheSnapshot.Container, StringComparison.OrdinalIgnoreCase);
            var allMatch = profileMatches && videoCodecMatches && audioCodecMatches && containerMatches;

            _logger.LogInformation(
                "TVHeadend profile and mediainfo cache comparison for channel {ChannelId}: IsMatch={IsMatch}, ProfileMatch={ProfileMatch}, VideoCodecMatch={VideoCodecMatch}, AudioCodecMatch={AudioCodecMatch}, ContainerMatch={ContainerMatch}",
                channelId,
                allMatch,
                profileMatches,
                videoCodecMatches,
                audioCodecMatches,
                containerMatches);

            if (allMatch)
            {
                return;
            }

            if (!proactiveCacheEnabled)
            {
                if (!string.IsNullOrWhiteSpace(cacheSnapshot.CacheFilePath) && File.Exists(cacheSnapshot.CacheFilePath))
                {
                    File.Delete(cacheSnapshot.CacheFilePath);
                    _logger.LogInformation("Deleted mismatching mediainfo cache file for channel {ChannelId}: {CacheFile}", channelId, cacheSnapshot.CacheFilePath);
                }

                return;
            }

            await TryWriteMediaInfoCacheAsync(channelId, streamUrl, profileSnapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enforce mediainfo cache state for channel {ChannelId}.", channelId);
        }
    }

    private async Task<CacheSnapshot?> TryGetMediainfoCacheSnapshotAsync(string channelId, CancellationToken cancellationToken)
    {
        var cachePath = _cachePathResolver();
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

        try
        {
            var json = await File.ReadAllTextAsync(cacheFilePath, cancellationToken).ConfigureAwait(false);
            if (TryParseCacheSnapshot(json, cacheFilePath, out var cacheSnapshot))
            {
                return cacheSnapshot;
            }

            _logger.LogWarning("Ignoring malformed mediainfo cache file for channel {ChannelId}: {CacheFile}", channelId, cacheFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to read mediainfo cache file for channel {ChannelId}: {CacheFile}", channelId, cacheFilePath);
        }

        TryDeleteCacheFile(cacheFilePath, channelId);
        return null;
    }

    internal static bool TryParseCacheSnapshot(string json, string? cacheFilePath, [NotNullWhen(true)] out CacheSnapshot? cacheSnapshot)
    {
        cacheSnapshot = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var path = root.TryGetProperty("Path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String
                ? pathElement.GetString()
                : null;
            var profileName = ExtractQueryParameter(path, "profile");
            var videoCodec = ExtractCodecFromMediaStreams(root, "Video");
            var audioCodec = ExtractCodecFromMediaStreams(root, "Audio");
            var container = root.TryGetProperty("Container", out var containerElement) && containerElement.ValueKind == JsonValueKind.String
                ? containerElement.GetString()
                : null;

            cacheSnapshot = new CacheSnapshot(profileName, videoCodec, audioCodec, container, cacheFilePath);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static string? ExtractCodecFromMediaStreams(JsonElement root, string streamType)
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

    internal static string? ExtractQueryParameter(string? url, string parameterName)
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

    private async Task TryWriteMediaInfoCacheAsync(string channelId, string streamUrl, ProfileSnapshot profileSnapshot, CancellationToken cancellationToken)
    {
        try
        {
            var cachePath = _cachePathResolver();
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

            if (!Directory.Exists(mediaInfoDir))
            {
                Directory.CreateDirectory(mediaInfoDir);
            }

            var cacheContent = BuildMediaInfoCacheContent(streamUrl, profileSnapshot);

            var json = JsonSerializer.Serialize(cacheContent);
            await File.WriteAllTextAsync(cacheFilePath, json, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Wrote proactive mediainfo cache for channel {ChannelId}: Profile={ProfileName}, Container={Container}, VideoCodec={VideoCodec}, AudioCodec={AudioCodec}",
                channelId,
                profileSnapshot.ProfileName,
                NormalizeContainerForCache(profileSnapshot.Container),
                GetNormalizedVideoCodec(profileSnapshot),
                GetNormalizedAudioCodec(profileSnapshot));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write mediainfo cache file for channel {ChannelId}.", channelId);
        }
    }

    internal static Dictionary<string, object?> BuildMediaInfoCacheContent(string streamUrl, ProfileSnapshot profileSnapshot)
    {
        var normalizedContainer = NormalizeContainerForCache(profileSnapshot.Container);
        var videoCodec = GetNormalizedVideoCodec(profileSnapshot);
        var audioCodec = GetNormalizedAudioCodec(profileSnapshot);
        var videoCodecTag = string.Equals(videoCodec, "h264", StringComparison.OrdinalIgnoreCase) ? "avc1" : videoCodec;
        var audioCodecTag = string.Equals(audioCodec, "aac", StringComparison.OrdinalIgnoreCase) ? "mp4a" : audioCodec;

        return new Dictionary<string, object?>
        {
            ["Protocol"] = "Http",
            ["Path"] = streamUrl,
            ["Type"] = "Default",
            ["Container"] = normalizedContainer,
            ["IsRemote"] = true,
            ["ReadAtNativeFramerate"] = false,
            ["IgnoreDts"] = false,
            ["SupportsTranscoding"] = true,
            ["SupportsDirectStream"] = true,
            ["SupportsDirectPlay"] = true,
            ["IsInfiniteStream"] = true,
            ["SupportsProbing"] = true,
            ["MediaStreams"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["Codec"] = videoCodec,
                    ["CodecTag"] = videoCodecTag,
                    ["DisplayTitle"] = videoCodec.ToUpperInvariant(),
                    ["IsDefault"] = true,
                    ["Height"] = 720,
                    ["Width"] = 1280,
                    ["AverageFrameRate"] = 25.0,
                    ["Type"] = "Video",
                    ["Index"] = 0,
                },
                new Dictionary<string, object?>
                {
                    ["Codec"] = audioCodec,
                    ["CodecTag"] = audioCodecTag,
                    ["DisplayTitle"] = audioCodec.ToUpperInvariant(),
                    ["BitRate"] = 128000,
                    ["Channels"] = 2,
                    ["SampleRate"] = 48000,
                    ["IsDefault"] = true,
                    ["Type"] = "Audio",
                    ["Index"] = 1,
                },
            },
        };
    }

    internal static string NormalizeContainerForCache(string? container)
    {
        // Use the container as reported by TVHeadend. Only default to "mpegts" if empty/null.
        return string.IsNullOrWhiteSpace(container) ? "mpegts" : container;
    }

    private static string GetNormalizedVideoCodec(ProfileSnapshot profileSnapshot)
        => string.IsNullOrWhiteSpace(profileSnapshot.VideoCodec) ? "h264" : profileSnapshot.VideoCodec;

    private static string GetNormalizedAudioCodec(ProfileSnapshot profileSnapshot)
        => string.IsNullOrWhiteSpace(profileSnapshot.AudioCodec) ? "aac" : profileSnapshot.AudioCodec;

    private Guid GetInternalChannelId(string externalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        // Mirrors Jellyfin.LiveTv.LiveTvDtoService.GetInternalChannelId.
        var name = OrchestratorServiceName + externalId + InternalChannelVersionNumber;
        return _libraryManager.GetNewItemId(name.ToLowerInvariant(), typeof(LiveTvChannel));
    }

    internal static string BuildMediainfoCacheFileName(string providerTypeOrHash, string itemTypeName, string itemIdN, string? sourceId)
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

    private void TryDeleteCacheFile(string cacheFilePath, string channelId)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(cacheFilePath) && File.Exists(cacheFilePath))
            {
                File.Delete(cacheFilePath);
                _logger.LogInformation("Deleted unreadable mediainfo cache file for channel {ChannelId}: {CacheFile}", channelId, cacheFilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to delete unreadable mediainfo cache file for channel {ChannelId}: {CacheFile}", channelId, cacheFilePath);
        }
    }

    private PluginConfiguration GetConfig()
    {
        return _tvheadendApiClient.GetCurrentConfiguration()
            ?? throw new InvalidOperationException("Plugin configuration is not available.");
    }

    internal sealed record CacheSnapshot(
        string? ProfileName,
        string? VideoCodec,
        string? AudioCodec,
        string? Container,
        string? CacheFilePath);
}
