using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Common;
using Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Profile;

/// <summary>
/// Creates and maintains the plugin-managed TVHeadend streaming profile ("jellyfin") so that
/// every channel is normalized to a single, deterministic output format (H.264 video + AAC audio,
/// MPEG-TS container). Because the output codecs are fixed, the plugin can pre-create a matching
/// mediainfo cache and Jellyfin can skip its expensive live-stream probe — enabling fast playback.
/// <para>
/// The profile transcodes every video and audio stream to the target codecs. The video encoder is
/// auto-detected from the backend's actual capabilities (libx264, VAAPI, QuickSync, NVENC, V4L2, …)
/// so the profile works on unknown public setups, not just one specific machine.
/// </para>
/// <para>
/// Note: TVHeadend's <c>profile-transcode</c> cannot mix a copied stream with a transcoded stream in
/// the same profile (copy-video + transcode-audio yields an empty stream on many builds). The plugin
/// therefore always transcodes BOTH video and audio, or — when an encoder is missing — copies BOTH
/// (a passthrough that cannot normalize), but never mixes the two.
/// </para>
/// </summary>
internal sealed class DefaultProfileService : IDefaultProfileService
{
    /// <summary>The plugin-managed streaming profile name.</summary>
    internal const string ManagedProfileName = "jellyfin";

    private const string VideoCodecProfileName = "jellyfin-h264";
    private const string AudioCodecProfileName = "jellyfin-aac";

    /// <summary>TVHeadend container enum value for MPEG-TS (av-lib) — the reliable live-TV container.</summary>
    private const int ContainerMpegTs = 2;

    /// <summary>
    /// All source video codecs are transcoded to H.264 so the output codec is always identical
    /// (deterministic cache). H.264 is included intentionally — TVHeadend cannot copy one stream while
    /// transcoding another in the same profile, so everything is transcoded. Values must match
    /// TVHeadend's <c>src_vcodec</c> enum keys.
    /// </summary>
    private static readonly string[] TranscodeSourceVideoCodecs =
    {
        "MPEG2VIDEO",
        "H264",
        "VP8",
        "HEVC",
        "VP9",
        "THEORA"
    };

    /// <summary>
    /// All source audio codecs are transcoded to AAC for the same reason (no copy/transcode mixing).
    /// Values must match TVHeadend's <c>src_acodec</c> enum keys.
    /// </summary>
    private static readonly string[] TranscodeSourceAudioCodecs =
    {
        "MPEG2AUDIO",
        "AC3",
        "AAC",
        "MP4A",
        "EAC3",
        "VORBIS",
        "OPUS",
        "AC-4"
    };

    private readonly ILogger<DefaultProfileService> _logger;
    private readonly IApiClient _tvheadendApiClient;
    private readonly IUrlBuilder _tvheadendUrlBuilder;
    private readonly IProfileContainerResolver? _profileContainerResolver;
    private readonly IProfileDiscoveryService? _profileDiscoveryService;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultProfileService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="tvheadendApiClient">TVHeadend API client.</param>
    /// <param name="tvheadendUrlBuilder">TVHeadend URL builder.</param>
    /// <param name="profileContainerResolver">
    /// Optional profile snapshot resolver whose in-memory cache is invalidated after the managed
    /// profile is created or rewritten, so playback never resolves a stale pre-change snapshot.
    /// </param>
    /// <param name="profileDiscoveryService">
    /// Optional profile discovery service whose cached profile-name list is invalidated after the
    /// managed profile is created, so validation immediately sees the new profile.
    /// </param>
    public DefaultProfileService(
        ILogger<DefaultProfileService> logger,
        IApiClient tvheadendApiClient,
        IUrlBuilder tvheadendUrlBuilder,
        IProfileContainerResolver? profileContainerResolver = null,
        IProfileDiscoveryService? profileDiscoveryService = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tvheadendApiClient = tvheadendApiClient ?? throw new ArgumentNullException(nameof(tvheadendApiClient));
        _tvheadendUrlBuilder = tvheadendUrlBuilder ?? throw new ArgumentNullException(nameof(tvheadendUrlBuilder));
        _profileContainerResolver = profileContainerResolver;
        _profileDiscoveryService = profileDiscoveryService;
    }

    /// <summary>
    /// Outcome of the post-creation transcode verification.
    /// </summary>
    private enum ProfileVerificationOutcome
    {
        /// <summary>The test stream delivered real data — the profile works.</summary>
        Verified,

        /// <summary>The test stream delivered no (or too little) data — the encoder is non-functional.</summary>
        NoData,

        /// <summary>Verification could not run (no channels, no streaming permission, unexpected error).</summary>
        Skipped,
    }

    /// <inheritdoc />
    public async Task<ProfileDetectionResult> CreateProfileAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = _tvheadendApiClient.GetCurrentConfiguration();
            if (config == null)
            {
                return new ProfileDetectionResult { Success = false, Message = "Plugin configuration is not available." };
            }

            using var httpClient = _tvheadendApiClient.CreateApiHttpClient(config);
            var baseUrl = _tvheadendUrlBuilder.GetBaseUrl(config);
            var webRoot = _tvheadendUrlBuilder.GetWebRoot(config);

            var createdParts = new List<string>();

            // 1. Detect the backend's encoder capabilities (this is what makes the profile portable).
            var capabilities = await DetectCodecCapabilitiesAsync(httpClient, baseUrl, webRoot, cancellationToken).ConfigureAwait(false);
            var videoEncoder = SelectBestVideoEncoder(capabilities);
            var audioEncoder = capabilities.FirstOrDefault(c => string.Equals(c.CreateClass, "aac", StringComparison.OrdinalIgnoreCase));
            var encoderNamesByNodeClass = BuildEncoderNameLookup(capabilities);

            // 2/3. Ensure the codec profiles. We transcode BOTH streams or copy BOTH — never mix, because
            // TVHeadend's transcode pipeline produces an empty stream for copy-one/transcode-other.
            var videoRef = "copy";
            var audioRef = "copy";

            if (videoEncoder != null && audioEncoder != null)
            {
                var videoConf = BuildVideoCodecConf(VideoCodecProfileName, videoEncoder);
                var videoOk = await EnsureCodecProfileAsync(httpClient, baseUrl, webRoot, VideoCodecProfileName, videoEncoder.CreateClass, videoEncoder.NodeClass, videoConf, encoderNamesByNodeClass, cancellationToken).ConfigureAwait(false);
                var audioConf = BuildAacCodecConf(AudioCodecProfileName);
                var audioOk = await EnsureCodecProfileAsync(httpClient, baseUrl, webRoot, AudioCodecProfileName, audioEncoder.CreateClass, audioEncoder.NodeClass, audioConf, encoderNamesByNodeClass, cancellationToken).ConfigureAwait(false);

                if (videoOk && audioOk)
                {
                    videoRef = VideoCodecProfileName;
                    audioRef = AudioCodecProfileName;
                    createdParts.Add($"video codec profile '{VideoCodecProfileName}' ({videoEncoder.Label})");
                    createdParts.Add($"audio codec profile '{AudioCodecProfileName}' (AAC)");
                }
                else
                {
                    _logger.LogWarning("Codec profile creation failed; using passthrough (streams will not be normalized to H.264/AAC).");
                    createdParts.Add("WARNING: codec profile creation failed — using passthrough (streams not normalized)");
                }
            }
            else
            {
                var missing = videoEncoder == null ? "H.264 video encoder" : "AAC audio encoder";
                _logger.LogWarning("TVHeadend has no usable {Missing}; using passthrough (streams cannot be normalized to H.264/AAC).", missing);
                createdParts.Add($"WARNING: TVHeadend has no {missing} — using passthrough; channels are NOT normalized to H.264/AAC");
            }

            // 4. Ensure the streaming profile itself, linked to the codec profiles with smart-copy filters.
            var streamOk = await EnsureStreamingProfileAsync(httpClient, baseUrl, webRoot, videoRef, audioRef, cancellationToken).ConfigureAwait(false);

            // TVHeadend state may have changed (codec and/or streaming profiles were created, rewritten
            // or deleted) — drop the in-memory profile caches so subsequent lookups (including the
            // MediaInfo cache rebuild) observe the new state instead of a stale pre-change snapshot.
            InvalidateProfileCaches();

            if (!streamOk)
            {
                return new ProfileDetectionResult
                {
                    Success = false,
                    Message = "Codec profiles were processed but creating/updating the streaming profile failed. "
                        + "Ensure the TVHeadend user has admin privileges. Details: " + string.Join(", ", createdParts)
                };
            }

            createdParts.Add($"streaming profile '{ManagedProfileName}' (transcode → H.264/AAC/MPEG-TS)");

            // 5. Self-verification: an encoder listed by api/codec/list can still be non-functional
            // at runtime (e.g. a VAAPI device that is visible but unusable — real TVHeadend setups
            // hit this). Pull a short test stream through the fresh profile; when it delivers no
            // data and a software fallback exists, rebuild the video codec profile on libx264 and
            // verify again, so the managed profile works out of the box on any backend.
            if (videoRef == VideoCodecProfileName && videoEncoder != null)
            {
                var verification = await VerifyProfileDeliversDataAsync(httpClient, baseUrl, webRoot, cancellationToken).ConfigureAwait(false);
                if (verification == ProfileVerificationOutcome.NoData)
                {
                    var software = FindSoftwareH264(capabilities);
                    if (software != null && !string.Equals(software.CreateClass, videoEncoder.CreateClass, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning(
                            "Transcode verification produced no stream data with encoder '{Encoder}' — falling back to software libx264.",
                            videoEncoder.Label);
                        var fallbackConf = BuildVideoCodecConf(VideoCodecProfileName, software);
                        var fallbackOk = await EnsureCodecProfileAsync(httpClient, baseUrl, webRoot, VideoCodecProfileName, software.CreateClass, software.NodeClass, fallbackConf, encoderNamesByNodeClass, cancellationToken).ConfigureAwait(false);
                        InvalidateProfileCaches();
                        var reverified = fallbackOk
                            ? await VerifyProfileDeliversDataAsync(httpClient, baseUrl, webRoot, cancellationToken).ConfigureAwait(false)
                            : ProfileVerificationOutcome.NoData;
                        createdParts.Add(reverified == ProfileVerificationOutcome.Verified
                            ? $"NOTE: encoder '{videoEncoder.Label}' produced no stream data — automatically fell back to software libx264 (verified working)"
                            : "WARNING: the transcode profile produced no stream data even after the libx264 fallback — check TVHeadend's transcoding support");
                    }
                    else
                    {
                        createdParts.Add("WARNING: the transcode profile produced no stream data in verification — check TVHeadend's transcoding support (encoders/drivers)");
                    }
                }
                else if (verification == ProfileVerificationOutcome.Verified)
                {
                    createdParts.Add("transcode verified (test stream delivered data)");
                }
            }

            _logger.LogInformation("Profile setup complete: {Parts}", string.Join("; ", createdParts));

            return new ProfileDetectionResult
            {
                Success = true,
                ProfileName = ManagedProfileName,
                ProfileClass = "profile-transcode",
                IsTranscodeProfile = true,
                Container = "mpegts",
                VideoCodec = "h264",
                AudioCodec = "aac",
                VideoBitrate = 0,
                AudioBitrate = 0,
                AudioChannels = 0,
                VideoHeight = 0,
                VideoIsInterlaced = false,
                VideoCodecProfile = videoRef,
                AudioCodecProfile = audioRef,
                Message = "Configured: " + string.Join(", ", createdParts)
                    + $". The plugin's streaming profile has been set to '{ManagedProfileName}'."
            };
        }
        catch (HttpRequestException ex)
        {
            // Profiles may have been partially rewritten before the failure — invalidate anyway.
            InvalidateProfileCaches();
            _logger.LogError(ex, "Failed to connect to TVHeadend.");
            return new ProfileDetectionResult
            {
                Success = false,
                Message = $"Cannot connect to TVHeadend: {ex.Message}. Check your connection and authentication settings."
            };
        }
        catch (Exception ex)
        {
            // Profiles may have been partially rewritten before the failure — invalidate anyway.
            InvalidateProfileCaches();
            _logger.LogError(ex, "Error creating profiles.");
            return new ProfileDetectionResult { Success = false, Message = $"Unexpected error: {ex.Message}" };
        }
    }

    /// <summary>
    /// Drops the in-memory profile caches (resolved profile snapshots and the discovered-profile
    /// list) after the managed profile has potentially changed in TVHeadend. Without this, stale
    /// snapshots are served until their TTL expires and the MediaInfo cache is rebuilt with
    /// outdated container/codec metadata — pushing clients off the Direct Play path.
    /// </summary>
    private void InvalidateProfileCaches()
    {
        _profileContainerResolver?.InvalidateCache();
        _profileDiscoveryService?.InvalidateCache();
    }

    /// <summary>
    /// Queries <c>api/codec/list</c> and returns the available encoder codec-profile classes together
    /// with their declared property ids and (for hardware encoders) the first detected device path.
    /// </summary>
    private async Task<List<EncoderCapability>> DetectCodecCapabilitiesAsync(HttpClient httpClient, string baseUrl, string webRoot, CancellationToken cancellationToken)
    {
        var result = new List<EncoderCapability>();
        try
        {
            var url = $"{baseUrl}{webRoot}api/codec/list";
            var json = await _tvheadendApiClient.GetStringAsync(httpClient, url, cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                var className = entry.TryGetProperty("class", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(className))
                {
                    continue;
                }

                // codec_profile/create expects the ffmpeg encoder name (e.g. "h264_vaapi"), which is the
                // "name" field — NOT the stripped codec-profile class ("vaapi_h264"). They coincide for
                // libx264/aac but differ for hardware encoders. Fall back to the stripped class if absent.
                var encoderName = entry.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : null;
                const string prefix = "codec_profile_";
                var strippedClass = className.StartsWith(prefix, StringComparison.Ordinal) ? className[prefix.Length..] : className;
                var createClass = string.IsNullOrWhiteSpace(encoderName) ? strippedClass : encoderName;

                var propIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string? device = null;
                if (entry.TryGetProperty("props", out var props) && props.ValueKind == JsonValueKind.Array)
                {
                    foreach (var prop in props.EnumerateArray())
                    {
                        var id = prop.TryGetProperty("id", out var pid) && pid.ValueKind == JsonValueKind.String ? pid.GetString() : null;
                        if (string.IsNullOrWhiteSpace(id))
                        {
                            continue;
                        }

                        propIds.Add(id);

                        if (string.Equals(id, "device", StringComparison.OrdinalIgnoreCase)
                            && prop.TryGetProperty("enum", out var en) && en.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var opt in en.EnumerateArray())
                            {
                                if (opt.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String)
                                {
                                    device = key.GetString();
                                    break;
                                }
                            }
                        }
                    }
                }

                result.Add(new EncoderCapability(createClass, className, propIds, device));
            }
        }
        catch (JsonException ex)
        {
            // A malformed codec list is non-fatal: continue with no detected encoders (copy fallback).
            // Connection failures (HttpRequestException) deliberately propagate to the caller.
            _logger.LogWarning(ex, "Could not parse TVHeadend codec capabilities.");
        }

        return result;
    }

    /// <summary>
    /// Selects the best available H.264 video encoder. A hardware encoder with a detected render
    /// device (VAAPI, QuickSync, NVENC, V4L2, …) is preferred to offload the CPU; software libx264 is
    /// the next choice (always works, no device needed); a hardware encoder without a detected device
    /// is only a last resort. H.265/HEVC encoders are ignored.
    /// </summary>
    private VideoEncoderChoice? SelectBestVideoEncoder(IReadOnlyList<EncoderCapability> capabilities)
    {
        VideoEncoderChoice? best = null;
        foreach (var cap in capabilities)
        {
            var cls = cap.CreateClass.ToLowerInvariant();
            if (!cls.Contains("264", StringComparison.Ordinal))
            {
                continue;
            }

            var isSoftware = cls.Contains("x264", StringComparison.Ordinal);
            var (hwOrder, label) = cls switch
            {
                _ when cls.Contains("vaapi", StringComparison.Ordinal) => (0, "VAAPI hardware"),
                _ when cls.Contains("qsv", StringComparison.Ordinal) => (1, "Intel QuickSync hardware"),
                _ when cls.Contains("nvenc", StringComparison.Ordinal) => (2, "NVIDIA NVENC hardware"),
                _ when cls.Contains("v4l2", StringComparison.Ordinal) => (3, "V4L2 M2M hardware"),
                _ when cls.Contains("videotoolbox", StringComparison.Ordinal) => (4, "Apple VideoToolbox hardware"),
                _ when cls.Contains("mediacodec", StringComparison.Ordinal) => (5, "Android MediaCodec hardware"),
                _ when isSoftware => (-1, "software libx264"),
                _ => (98, cap.CreateClass)
            };

            // Rank: hardware-with-device (10–15) → software libx264 (20) → hardware-without-device (30–35) → other (40).
            int rank;
            if (hwOrder >= 0 && hwOrder < 98 && !string.IsNullOrWhiteSpace(cap.Device))
            {
                rank = 10 + hwOrder;
            }
            else if (isSoftware)
            {
                rank = 20;
            }
            else if (hwOrder < 98)
            {
                rank = 30 + hwOrder;
            }
            else
            {
                rank = 40;
            }

            if (best == null || rank < best.Rank)
            {
                best = new VideoEncoderChoice(cap.CreateClass, cap.NodeClass, label, cap.Device, cap.PropIds, rank);
            }
        }

        if (best != null)
        {
            _logger.LogInformation(
                "Selected TVHeadend video encoder '{Class}' ({Label}){Device}.",
                best.CreateClass,
                best.Label,
                string.IsNullOrWhiteSpace(best.Device) ? string.Empty : $" on device {best.Device}");
        }

        return best;
    }

    /// <summary>
    /// Returns the software libx264 encoder from the detected capabilities, or <c>null</c>.
    /// </summary>
    private static VideoEncoderChoice? FindSoftwareH264(IReadOnlyList<EncoderCapability> capabilities)
    {
        var cap = capabilities.FirstOrDefault(c =>
            c.CreateClass.Contains("x264", StringComparison.OrdinalIgnoreCase));
        return cap == null
            ? null
            : new VideoEncoderChoice(cap.CreateClass, cap.NodeClass, "software libx264", cap.Device, cap.PropIds, 20);
    }

    /// <summary>
    /// Verifies the managed profile actually delivers stream data by reading a short test stream
    /// from the first mapped channel. Skips (treats as verified) when no channel exists yet or the
    /// configured user lacks streaming permission — verification must never produce false alarms.
    /// </summary>
    private async Task<ProfileVerificationOutcome> VerifyProfileDeliversDataAsync(HttpClient httpClient, string baseUrl, string webRoot, CancellationToken cancellationToken)
    {
        const int RequiredBytes = 32 * 1024;
        try
        {
            var gridUrl = $"{baseUrl}{webRoot}api/channel/grid?limit=1";
            var gridJson = await _tvheadendApiClient.GetStringAsync(httpClient, gridUrl, cancellationToken).ConfigureAwait(false);
            using var gridDoc = JsonDocument.Parse(gridJson);
            var entries = gridDoc.RootElement.TryGetProperty("entries", out var e) ? e : default;
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() == 0)
            {
                _logger.LogInformation("Transcode verification skipped: no mapped channels available yet.");
                return ProfileVerificationOutcome.Skipped;
            }

            var channelUuid = entries[0].GetProperty("uuid").GetString();
            var streamUrl = $"{baseUrl}{webRoot}stream/channel/{channelUuid}?profile={ManagedProfileName}";

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var request = new HttpRequestMessage(HttpMethod.Get, streamUrl);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogInformation("Transcode verification skipped: HTTP {Status} from the stream endpoint (no streaming permission?).", (int)response.StatusCode);
                return ProfileVerificationOutcome.Skipped;
            }

            if (!response.IsSuccessStatusCode)
            {
                return ProfileVerificationOutcome.NoData;
            }

            var body = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                var buffer = new byte[8192];
                var total = 0;
                while (total < RequiredBytes)
                {
                    var read = await body.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                }

                _logger.LogInformation("Transcode verification read {Bytes} bytes through profile '{Profile}'.", total, ManagedProfileName);
                return total >= RequiredBytes ? ProfileVerificationOutcome.Verified : ProfileVerificationOutcome.NoData;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Verification window elapsed without enough data — the 0-byte failure mode.
            return ProfileVerificationOutcome.NoData;
        }
        catch (Exception ex)
        {
            // Never let verification break profile creation — skip on unexpected errors.
            _logger.LogDebug(ex, "Transcode verification skipped due to an unexpected error.");
            return ProfileVerificationOutcome.Skipped;
        }
    }

    /// <summary>
    /// Builds a robust video codec-profile configuration. Only properties that the chosen encoder class
    /// actually declares are included, so the same logic produces valid configs for libx264, VAAPI, QSV,
    /// NVENC, etc. Profile/level/pixel-format are left on "auto" and no bitrate cap is applied.
    /// </summary>
    private static JsonObject BuildVideoCodecConf(string name, VideoEncoderChoice encoder)
    {
        // Superset of desired settings; filtered to the encoder's actual properties below.
        var desired = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
        {
            ["deinterlace"] = true,   // Deinterlace interlaced broadcast sources for clean web playback.
            ["profile"] = -99,        // -99 = auto (the class default); avoids the invalid hard-coded value.
            ["level"] = -100,         // -100 = skip/auto; never constrain the H.264 level.
            ["pix_fmt"] = -1,         // -1 = auto (yuv420p for H.264).
            ["bit_rate"] = 0,         // 0 = quality-based, no fixed bitrate.
            ["max_bit_rate"] = 0,     // 0 = no cap (the previous 1 Mbps cap wrecked HD quality).
            ["crf"] = 0,
            ["quality"] = 0,
            ["preset"] = "faster",
            ["tune"] = "zerolatency",
            ["hwaccel_details"] = 0,
            ["platform"] = 0,
        };

        var conf = new JsonObject
        {
            ["name"] = name,
            ["description"] = "Managed by the Jellyfin TVHeadend plugin — do not rename.",
        };

        foreach (var kvp in desired)
        {
            if (encoder.PropIds.Contains(kvp.Key))
            {
                conf[kvp.Key] = kvp.Value;
            }
        }

        if (encoder.PropIds.Contains("device") && !string.IsNullOrWhiteSpace(encoder.Device))
        {
            conf["device"] = encoder.Device;
        }

        return conf;
    }

    /// <summary>
    /// Builds the AAC audio codec-profile configuration (single stereo track).
    /// <para>
    /// <c>profile = 1</c> forces <strong>AAC-LC</strong> (Low Complexity). This is critical: the
    /// encoder otherwise emits AAC Main (profile 0), which browser MSE decoders (Chrome/Firefox)
    /// cannot decode — playback dies with <c>PIPELINE_ERROR_DECODE</c> on the audio packets. AAC-LC
    /// is the universally supported profile, so this is also the safe default for every backend.
    /// </para>
    /// </summary>
    private static JsonObject BuildAacCodecConf(string name)
    {
        return new JsonObject
        {
            ["name"] = name,
            ["description"] = "Managed by the Jellyfin TVHeadend plugin — do not rename.",
            ["tracks"] = 1,
            ["profile"] = 1, // AAC-LC (browsers cannot decode AAC Main = 0).
            // Explicit CBR bitrate (kb/s). With bit_rate=0 (auto/VBR) the encoder emits oversized AAC
            // frames (> the 1536-byte/2ch AAC maximum) → "muxer reported errors" and the stream restarts.
            // A fixed 160 kb/s keeps every frame well within the limit while preserving good quality.
            ["bit_rate"] = 160,
            ["sample_rate"] = 0,
            ["channel_layout"] = 0,
            ["coder"] = "twoloop",
        };
    }

    /// <summary>
    /// Ensures a codec profile with the given name exists and uses the desired encoder class and config.
    /// Creates it when missing, updates the config when it already uses the desired class, and
    /// recreates it when an existing profile uses a different encoder class. TVHeadend has no atomic
    /// replace, so the swap is delete-then-create: the existing configuration is captured first and
    /// restored when creating the replacement fails — a failed swap must never leave the previous
    /// (working) profile deleted.
    /// </summary>
    private async Task<bool> EnsureCodecProfileAsync(HttpClient httpClient, string baseUrl, string webRoot, string name, string createClass, string expectedNodeClass, JsonObject conf, IReadOnlyDictionary<string, string> encoderNamesByNodeClass, CancellationToken cancellationToken)
    {
        try
        {
            var existing = await ProfileMappingHelper.FindCodecProfileEntryByReferenceAsync(
                _tvheadendApiClient, httpClient, baseUrl, webRoot, name, cancellationToken).ConfigureAwait(false);

            if (existing == null)
            {
                return await CreateCodecProfileAsync(httpClient, baseUrl, webRoot, createClass, conf, cancellationToken).ConfigureAwait(false);
            }

            var existingState = await LoadCodecProfileNodeStateAsync(httpClient, baseUrl, webRoot, existing.Key, cancellationToken).ConfigureAwait(false);
            if (string.Equals(existingState.NodeClass, expectedNodeClass, StringComparison.OrdinalIgnoreCase))
            {
                var node = (JsonObject)conf.DeepClone();
                node["uuid"] = existing.Key;
                return await SaveNodeAsync(httpClient, baseUrl, webRoot, node, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Codec profile '{Name}' uses class '{Existing}' but '{Desired}' is required; recreating.",
                name,
                existingState.NodeClass,
                expectedNodeClass);

            // Abort when the delete fails so the existing profile stays untouched.
            if (!await DeleteNodeAsync(httpClient, baseUrl, webRoot, existing.Key, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning("Could not delete codec profile '{Name}'; keeping the existing profile untouched.", name);
                return false;
            }

            if (await CreateCodecProfileAsync(httpClient, baseUrl, webRoot, createClass, conf, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await RestoreCodecProfileAsync(httpClient, baseUrl, webRoot, name, existingState, encoderNamesByNodeClass, cancellationToken).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure codec profile '{Name}'.", name);
            return false;
        }
    }

    /// <summary>
    /// Restores a codec profile from the configuration captured before it was deleted, after
    /// creating its replacement failed. This keeps the previously working profile available instead
    /// of silently degrading the managed streaming profile to copy passthrough until an admin notices.
    /// </summary>
    private async Task RestoreCodecProfileAsync(
        HttpClient httpClient,
        string baseUrl,
        string webRoot,
        string name,
        CodecProfileNodeState previousState,
        IReadOnlyDictionary<string, string> encoderNamesByNodeClass,
        CancellationToken cancellationToken)
    {
        if (previousState.Conf == null || string.IsNullOrWhiteSpace(previousState.NodeClass))
        {
            _logger.LogError(
                "Replacement creation for codec profile '{Name}' failed and no configuration backup is available; the previous profile could not be restored.",
                name);
            return;
        }

        // codec_profile/create expects the ffmpeg encoder name, not the node class — map it back via
        // the detected capabilities. A missing entry means the previous encoder no longer exists on
        // the backend, so recreating the old profile could not work either.
        if (!encoderNamesByNodeClass.TryGetValue(previousState.NodeClass, out var previousCreateClass))
        {
            _logger.LogError(
                "Replacement creation for codec profile '{Name}' failed and its previous encoder class '{NodeClass}' is no longer available; the previous profile could not be restored.",
                name,
                previousState.NodeClass);
            return;
        }

        _logger.LogWarning(
            "Replacement creation for codec profile '{Name}' failed; restoring the previous profile (class '{NodeClass}').",
            name,
            previousState.NodeClass);

        var restoreConf = (JsonObject)previousState.Conf.DeepClone();

        // idnode/load may omit the name from the parameter list; the profile is referenced by name.
        restoreConf["name"] = name;

        if (!await CreateCodecProfileAsync(httpClient, baseUrl, webRoot, previousCreateClass, restoreConf, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogError("Restoring codec profile '{Name}' failed; the profile no longer exists in TVHeadend.", name);
        }
    }

    /// <summary>
    /// Maps codec-profile node classes (e.g. "codec_profile_vaapi_h264") to the ffmpeg encoder names
    /// (e.g. "h264_vaapi") that <c>codec_profile/create</c> expects, used to restore deleted profiles.
    /// </summary>
    private static Dictionary<string, string> BuildEncoderNameLookup(IEnumerable<EncoderCapability> capabilities)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var capability in capabilities)
        {
            lookup.TryAdd(capability.NodeClass, capability.CreateClass);
        }

        return lookup;
    }

    /// <summary>
    /// Creates or updates the "jellyfin" streaming profile (class <c>profile-transcode</c>) that
    /// transcodes all source codecs to the linked H.264/AAC codec profiles in an MPEG-TS container.
    /// </summary>
    private async Task<bool> EnsureStreamingProfileAsync(HttpClient httpClient, string baseUrl, string webRoot, string videoRef, string audioRef, CancellationToken cancellationToken)
    {
        try
        {
            var conf = new JsonObject
            {
                ["name"] = ManagedProfileName,
                ["enabled"] = true,
                ["default"] = false,
                ["comment"] = "Managed by the Jellyfin TVHeadend plugin",
                ["timeout"] = 1,
                ["timeout_start"] = 0,
                ["priority"] = 0,
                ["fpriority"] = 0,
                ["restart"] = false,
                ["contaccess"] = true,
                ["catimeout"] = 2000,
                ["swservice"] = true,
                ["svfilter"] = 0,
                ["container"] = ContainerMpegTs,

                // Rewrite the service id and NIT in TVHeadend itself. With sid == 0 TVHeadend's
                // libav muxer unconditionally passes "mpegts_flags=nit" to FFmpeg — a flag that
                // FFmpeg 6.x does not know — so every MPEG-TS transcode stream dies with
                // "Failed to write mpegts header" (0 bytes). sid=1 + rewrite_nit=true selects
                // the code path that never sets the broken flag; the rewritten values are
                // irrelevant to Jellyfin/FFmpeg clients.
                ["sid"] = 1,
                ["rewrite_pmt"] = false,
                ["rewrite_nit"] = true,
                ["pro_vcodec"] = videoRef,
                ["src_vcodec"] = ToJsonArray(TranscodeSourceVideoCodecs),
                ["pro_acodec"] = audioRef,
                ["src_acodec"] = ToJsonArray(TranscodeSourceAudioCodecs),
                ["pro_scodec"] = string.Empty,
                ["src_scodec"] = new JsonArray(),
            };

            var existing = await GetStreamingProfileByNameAsync(httpClient, baseUrl, webRoot, ManagedProfileName, cancellationToken).ConfigureAwait(false);
            if (existing != null)
            {
                conf["uuid"] = existing.Key;
                _logger.LogInformation("Updating existing streaming profile '{Name}' to the managed transcode configuration.", ManagedProfileName);
                return await SaveNodeAsync(httpClient, baseUrl, webRoot, conf, cancellationToken).ConfigureAwait(false);
            }

            var createUrl = $"{baseUrl}{webRoot}api/profile/create";
            _logger.LogInformation("Creating streaming profile '{Name}' in TVHeadend.", ManagedProfileName);
            using var response = await _tvheadendApiClient.PostFormAsync(
                httpClient,
                createUrl,
                new[]
                {
                    new KeyValuePair<string, string>("class", "profile-transcode"),
                    new KeyValuePair<string, string>("conf", conf.ToJsonString())
                },
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogError("Failed to create streaming profile (HTTP {Status}): {Body}", response.StatusCode, body);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure streaming profile '{Name}'.", ManagedProfileName);
            return false;
        }
    }

    private async Task<bool> CreateCodecProfileAsync(HttpClient httpClient, string baseUrl, string webRoot, string codecClass, JsonObject conf, CancellationToken cancellationToken)
    {
        var createUrl = $"{baseUrl}{webRoot}api/codec_profile/create";
        _logger.LogDebug("Codec profile creation: class={Class}, conf={Json}", codecClass, conf.ToJsonString());

        using var response = await _tvheadendApiClient.PostFormAsync(
            httpClient,
            createUrl,
            new[]
            {
                new KeyValuePair<string, string>("class", codecClass),
                new KeyValuePair<string, string>("conf", conf.ToJsonString())
            },
            cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Created codec profile (class={Class}).", codecClass);
            return true;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("Codec profile creation failed (class={Class}, HTTP {Status}): {Body}", codecClass, response.StatusCode, body);
        return false;
    }

    /// <summary>
    /// Loads the class and the current configuration of a codec-profile node in a single request.
    /// The configuration doubles as the restore backup for the recreate flow.
    /// </summary>
    private async Task<CodecProfileNodeState> LoadCodecProfileNodeStateAsync(HttpClient httpClient, string baseUrl, string webRoot, string uuid, CancellationToken cancellationToken)
    {
        try
        {
            var loadUrl = $"{baseUrl}{webRoot}api/idnode/load?uuid={Uri.EscapeDataString(uuid)}";
            var body = await _tvheadendApiClient.GetStringAsync(httpClient, loadUrl, cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("entries", out var entries)
                || entries.ValueKind != JsonValueKind.Array
                || entries.GetArrayLength() == 0)
            {
                return new CodecProfileNodeState(null, null);
            }

            var first = entries[0];
            var nodeClass = first.TryGetProperty("class", out var cls) && cls.ValueKind == JsonValueKind.String
                ? cls.GetString()
                : null;

            JsonObject? backup = null;
            if (first.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
            {
                var conf = new JsonObject();
                foreach (var param in parameters.EnumerateArray())
                {
                    var id = param.TryGetProperty("id", out var pid) && pid.ValueKind == JsonValueKind.String ? pid.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id)
                        || string.Equals(id, "uuid", StringComparison.OrdinalIgnoreCase)
                        || !param.TryGetProperty("value", out var value))
                    {
                        continue;
                    }

                    conf[id] = JsonNode.Parse(value.GetRawText());
                }

                if (conf.Count > 0)
                {
                    backup = conf;
                }
            }

            return new CodecProfileNodeState(nodeClass, backup);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load state for node {Uuid}.", uuid);
            return new CodecProfileNodeState(null, null);
        }
    }

    private async Task<bool> SaveNodeAsync(HttpClient httpClient, string baseUrl, string webRoot, JsonObject node, CancellationToken cancellationToken)
    {
        var saveUrl = $"{baseUrl}{webRoot}api/idnode/save";
        using var response = await _tvheadendApiClient.PostFormAsync(
            httpClient,
            saveUrl,
            new[] { new KeyValuePair<string, string>("node", node.ToJsonString()) },
            cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("idnode/save failed (HTTP {Status}): {Body}", response.StatusCode, body);
        return false;
    }

    private async Task<bool> DeleteNodeAsync(HttpClient httpClient, string baseUrl, string webRoot, string uuid, CancellationToken cancellationToken)
    {
        try
        {
            var deleteUrl = $"{baseUrl}{webRoot}api/idnode/delete";
            var uuidArray = new JsonArray { uuid };
            using var response = await _tvheadendApiClient.PostFormAsync(
                httpClient,
                deleteUrl,
                new[] { new KeyValuePair<string, string>("uuid", uuidArray.ToJsonString()) },
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("idnode/delete failed for {Uuid} (HTTP {Status}): {Body}", uuid, response.StatusCode, body);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete node {Uuid}.", uuid);
            return false;
        }
    }

    private async Task<ProfileListEntry?> GetStreamingProfileByNameAsync(HttpClient httpClient, string baseUrl, string webRoot, string profileName, CancellationToken cancellationToken)
    {
        try
        {
            var listUrl = $"{baseUrl}{webRoot}api/profile/list";
            var response = await _tvheadendApiClient.GetStringAsync(httpClient, listUrl, cancellationToken).ConfigureAwait(false);
            var list = JsonSerializer.Deserialize<ProfileListResponse>(response, JsonDefaults.Api);
            return list?.Entries?.FirstOrDefault(e => string.Equals(e.Val, profileName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve streaming profile '{ProfileName}'.", profileName);
            return null;
        }
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    /// <summary>
    /// An encoder detected on the backend. <c>CreateClass</c> is the ffmpeg encoder name passed to
    /// <c>codec_profile/create</c> (e.g. "h264_vaapi"); <c>NodeClass</c> is the codec-profile class
    /// (e.g. "codec_profile_vaapi_h264") used to compare against an existing profile.
    /// </summary>
    private sealed record EncoderCapability(string CreateClass, string NodeClass, HashSet<string> PropIds, string? Device);

    /// <summary>The selected H.264 video encoder and its associated metadata.</summary>
    private sealed record VideoEncoderChoice(string CreateClass, string NodeClass, string Label, string? Device, HashSet<string> PropIds, int Rank);

    /// <summary>
    /// Captured state of an existing codec-profile node: its class (compared against the desired
    /// encoder class) and its configuration (the restore backup for the recreate flow).
    /// </summary>
    private sealed record CodecProfileNodeState(string? NodeClass, JsonObject? Conf);
}
