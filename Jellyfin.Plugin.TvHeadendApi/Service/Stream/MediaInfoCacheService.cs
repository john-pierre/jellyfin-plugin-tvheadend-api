using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Guide;
using Jellyfin.Plugin.TvHeadendApi.Service.Metrics;
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

    // In-process counters mirrored to the OTel instruments so the dashboard can show
    // them without a metrics listener. Updated with Interlocked — the service is a singleton.
    private long _cacheHits;
    private long _cacheMisses;
    private long _cacheMismatches;
    private long _cacheInvalidations;

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
    public MediaInfoCacheCounters GetCounters()
    {
        return new MediaInfoCacheCounters(
            Interlocked.Read(ref _cacheHits),
            Interlocked.Read(ref _cacheMisses),
            Interlocked.Read(ref _cacheMismatches),
            Interlocked.Read(ref _cacheInvalidations));
    }

    /// <inheritdoc />
    public void RecordStreamBuildReuseHit()
    {
        MetricService.CacheHitCount.Add(1);
        Interlocked.Increment(ref _cacheHits);
    }

    /// <inheritdoc />
    public async Task<MediaInfoCacheStatus> EnsureMediaInfoCacheStateAsync(string channelId, string streamUrl, ProfileSnapshot profileSnapshot, bool proactiveCacheEnabled, bool validationEnabled, CancellationToken cancellationToken)
    {
        try
        {
            var cacheSnapshot = await TryGetMediainfoCacheSnapshotAsync(channelId, cancellationToken).ConfigureAwait(false);
            if (cacheSnapshot == null)
            {
                MetricService.CacheMissCount.Add(1);
                Interlocked.Increment(ref _cacheMisses);
                if (proactiveCacheEnabled)
                {
                    if (await TryRestoreFromProfileStoreAsync(channelId, profileSnapshot.ProfileName, streamUrl, cancellationToken).ConfigureAwait(false))
                    {
                        return MediaInfoCacheStatus.Restored;
                    }

                    await TryWriteMediaInfoCacheAsync(channelId, streamUrl, profileSnapshot, cancellationToken).ConfigureAwait(false);
                }

                return MediaInfoCacheStatus.Miss;
            }

            if (!validationEnabled)
            {
                // The existing file is used as-is — a warm start.
                MetricService.CacheHitCount.Add(1);
                Interlocked.Increment(ref _cacheHits);
                return MediaInfoCacheStatus.Hit;
            }

            // Pass-through-like profiles carry no output codecs in their snapshot (the stream
            // keeps the SOURCE codecs, which only a probe can know). For those, a probed cache
            // file with real codecs is exactly what we want — comparing the empty snapshot
            // codec against it would flag every probed file as a mismatch and destroy it.
            var passThroughLike = string.IsNullOrWhiteSpace(profileSnapshot.VideoCodec);
            var profileMatches = string.Equals(profileSnapshot.ProfileName, cacheSnapshot.ProfileName, StringComparison.OrdinalIgnoreCase);
            var videoCodecMatches = passThroughLike || string.Equals(profileSnapshot.VideoCodec, cacheSnapshot.VideoCodec, StringComparison.OrdinalIgnoreCase);
            var audioCodecMatches = passThroughLike || string.Equals(profileSnapshot.AudioCodec, cacheSnapshot.AudioCodec, StringComparison.OrdinalIgnoreCase);
            var containerMatches = string.Equals(
                CanonicalizeContainerForComparison(profileSnapshot.Container),
                CanonicalizeContainerForComparison(cacheSnapshot.Container),
                StringComparison.Ordinal);
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
                // A real hit — count it only here, after validation confirmed the match.
                // Mismatches used to be counted as hits because the counter fired before validation.
                MetricService.CacheHitCount.Add(1);
                Interlocked.Increment(ref _cacheHits);

                // The media data is current, but the cached Path may still carry an older
                // stream URL (stale relay token, previous delivery-mode URL shape). Jellyfin
                // hands the cached Path verbatim to players on Direct Play starts, so it must
                // be refreshed on every start.
                await TryRefreshCachedStreamUrlAsync(cacheSnapshot, channelId, streamUrl, cancellationToken).ConfigureAwait(false);
                return MediaInfoCacheStatus.Hit;
            }

            // A cache file existed but did not match the effective profile — a mismatch,
            // counted as its own outcome (NOT a hit).
            MetricService.CacheMismatchCount.Add(1);
            Interlocked.Increment(ref _cacheMismatches);

            // Preserve the outgoing file in the per-profile store BEFORE it gets replaced:
            // probed source-codec data (pass-through profiles) is expensive to regain and must
            // survive a rule switching this channel to a different TVHeadend profile.
            if (!string.IsNullOrWhiteSpace(cacheSnapshot.CacheFilePath) && File.Exists(cacheSnapshot.CacheFilePath))
            {
                MirrorToProfileStore(channelId, cacheSnapshot.ProfileName, cacheSnapshot.CacheFilePath, overwrite: false);
            }

            if (!proactiveCacheEnabled)
            {
                if (!string.IsNullOrWhiteSpace(cacheSnapshot.CacheFilePath) && File.Exists(cacheSnapshot.CacheFilePath))
                {
                    File.Delete(cacheSnapshot.CacheFilePath);
                    MetricService.CacheInvalidationCount.Add(1);
                    Interlocked.Increment(ref _cacheInvalidations);
                    _logger.LogInformation("Deleted mismatching mediainfo cache file for channel {ChannelId}: {CacheFile}", channelId, cacheSnapshot.CacheFilePath);
                }

                return MediaInfoCacheStatus.Mismatch;
            }

            // Prefer the stored per-profile file (probed data survives rule ping-pong);
            // fall back to a synthetic write only when this combination was never seen.
            if (!await TryRestoreFromProfileStoreAsync(channelId, profileSnapshot.ProfileName, streamUrl, cancellationToken).ConfigureAwait(false))
            {
                await TryWriteMediaInfoCacheAsync(channelId, streamUrl, profileSnapshot, cancellationToken).ConfigureAwait(false);
            }

            return MediaInfoCacheStatus.Mismatch;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enforce mediainfo cache state for channel {ChannelId}.", channelId);
            return MediaInfoCacheStatus.Unknown;
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

            // Mirror into the per-profile store so this (channel, profile) combination can be
            // restored after another rule/profile rewrites the Jellyfin file. Never overwrite:
            // an existing store entry may hold PROBED data, which beats synthetic content.
            MirrorToProfileStore(channelId, profileSnapshot.ProfileName, cacheFilePath, overwrite: false);
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

    /// <summary>
    /// Seeds the per-profile store for every profile a configured rule can resolve to
    /// (client/user rules plus the channel's effective profile, which carries channel/group
    /// overrides), beyond the combination held by the live Jellyfin cache file. Seeding never
    /// overwrites an existing store entry, so probed data mirrored from real playback wins.
    /// Pass-through-like variants reuse the live file's source codecs only when that file
    /// itself belongs to a pass-through-like profile; anything else gets deterministic
    /// synthetic content derived from the variant profile.
    /// </summary>
    private async Task MirrorRuleProfileVariantsAsync(PluginConfiguration config, string channelId, string? streamUrl, string? jellyfinFileProfile, CancellationToken cancellationToken)
    {
        try
        {
            var settings = config.StreamingProfileSettings;
            if (settings == null)
            {
                return;
            }

            var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in (settings.ClientRules ?? new List<StreamingProfileRule>()).Concat(settings.UserRules ?? new List<StreamingProfileRule>()))
            {
                if (rule.Enabled && !string.IsNullOrWhiteSpace(rule.TvHeadendProfileName))
                {
                    variants.Add(rule.TvHeadendProfileName);
                }
            }

            // Channel/group overrides surface through the effective profile — include it so a
            // freshly configured override finds its combination pre-seeded before first start.
            var effective = _streamingProfileResolver.Resolve(new StreamingProfileContext { ChannelId = channelId }).EffectiveTvHeadendProfile;
            if (!string.IsNullOrWhiteSpace(effective))
            {
                variants.Add(effective);
            }

            variants.Remove(jellyfinFileProfile ?? string.Empty);
            if (variants.Count == 0)
            {
                return;
            }

            var cachePath = _cachePathResolver();
            if (string.IsNullOrWhiteSpace(cachePath))
            {
                return;
            }

            // The live file carries source codecs only when it belongs to a pass-through-like
            // profile; copying transcode output codecs into a pass store would poison it.
            var fileSnapshot = await _profileContainerResolver.ResolveProfileSnapshotAsync(config, jellyfinFileProfile, cancellationToken).ConfigureAwait(false);
            var fileHasSourceData = string.IsNullOrWhiteSpace(fileSnapshot.VideoCodec);

            var seeded = 0;
            var jellyfinFilePath = Path.Combine(cachePath, "mediainfo", BuildChannelCacheFileName(channelId, channelId));
            foreach (var variant in variants)
            {
                var storePath = GetProfileStorePath(channelId, variant);
                if (storePath == null || File.Exists(storePath))
                {
                    continue;
                }

                var snapshot = await _profileContainerResolver.ResolveProfileSnapshotAsync(config, variant, cancellationToken).ConfigureAwait(false);
                Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
                if (string.IsNullOrWhiteSpace(snapshot.VideoCodec) && fileHasSourceData && File.Exists(jellyfinFilePath))
                {
                    File.Copy(jellyfinFilePath, storePath, overwrite: false);
                }
                else
                {
                    streamUrl ??= await _relayUrlBuilder.BuildTokenizedStreamRelayUrlAsync(channelId, variant, null, null, null, cancellationToken).ConfigureAwait(false);
                    var content = BuildMediaInfoCacheContent(streamUrl, snapshot);
                    await File.WriteAllTextAsync(storePath, JsonSerializer.Serialize(content), cancellationToken).ConfigureAwait(false);
                }

                seeded++;
            }

            if (seeded > 0)
            {
                _logger.LogDebug("Pre-seeded {Count} rule-profile cache variants for channel {ChannelId}.", seeded, channelId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to pre-warm rule-profile cache variants for channel {ChannelId}.", channelId);
        }
    }

    /// <summary>
    /// Rewrites the <c>Path</c> of an otherwise matching cache file to the current stream URL.
    /// The cached Path is what Jellyfin hands to players on Direct Play starts — it must always
    /// carry a fresh relay token and the currently configured delivery-mode URL shape.
    /// </summary>
    private async Task TryRefreshCachedStreamUrlAsync(CacheSnapshot cacheSnapshot, string channelId, string streamUrl, CancellationToken cancellationToken)
    {
        var cacheFilePath = cacheSnapshot.CacheFilePath;
        if (string.IsNullOrWhiteSpace(cacheFilePath) || !File.Exists(cacheFilePath))
        {
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(cacheFilePath, cancellationToken).ConfigureAwait(false);
            var node = JsonNode.Parse(json);
            if (node == null)
            {
                return;
            }

            if (string.Equals((string?)node["Path"], streamUrl, StringComparison.Ordinal))
            {
                return;
            }

            node["Path"] = streamUrl;
            await File.WriteAllTextAsync(cacheFilePath, node.ToJsonString(), cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Refreshed cached stream URL for channel {ChannelId}.", channelId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to refresh cached stream URL for channel {ChannelId}.", channelId);
        }
    }

    /// <summary>
    /// Returns the per-profile store path for a (channel, profile) combination, or <c>null</c>
    /// when the cache root is unavailable. The store keeps one file per combination so that
    /// switching rules/profiles never loses probed media info.
    /// </summary>
    private string? GetProfileStorePath(string channelId, string? profileName)
    {
        var cachePath = _cachePathResolver();
        if (string.IsNullOrWhiteSpace(cachePath))
        {
            return null;
        }

        var jellyfinName = BuildChannelCacheFileName(channelId, channelId);
        return Path.Combine(cachePath, "mediainfo", "profiles", $"{jellyfinName}.{BuildProfileStoreKey(profileName)}.json");
    }

    /// <summary>
    /// Copies the current Jellyfin cache file into the per-profile store. With
    /// <paramref name="overwrite"/> = false an existing entry (possibly probed) is kept.
    /// </summary>
    private void MirrorToProfileStore(string channelId, string? profileName, string jellyfinCacheFilePath, bool overwrite)
    {
        try
        {
            var storePath = GetProfileStorePath(channelId, profileName);
            if (storePath == null || (!overwrite && File.Exists(storePath)))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
            File.Copy(jellyfinCacheFilePath, storePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to mirror mediainfo cache for channel {ChannelId} into the profile store.", channelId);
        }
    }

    /// <summary>
    /// Restores the Jellyfin cache file for a (channel, profile) combination from the
    /// per-profile store, patching the stored <c>Path</c> to the fresh stream URL (the stored
    /// one carries an expired token and the previous profile parameter). Returns <c>true</c>
    /// when the combination existed and was restored.
    /// </summary>
    private async Task<bool> TryRestoreFromProfileStoreAsync(string channelId, string? profileName, string streamUrl, CancellationToken cancellationToken)
    {
        try
        {
            var storePath = GetProfileStorePath(channelId, profileName);
            if (storePath == null || !File.Exists(storePath))
            {
                return false;
            }

            var json = await File.ReadAllTextAsync(storePath, cancellationToken).ConfigureAwait(false);
            var node = JsonNode.Parse(json);
            if (node == null)
            {
                return false;
            }

            node["Path"] = streamUrl;

            var cachePath = _cachePathResolver();
            if (string.IsNullOrWhiteSpace(cachePath))
            {
                return false;
            }

            var mediaInfoDir = Path.Combine(cachePath, "mediainfo");
            Directory.CreateDirectory(mediaInfoDir);
            var jellyfinFilePath = Path.Combine(mediaInfoDir, BuildChannelCacheFileName(channelId, channelId));
            await File.WriteAllTextAsync(jellyfinFilePath, node.ToJsonString(), cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Restored profile-specific mediainfo cache for channel {ChannelId} (profile '{Profile}').",
                channelId,
                string.IsNullOrWhiteSpace(profileName) ? "default" : profileName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to restore mediainfo cache for channel {ChannelId} from the profile store.", channelId);
            return false;
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

    /// <summary>
    /// Reduces a container name to a single canonical spelling so cache files written by
    /// different producers still compare equal.
    /// </summary>
    /// <remarks>
    /// Two writers populate the mediainfo cache with different spellings of the same container:
    /// the proactive writer stores the TVHeadend name (<c>mpegts</c>) via
    /// <see cref="NormalizeContainerForCache"/>, while the ffprobe warmup stores
    /// <c>MediaInfo.Container</c>, which Jellyfin's probe normalizer has already rewritten
    /// (<c>mpegts</c> becomes <c>ts</c>, <c>matroska</c> becomes <c>mkv</c>,
    /// <c>mpegvideo</c> becomes <c>mpeg</c>). Comparing those raw strings marked every
    /// probe-written cache file as a mismatch, which deleted it and forced Jellyfin into a
    /// fresh multi-second probe on the next channel start — defeating the warm cache the
    /// Direct Play path depends on.
    /// </remarks>
    /// <param name="container">The container name to canonicalize; may be null or empty.</param>
    /// <returns>The canonical container name, or an empty string when none was supplied.</returns>
    internal static string CanonicalizeContainerForComparison(string? container)
    {
        if (string.IsNullOrWhiteSpace(container))
        {
            return string.Empty;
        }

        return container.Trim().ToLowerInvariant() switch
        {
            "mpegts" or "ts" => "ts",
            "matroska" or "mkv" => "mkv",
            "mpegvideo" or "mpeg" => "mpeg",
            var other => other,
        };
    }

    private static string BuildProfileStoreKey(string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return "default";
        }

        var chars = profileName.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_');
        return string.Concat(chars);
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
                MetricService.CacheInvalidationCount.Add(1);
                Interlocked.Increment(ref _cacheInvalidations);
                _logger.LogInformation("Deleted unreadable mediainfo cache file for channel {ChannelId}: {CacheFile}", channelId, cacheFilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to delete unreadable mediainfo cache file for channel {ChannelId}: {CacheFile}", channelId, cacheFilePath);
        }
    }

    /// <inheritdoc />
    public Task<CacheWarmupResult> WarmAllChannelCachesAsync(CancellationToken cancellationToken)
        => WarmAllChannelCachesAsync(null!, cancellationToken);

    /// <inheritdoc />
    public async Task<CacheWarmupResult> WarmAllChannelCachesAsync(IProgress<CacheWarmupProgress>? progress, CancellationToken cancellationToken)
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

        var tasks = channels.Select(async (channel, channelIndex) =>
        {
            // Stable, pre-assigned 1-based position for all progress reports of this
            // channel. Up to two channels run concurrently, so deriving the position
            // from a shared mutable counter would let parallel tasks observe duplicate
            // or skipped indices in the progress stream.
            var position = channelIndex + 1;

            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var channelId = channel.Id;
                var channelName = channel.Name ?? channelId ?? "Unknown";

                if (string.IsNullOrWhiteSpace(channelId))
                {
                    lock (lockObj)
                    {
                        failed++;
                        errors.Add("Channel with empty ID skipped.");
                    }

                    progress?.Report(new CacheWarmupProgress(position, totalChannels, channelName, channelId ?? string.Empty, "failed", "Empty channel ID"));
                    return;
                }

                // Report that we are probing this channel.
                progress?.Report(new CacheWarmupProgress(
                    position,
                    totalChannels,
                    channelName,
                    channelId,
                    "probing",
                    null));

                // Check if cache already exists and is valid.
                var existingSnapshot = await TryGetMediainfoCacheSnapshotAsync(channelId, cancellationToken).ConfigureAwait(false);
                if (existingSnapshot != null)
                {
                    // The live file stays, but rules may have changed since it was written:
                    // preserve its combination and seed every rule-reachable combination that
                    // is still missing from the per-profile store.
                    if (!string.IsNullOrWhiteSpace(existingSnapshot.CacheFilePath))
                    {
                        MirrorToProfileStore(channelId, existingSnapshot.ProfileName, existingSnapshot.CacheFilePath, overwrite: false);
                    }

                    await MirrorRuleProfileVariantsAsync(config, channelId, null, existingSnapshot.ProfileName, cancellationToken).ConfigureAwait(false);

                    lock (lockObj)
                    {
                        alreadyCached++;
                    }

                    progress?.Report(new CacheWarmupProgress(position, totalChannels, channelName, channelId, "skipped", "Already cached"));
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
                var probed = await TryProbeAndWriteCacheAsync(channelId, streamUrl, effectiveProfile, cancellationToken).ConfigureAwait(false);

                if (!probed)
                {
                    // Fallback: write a synthetic cache file from the EFFECTIVE profile snapshot
                    // (the per-channel rule result), not the global one.
                    var profileSnapshot = await _profileContainerResolver.ResolveProfileSnapshotAsync(config, effectiveProfile, cancellationToken).ConfigureAwait(false);
                    await TryWriteMediaInfoCacheAsync(channelId, streamUrl, profileSnapshot, cancellationToken).ConfigureAwait(false);
                }

                // Pre-warm the per-profile store for every OTHER profile that a configured rule
                // can resolve to, so a rule-driven first start finds its combination ready:
                // transcode profiles get deterministic synthetic content; pass-through-like
                // profiles reuse the (probed) source data of the file just written.
                await MirrorRuleProfileVariantsAsync(config, channelId, streamUrl, effectiveProfile, cancellationToken).ConfigureAwait(false);

                lock (lockObj)
                {
                    warmed++;
                }

                progress?.Report(new CacheWarmupProgress(
                    position,
                    totalChannels,
                    channelName,
                    channelId,
                    "cached",
                    probed ? "FFprobe" : "Synthetic"));
            }
            catch (Exception ex)
            {
                lock (lockObj)
                {
                    failed++;
                    errors.Add($"Channel {channel.Id}: {ex.Message}");
                }

                progress?.Report(new CacheWarmupProgress(
                    position,
                    totalChannels,
                    channel.Name ?? channel.Id ?? "Unknown",
                    channel.Id ?? string.Empty,
                    "failed",
                    ex.Message));
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
    private async Task<bool> TryProbeAndWriteCacheAsync(string channelId, string streamUrl, string? profileName, CancellationToken cancellationToken)
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

            // A probe that yields neither a video nor an audio stream (e.g. the analyze
            // window elapsed before the first keyframe) is useless for Direct Play
            // negotiation. Persisting it would poison the cache until an admin manually
            // invalidates it, so treat it as a failed probe — the caller then falls back
            // to the synthetic profile-based cache content, which always declares both.
            if (filteredStreams.Count == 0)
            {
                _logger.LogWarning("FFprobe found no usable video or audio streams for channel {ChannelId}, falling back to synthetic cache.", channelId);
                return false;
            }

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

            // Probed data is authoritative for this (channel, profile) combination — overwrite
            // any previous (possibly synthetic) store entry.
            MirrorToProfileStore(channelId, profileName, cacheFilePath, overwrite: true);

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
    public async Task<int> InvalidateAllCachesAsync()
    {
        var cachePath = _cachePathResolver();
        if (string.IsNullOrWhiteSpace(cachePath))
        {
            return 0;
        }

        var mediaInfoDir = Path.Combine(cachePath, "mediainfo");
        if (!Directory.Exists(mediaInfoDir))
        {
            return 0;
        }

        // cache/mediainfo is Jellyfin's SHARED probe cache — M3U, HDHomeRun and every other
        // provider keep their entries in the same folder. Deleting *.json wholesale cost each of
        // them a multi-second re-probe on their next tune, so only this plugin's own entries are
        // removed: the file names derived from the TVHeadend channel list, plus the plugin-owned
        // "profiles" sub-directory.
        var files = new List<string>();

        foreach (var fileName in await BuildOwnCacheFileNamesAsync().ConfigureAwait(false))
        {
            var candidate = Path.Combine(mediaInfoDir, fileName);
            if (File.Exists(candidate))
            {
                files.Add(candidate);
            }
        }

        var profileStoreDir = Path.Combine(mediaInfoDir, "profiles");
        if (Directory.Exists(profileStoreDir))
        {
            files.AddRange(Directory.GetFiles(profileStoreDir, "*.json"));
        }

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

        _logger.LogInformation("Invalidated this plugin's mediainfo caches: {Deleted} files deleted.", deleted);
        MetricService.CacheInvalidationCount.Add(deleted);
        Interlocked.Add(ref _cacheInvalidations, deleted);
        return deleted;
    }

    /// <summary>
    /// Resolves the mediainfo cache file names this plugin owns, one per TVHeadend channel.
    /// </summary>
    /// <returns>The owned file names, or an empty set when the channel list is unavailable.</returns>
    private async Task<IReadOnlyCollection<string>> BuildOwnCacheFileNamesAsync()
    {
        if (_guideService == null)
        {
            return Array.Empty<string>();
        }

        try
        {
            var channels = await _guideService.GetChannelsAsync(CancellationToken.None).ConfigureAwait(false);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var channel in channels)
            {
                if (string.IsNullOrWhiteSpace(channel.Id))
                {
                    continue;
                }

                // Jellyfin keys the cache by media-source id; the plugin writes both the
                // channel-scoped and the source-less variant, so both must be cleared.
                names.Add(BuildChannelCacheFileName(channel.Id, channel.Id));
                names.Add(BuildChannelCacheFileName(channel.Id, null));
            }

            return names;
        }
        catch (Exception ex)
        {
            // Without the channel list the safe action is to clear nothing from the shared
            // directory rather than risk deleting another provider's entries.
            _logger.LogWarning(ex, "Could not resolve the TVHeadend channel list; skipping mediainfo cache invalidation.");
            return Array.Empty<string>();
        }
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
            Interlocked.Increment(ref _cacheInvalidations);
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
