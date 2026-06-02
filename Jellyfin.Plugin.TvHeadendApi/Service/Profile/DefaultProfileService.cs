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

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultProfileService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="tvheadendApiClient">TVHeadend API client.</param>
    /// <param name="tvheadendUrlBuilder">TVHeadend URL builder.</param>
    public DefaultProfileService(
        ILogger<DefaultProfileService> logger,
        IApiClient tvheadendApiClient,
        IUrlBuilder tvheadendUrlBuilder)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tvheadendApiClient = tvheadendApiClient ?? throw new ArgumentNullException(nameof(tvheadendApiClient));
        _tvheadendUrlBuilder = tvheadendUrlBuilder ?? throw new ArgumentNullException(nameof(tvheadendUrlBuilder));
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

            // 2/3. Ensure the codec profiles. We transcode BOTH streams or copy BOTH — never mix, because
            // TVHeadend's transcode pipeline produces an empty stream for copy-one/transcode-other.
            var videoRef = "copy";
            var audioRef = "copy";

            if (videoEncoder != null && audioEncoder != null)
            {
                var videoConf = BuildVideoCodecConf(VideoCodecProfileName, videoEncoder);
                var videoOk = await EnsureCodecProfileAsync(httpClient, baseUrl, webRoot, VideoCodecProfileName, videoEncoder.CreateClass, videoEncoder.NodeClass, videoConf, cancellationToken).ConfigureAwait(false);
                var audioConf = BuildAacCodecConf(AudioCodecProfileName);
                var audioOk = await EnsureCodecProfileAsync(httpClient, baseUrl, webRoot, AudioCodecProfileName, audioEncoder.CreateClass, audioEncoder.NodeClass, audioConf, cancellationToken).ConfigureAwait(false);

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
            _logger.LogError(ex, "Failed to connect to TVHeadend.");
            return new ProfileDetectionResult
            {
                Success = false,
                Message = $"Cannot connect to TVHeadend: {ex.Message}. Check your connection and authentication settings."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating profiles.");
            return new ProfileDetectionResult { Success = false, Message = $"Unexpected error: {ex.Message}" };
        }
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
    /// recreates it (delete + create) when an existing profile uses a different encoder class.
    /// </summary>
    private async Task<bool> EnsureCodecProfileAsync(HttpClient httpClient, string baseUrl, string webRoot, string name, string createClass, string expectedNodeClass, JsonObject conf, CancellationToken cancellationToken)
    {
        try
        {
            var existing = await ProfileMappingHelper.FindCodecProfileEntryByReferenceAsync(
                _tvheadendApiClient, httpClient, baseUrl, webRoot, name, cancellationToken).ConfigureAwait(false);

            if (existing != null)
            {
                var existingClass = await LoadNodeClassAsync(httpClient, baseUrl, webRoot, existing.Key, cancellationToken).ConfigureAwait(false);
                if (string.Equals(existingClass, expectedNodeClass, StringComparison.OrdinalIgnoreCase))
                {
                    var node = (JsonObject)conf.DeepClone();
                    node["uuid"] = existing.Key;
                    return await SaveNodeAsync(httpClient, baseUrl, webRoot, node, cancellationToken).ConfigureAwait(false);
                }

                _logger.LogInformation(
                    "Codec profile '{Name}' uses class '{Existing}' but '{Desired}' is required; recreating.",
                    name,
                    existingClass,
                    expectedNodeClass);
                await DeleteNodeAsync(httpClient, baseUrl, webRoot, existing.Key, cancellationToken).ConfigureAwait(false);
            }

            return await CreateCodecProfileAsync(httpClient, baseUrl, webRoot, createClass, conf, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure codec profile '{Name}'.", name);
            return false;
        }
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

    private async Task<string?> LoadNodeClassAsync(HttpClient httpClient, string baseUrl, string webRoot, string uuid, CancellationToken cancellationToken)
    {
        try
        {
            var loadUrl = $"{baseUrl}{webRoot}api/idnode/load?uuid={Uri.EscapeDataString(uuid)}";
            var body = await _tvheadendApiClient.GetStringAsync(httpClient, loadUrl, cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("entries", out var entries)
                && entries.ValueKind == JsonValueKind.Array
                && entries.GetArrayLength() > 0)
            {
                var first = entries[0];
                if (first.TryGetProperty("class", out var cls) && cls.ValueKind == JsonValueKind.String)
                {
                    return cls.GetString();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load class for node {Uuid}.", uuid);
        }

        return null;
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

    private async Task DeleteNodeAsync(HttpClient httpClient, string baseUrl, string webRoot, string uuid, CancellationToken cancellationToken)
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
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete node {Uuid}.", uuid);
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
}
