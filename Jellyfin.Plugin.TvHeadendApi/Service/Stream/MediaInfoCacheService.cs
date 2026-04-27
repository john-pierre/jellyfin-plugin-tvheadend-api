using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Guide;
using Jellyfin.Plugin.TvHeadendApi.Service.Metric;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Storage;
using Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Stream;

/// <summary>
/// Manages proactive mediainfo cache files that pre-populate codec and container
/// metadata so Jellyfin can skip the expensive FFprobe probe on live streams.
/// </summary>
internal sealed class MediaInfoCacheService : IMediaInfoCacheService
{
    private const char StreamIdDelimiter = '_';
    private const string OrchestratorServiceName = "TvHeadendApi";
    private const string InternalChannelVersionNumber = "4";

    /// <summary>
    /// JSON options that match Jellyfin's internal mediainfo cache format.
    /// Enums are serialized as strings (e.g. "Http", "Video") to stay compatible
    /// with <c>Jellyfin.Extensions.Json.JsonDefaults.Options</c>.
    /// </summary>
    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<MediaInfoCacheService> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly Func<string?> _cachePathResolver;
    private readonly IGuideService _guideService;
    private readonly IStreamingProfileResolver _streamingProfileResolver;
    private readonly IProfileContainerResolver _profileContainerResolver;
    private readonly Relay.IRelayUrlBuilder _relayUrlBuilder;
    private readonly IApiClient _apiClient;
    private readonly IMediaEncoder? _mediaEncoder;
    private readonly IApplicationPaths? _applicationPaths;

    public MediaInfoCacheService(
        ILogger<MediaInfoCacheService> logger,
        ILibraryManager libraryManager,
        CachePathProvider cachePathProvider,
        IGuideService guideService,
        IStreamingProfileResolver streamingProfileResolver,
        IProfileContainerResolver profileContainerResolver,
        Relay.IRelayUrlBuilder relayUrlBuilder,
        IApiClient apiClient,
        IMediaEncoder mediaEncoder,
        IApplicationPaths applicationPaths)
        : this(logger, libraryManager, () => cachePathProvider.Path)
    {
        _guideService = guideService ?? throw new ArgumentNullException(nameof(guideService));
        _streamingProfileResolver = streamingProfileResolver ?? throw new ArgumentNullException(nameof(streamingProfileResolver));
        _profileContainerResolver = profileContainerResolver ?? throw new ArgumentNullException(nameof(profileContainerResolver));
        _relayUrlBuilder = relayUrlBuilder ?? throw new ArgumentNullException(nameof(relayUrlBuilder));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _mediaEncoder = mediaEncoder;
        _applicationPaths = applicationPaths;
    }

    internal MediaInfoCacheService(
        ILogger<MediaInfoCacheService> logger,
        ILibraryManager libraryManager,
        Func<string?> cachePathResolver)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _cachePathResolver = cachePathResolver ?? throw new ArgumentNullException(nameof(cachePathResolver));

        // Warmup / probe dependencies are not available in the internal test constructor.
        _guideService = null!;
        _streamingProfileResolver = null!;
        _profileContainerResolver = null!;
        _relayUrlBuilder = null!;
        _apiClient = null!;
        _mediaEncoder = null;
        _applicationPaths = null;
    }

    /// <inheritdoc />
    public async Task EnsureMediaInfoCacheStateAsync(string channelId, string streamUrl, ProfileSnapshot profileSnapshot, bool proactiveCacheEnabled, bool validationEnabled, CancellationToken cancellationToken)
    {
        try
        {
            var cacheSnapshot = await TryGetMediainfoCacheSnapshotAsync(channelId, cancellationToken).ConfigureAwait(false);
            if (cacheSnapshot == null)
            {
                MetricService.CacheMissCount.Add(1);
                if (proactiveCacheEnabled)
                {
                    await TryWriteMediaInfoCacheAsync(channelId, streamUrl, profileSnapshot, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            MetricService.CacheHitCount.Add(1);

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
                    MetricService.CacheInvalidationCount.Add(1);
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

    /// <inheritdoc />
    string IMediaInfoCacheService.BuildMediainfoCacheFileName(string providerTypeOrHash, string itemTypeName, string itemIdN, string? sourceId)
        => BuildMediainfoCacheFileName(providerTypeOrHash, itemTypeName, itemIdN, sourceId);

    /// <inheritdoc />
    public string BuildChannelCacheFileName(string channelId, string? sourceId)
    {
        var providerTypeFullName = "Jellyfin.LiveTv.LiveTvMediaSourceProvider";
        var itemTypeName = "LiveTvChannel";
        var internalChannelId = GetInternalChannelId(channelId);
        var itemIdN = internalChannelId.ToString("N", CultureInfo.InvariantCulture);
        return BuildMediainfoCacheFileName(providerTypeFullName, itemTypeName, itemIdN, sourceId);
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

    /// <inheritdoc />
    public async Task<CacheWarmupResult> WarmAllChannelCachesAsync(CancellationToken cancellationToken)
    {
        var config = _apiClient.GetCurrentConfiguration();
        if (config == null)
        {
            return new CacheWarmupResult(0, 0, 0, 0, new List<string> { "Plugin configuration is not available." });
        }

        var channels = (await _guideService.GetChannelsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var totalChannels = channels.Count;
        var warmed = 0;
        var alreadyCached = 0;
        var failed = 0;
        var errors = new List<string>();
        var lockObj = new object();

        // Limit parallelism to 2 — each FFprobe opens a real TVH stream and we
        // must not overwhelm the tuners.
        using var throttle = new SemaphoreSlim(2);

        var tasks = channels.Select(async channel =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var channelId = channel.Id;
                if (string.IsNullOrWhiteSpace(channelId))
                {
                    lock (lockObj)
                    {
                        failed++;
                        errors.Add("Channel with empty ID skipped.");
                    }

                    return;
                }

                // Check if cache already exists and is valid.
                var existingSnapshot = await TryGetMediainfoCacheSnapshotAsync(channelId, cancellationToken).ConfigureAwait(false);
                if (existingSnapshot != null)
                {
                    lock (lockObj)
                    {
                        alreadyCached++;
                    }

                    return;
                }

                // Resolve the effective profile for this channel.
                var profileContext = new StreamingProfileContext { ChannelId = channelId };
                var resolution = _streamingProfileResolver.Resolve(profileContext);
                var effectiveProfile = resolution.EffectiveTvHeadendProfile;

                // Build the stream URL (includes auth token for TVH access).
                var streamUrl = await _relayUrlBuilder.BuildTokenizedStreamRelayUrlAsync(
                    channelId, effectiveProfile, null, null, null, cancellationToken).ConfigureAwait(false);

                // Attempt real FFprobe probing via IMediaEncoder when available.
                var probed = await TryProbeAndWriteCacheAsync(channelId, streamUrl, cancellationToken).ConfigureAwait(false);

                if (!probed)
                {
                    // Fallback: write a synthetic cache file from the profile snapshot.
                    var profileSnapshot = await _profileContainerResolver.ResolveProfileSnapshotAsync(config, cancellationToken).ConfigureAwait(false);
                    await TryWriteMediaInfoCacheAsync(channelId, streamUrl, profileSnapshot, cancellationToken).ConfigureAwait(false);
                }

                lock (lockObj)
                {
                    warmed++;
                }
            }
            catch (Exception ex)
            {
                lock (lockObj)
                {
                    failed++;
                    errors.Add($"Channel {channel.Id}: {ex.Message}");
                }
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        _logger.LogInformation(
            "Cache warmup completed: Total={Total}, Warmed={Warmed}, AlreadyCached={AlreadyCached}, Failed={Failed}",
            totalChannels,
            warmed,
            alreadyCached,
            failed);

        return new CacheWarmupResult(totalChannels, warmed, alreadyCached, failed, errors);
    }

    /// <summary>
    /// Probes a live channel stream with FFprobe via <see cref="IMediaEncoder"/> and
    /// writes the resulting <see cref="MediaSourceInfo"/> to the Jellyfin mediainfo
    /// cache directory. Returns <c>true</c> when the probe and write both succeed.
    /// </summary>
    private async Task<bool> TryProbeAndWriteCacheAsync(string channelId, string streamUrl, CancellationToken cancellationToken)
    {
        if (_mediaEncoder == null || _applicationPaths == null)
        {
            return false;
        }

        try
        {
            // Per-channel timeout: 15 seconds (FFprobe typically takes ~6-8s on live streams).
            using var channelCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            channelCts.CancelAfter(TimeSpan.FromSeconds(15));

            // Build a lightweight MediaSourceInfo that tells FFprobe where to look.
            var probeSource = new MediaSourceInfo
            {
                Path = streamUrl,
                Protocol = MediaProtocol.Http,
                IsRemote = true,
                IsInfiniteStream = true,
                SupportsProbing = true,
                AnalyzeDurationMs = 3000,
            };

            var mediaInfo = await _mediaEncoder.GetMediaInfo(
                new MediaInfoRequest
                {
                    MediaSource = probeSource,
                    MediaType = DlnaProfileType.Video,
                    ExtractChapters = false,
                },
                channelCts.Token).ConfigureAwait(false);

            if (mediaInfo == null)
            {
                _logger.LogWarning("FFprobe returned null for channel {ChannelId}.", channelId);
                return false;
            }

            // Keep only one video + one audio stream (same filtering Jellyfin applies
            // to live stream probes) and reset indices so Jellyfin treats them as live.
            var filteredStreams = new List<MediaStream>();
            filteredStreams.AddRange(mediaInfo.MediaStreams.Where(s => s.Type == MediaStreamType.Video).Take(1));
            filteredStreams.AddRange(mediaInfo.MediaStreams.Where(s => s.Type == MediaStreamType.Audio).Take(1));
            foreach (var stream in filteredStreams)
            {
                stream.Index = -1;
                stream.Language = null;
            }

            // Build the full MediaSourceInfo that Jellyfin expects in the cache.
            var cachedSource = new MediaSourceInfo
            {
                Path = streamUrl,
                Protocol = MediaProtocol.Http,
                Container = mediaInfo.Container,
                Bitrate = mediaInfo.Bitrate,
                IsRemote = true,
                IsInfiniteStream = true,
                SupportsProbing = true,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                MediaStreams = filteredStreams,
                Formats = mediaInfo.Formats,
                Timestamp = mediaInfo.Timestamp,
                Video3DFormat = mediaInfo.Video3DFormat,
                VideoType = mediaInfo.VideoType,
                AnalyzeDurationMs = 3000,
            };

            // Resolve the cache file path using the same key Jellyfin would use.
            var providerTypeFullName = "Jellyfin.LiveTv.LiveTvMediaSourceProvider";
            var itemTypeName = "LiveTvChannel";
            var internalChannelId = GetInternalChannelId(channelId);
            var itemIdN = internalChannelId.ToString("N", CultureInfo.InvariantCulture);
            var cacheFileName = BuildMediainfoCacheFileName(providerTypeFullName, itemTypeName, itemIdN, channelId);
            var cacheDir = Path.Combine(_applicationPaths.CachePath, "mediainfo");
            Directory.CreateDirectory(cacheDir);
            var cacheFilePath = Path.Combine(cacheDir, cacheFileName);

            var json = JsonSerializer.Serialize(cachedSource, CacheJsonOptions);
            await File.WriteAllTextAsync(cacheFilePath, json, cancellationToken).ConfigureAwait(false);

            var videoCodec = filteredStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video)?.Codec;
            var audioCodec = filteredStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio)?.Codec;
            _logger.LogInformation(
                "FFprobe warmup succeeded for channel {ChannelId}: Container={Container}, VideoCodec={VideoCodec}, AudioCodec={AudioCodec}",
                channelId,
                mediaInfo.Container,
                videoCodec,
                audioCodec);

            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("FFprobe timed out for channel {ChannelId}, falling back to synthetic cache.", channelId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FFprobe failed for channel {ChannelId}, falling back to synthetic cache.", channelId);
            return false;
        }
    }

    /// <inheritdoc />
    public Task<int> InvalidateAllCachesAsync()
    {
        var cachePath = _cachePathResolver();
        if (string.IsNullOrWhiteSpace(cachePath))
        {
            return Task.FromResult(0);
        }

        var mediaInfoDir = Path.Combine(cachePath, "mediainfo");
        if (!Directory.Exists(mediaInfoDir))
        {
            return Task.FromResult(0);
        }

        var files = Directory.GetFiles(mediaInfoDir, "*.json");
        var deleted = 0;
        foreach (var file in files)
        {
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to delete cache file during invalidation: {File}", file);
            }
        }

        _logger.LogInformation("Invalidated all mediainfo caches: {Deleted} files deleted.", deleted);
        MetricService.CacheInvalidationCount.Add(deleted);
        return Task.FromResult(deleted);
    }

    /// <inheritdoc />
    public Task<bool> InvalidateChannelCacheAsync(string channelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        var cachePath = _cachePathResolver();
        if (string.IsNullOrWhiteSpace(cachePath))
        {
            return Task.FromResult(false);
        }

        var cacheFileName = BuildChannelCacheFileName(channelId, channelId);
        var cacheFilePath = Path.Combine(cachePath, "mediainfo", cacheFileName);

        if (!File.Exists(cacheFilePath))
        {
            return Task.FromResult(false);
        }

        try
        {
            File.Delete(cacheFilePath);
            MetricService.CacheInvalidationCount.Add(1);
            _logger.LogInformation("Invalidated mediainfo cache for channel {ChannelId}: {CacheFile}", channelId, cacheFilePath);
            return Task.FromResult(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to invalidate mediainfo cache for channel {ChannelId}: {CacheFile}", channelId, cacheFilePath);
            return Task.FromResult(false);
        }
    }

    internal sealed record CacheSnapshot(
        string? ProfileName,
        string? VideoCodec,
        string? AudioCodec,
        string? Container,
        string? CacheFilePath);
}
