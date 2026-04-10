using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Auth;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Auth;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Profile;

/// <summary>
/// Creates and links recommended TVHeadend codec and streaming profiles for Jellyfin playback.
/// </summary>
internal sealed class ProvisioningService : IProvisioningService
{
    private static readonly string[] DefaultSourceVideoCodecs =
    {
        "MPEG2VIDEO",
        "H264",
        "VP8",
        "HEVC",
        "VP9",
        "THEORA"
    };

    private static readonly string[] DefaultSourceAudioCodecs =
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<ProvisioningService> _logger;
    private readonly IApiClient _tvheadendApiClient;
    private readonly ITokenService _tokenService;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProvisioningService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="tvheadendApiClient">TVHeadend API client.</param>
    /// <param name="tokenService">Service that creates/refreshes auth tokens until valid.</param>
    public ProvisioningService(
        ILogger<ProvisioningService> logger,
        IApiClient tvheadendApiClient,
        ITokenService tokenService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tvheadendApiClient = tvheadendApiClient ?? throw new ArgumentNullException(nameof(tvheadendApiClient));
        _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
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

            using var httpClient = _tvheadendApiClient.BuildHttpClient(config);
            var baseUrl = _tvheadendApiClient.GetBaseUrl(config);
            var webRoot = _tvheadendApiClient.GetWebRoot(config);

            var createdParts = new List<string>();

            const string videoCodecProfileName = "jellyfin-h264";
            var videoCodecExists = await CodecProfileExistsAsync(httpClient, baseUrl, webRoot, videoCodecProfileName, cancellationToken).ConfigureAwait(false);
            if (!videoCodecExists)
            {
                _logger.LogInformation("Creating video codec profile '{Name}' in TVHeadend.", videoCodecProfileName);

                var videoConf = new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = videoCodecProfileName,
                    ["description"] = string.Empty,
                    ["deinterlace"] = true,
                    ["height"] = 0,
                    ["scaling_mode"] = 0,
                    ["hwaccel"] = false,
                    ["crf"] = 0,
                    ["bit_rate"] = 0,
                    ["max_bit_rate"] = 1000,
                    ["buff_factor"] = 3,
                    ["bit_rate_scale_factor"] = 0,
                    ["profile"] = 77,
                    ["pix_fmt"] = 46,
                    ["qmin"] = 0,
                    ["qmax"] = 0,
                    ["quality"] = 5,
                    ["level"] = 30,
                    ["preset"] = "faster",
                    ["tune"] = "zerolatency",
                    ["params"] = string.Empty,
                };

                var created = await CreateCodecProfileAsync(httpClient, baseUrl, webRoot, "libx264", videoConf, cancellationToken).ConfigureAwait(false);
                if (created)
                {
                    createdParts.Add("video codec profile 'jellyfin-h264' (H.264 libx264)");
                }
                else
                {
                    _logger.LogWarning("Could not create video codec profile '{Name}'. The streaming profile will fall back to default libx264 settings.", videoCodecProfileName);
                }
            }
            else
            {
                createdParts.Add("video codec profile 'jellyfin-h264' (already exists)");
            }

            const string videoCodecIntelProfileName = "jellyfin-h264-intel";
            var videoCodecIntelExists = await CodecProfileExistsAsync(httpClient, baseUrl, webRoot, videoCodecIntelProfileName, cancellationToken).ConfigureAwait(false);
            if (!videoCodecIntelExists)
            {
                _logger.LogInformation("Creating Intel QuickSync video codec profile '{Name}' in TVHeadend.", videoCodecIntelProfileName);

                var videoIntelConf = new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = videoCodecIntelProfileName,
                    ["description"] = string.Empty,
                    ["deinterlace"] = true,
                    ["height"] = 0,
                    ["scaling_mode"] = 0,
                    ["hwaccel"] = true,
                    ["hwaccel_details"] = 1,
                    ["hw_denoise"] = 0,
                    ["hw_sharpness"] = 44,
                    ["platform"] = 1,
                    ["device"] = "/dev/dri/renderD128",
                    ["low_power"] = false,
                    ["async_depth"] = 4,
                    ["desired_b_depth"] = 0,
                    ["b_reference"] = 0,
                    ["rc_mode"] = 0,
                    ["qp"] = 0,
                    ["bit_rate"] = 0,
                    ["max_bit_rate"] = 1000,
                    ["buff_factor"] = 3,
                    ["bit_rate_scale_factor"] = 0,
                    ["profile"] = 77,
                    ["pix_fmt"] = 46,
                    ["qmin"] = 0,
                    ["qmax"] = 0,
                    ["quality"] = 5,
                    ["level"] = 30,
                    ["ui"] = 255,
                    ["uilp"] = 255,
                };

                var created = await CreateCodecProfileAsync(httpClient, baseUrl, webRoot, "h264_qsv", videoIntelConf, cancellationToken).ConfigureAwait(false);
                if (created)
                {
                    createdParts.Add("video codec profile 'jellyfin-h264-intel' (H.264 Intel QuickSync)");
                }
                else
                {
                    _logger.LogWarning("Could not create Intel QuickSync video codec profile '{Name}'. Continuing without it.", videoCodecIntelProfileName);
                }
            }
            else
            {
                createdParts.Add("video codec profile 'jellyfin-h264-intel' (already exists)");
            }

            const string audioCodecProfileName = "jellyfin-aac";
            var audioCodecExists = await CodecProfileExistsAsync(httpClient, baseUrl, webRoot, audioCodecProfileName, cancellationToken).ConfigureAwait(false);
            if (!audioCodecExists)
            {
                _logger.LogInformation("Creating audio codec profile '{Name}' in TVHeadend.", audioCodecProfileName);

                var audioConf = new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = audioCodecProfileName,
                    ["description"] = string.Empty,
                    ["tracks"] = 1,
                    ["language1"] = string.Empty,
                    ["language2"] = string.Empty,
                    ["language3"] = string.Empty,
                    ["bit_rate"] = 0,
                    ["qscale"] = 0,
                    ["profile"] = 0,
                    ["sample_fmt"] = 8,
                    ["sample_rate"] = 48000,
                    ["channel_layout"] = 3,
                    ["coder"] = "twoloop",
                };

                var created = await CreateCodecProfileAsync(httpClient, baseUrl, webRoot, "aac", audioConf, cancellationToken).ConfigureAwait(false);
                if (created)
                {
                    createdParts.Add("audio codec profile 'jellyfin-aac' (AAC 128 kbps)");
                }
                else
                {
                    _logger.LogWarning("Could not create audio codec profile '{Name}'. The streaming profile will fall back to default AAC settings.", audioCodecProfileName);
                }
            }
            else
            {
                createdParts.Add("audio codec profile 'jellyfin-aac' (already exists)");
            }

            var listUrl = $"{baseUrl}{webRoot}api/profile/list";
            var listResponse = await _tvheadendApiClient.GetStringAsync(httpClient, listUrl, cancellationToken).ConfigureAwait(false);
            var profileList = JsonSerializer.Deserialize<ProfileListResponse>(listResponse, JsonOptions);

            var existingStreamProfile = profileList?.Entries?.FirstOrDefault(e =>
                string.Equals(e.Val, "jellyfin", StringComparison.OrdinalIgnoreCase));

            if (existingStreamProfile != null)
            {
                createdParts.Add("streaming profile 'jellyfin' (already exists)");
            }
            else
            {
                var createUrl = $"{baseUrl}{webRoot}api/profile/create";
                _logger.LogInformation("Creating streaming profile 'jellyfin' in TVHeadend at {Url}.", createUrl);

                var confNode = new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = "jellyfin",
                    ["enabled"] = true,
                    ["default"] = false,
                    ["comment"] = string.Empty,
                    ["timeout"] = 1,
                    ["timeout_start"] = 0,
                    ["priority"] = 0,
                    ["fpriority"] = 0,
                    ["restart"] = false,
                    ["contaccess"] = true,
                    ["catimeout"] = 2000,
                    ["swservice"] = true,
                    ["svfilter"] = 0,
                    ["container"] = 9,
                    ["pro_vcodec"] = "jellyfin-h264",
                    ["src_vcodec"] = new System.Text.Json.Nodes.JsonArray { "MPEG2VIDEO", "H264", "VP8", "HEVC", "VP9", "THEORA" },
                    ["pro_acodec"] = "jellyfin-aac",
                    ["src_acodec"] = new System.Text.Json.Nodes.JsonArray { "MPEG2AUDIO", "AC3", "AAC", "MP4A", "EAC3", "VORBIS", "OPUS", "AC-4" },
                    ["pro_scodec"] = string.Empty,
                    ["src_scodec"] = new System.Text.Json.Nodes.JsonArray(),
                };

                var jsonConf = confNode.ToJsonString();
                _logger.LogDebug("Streaming profile creation payload: class=profile-transcode, conf={Json}", jsonConf);

                using var response = await _tvheadendApiClient.PostFormAsync(
                    httpClient,
                    createUrl,
                    new[]
                    {
                        new KeyValuePair<string, string>("class", "profile-transcode"),
                        new KeyValuePair<string, string>("conf", jsonConf)
                    },
                    cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("First streaming profile attempt failed (HTTP {Status}). Retrying with minimal config.", response.StatusCode);

                    var minimalConf = new System.Text.Json.Nodes.JsonObject
                    {
                        ["enabled"] = true,
                        ["name"] = "jellyfin",
                        ["container"] = 9,
                        ["pro_vcodec"] = "jellyfin-h264",
                        ["pro_acodec"] = "jellyfin-aac",
                    };

                    using var retryResponse = await _tvheadendApiClient.PostFormAsync(
                        httpClient,
                        createUrl,
                        new[]
                        {
                            new KeyValuePair<string, string>("class", "profile-transcode"),
                            new KeyValuePair<string, string>("conf", minimalConf.ToJsonString())
                        },
                        cancellationToken).ConfigureAwait(false);

                    if (retryResponse.IsSuccessStatusCode)
                    {
                        createdParts.Add("streaming profile 'jellyfin'");
                    }
                    else
                    {
                        var retryErrorBody = await retryResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        _logger.LogError("Failed to create streaming profile. HTTP {Status}: {Body}", retryResponse.StatusCode, retryErrorBody);
                        return new ProfileDetectionResult
                        {
                            Success = false,
                            Message = $"Codec profiles were processed but the streaming profile creation failed (HTTP {(int)retryResponse.StatusCode}). "
                                + "Make sure the TVHeadend user has admin privileges. "
                                + $"Response: {retryErrorBody}"
                        };
                    }
                }
                else
                {
                    createdParts.Add("streaming profile 'jellyfin'");
                }
            }

            var streamProfile = existingStreamProfile
                ?? await GetStreamingProfileByNameAsync(httpClient, baseUrl, webRoot, "jellyfin", cancellationToken).ConfigureAwait(false);
            var videoCodecProfile = await GetCodecProfileByNameAsync(httpClient, baseUrl, webRoot, videoCodecProfileName, cancellationToken).ConfigureAwait(false);
            var audioCodecProfile = await GetCodecProfileByNameAsync(httpClient, baseUrl, webRoot, audioCodecProfileName, cancellationToken).ConfigureAwait(false);

            if (streamProfile != null)
            {
                var linked = await LinkStreamingProfileToCodecProfilesAsync(
                    httpClient,
                    baseUrl,
                    webRoot,
                    streamProfile.Key,
                    videoCodecProfile?.Val ?? videoCodecProfileName,
                    audioCodecProfile?.Val ?? audioCodecProfileName,
                    cancellationToken).ConfigureAwait(false);

                createdParts.Add(linked
                    ? "streaming profile linked to codec profiles"
                    : "WARNING: could not explicitly link streaming profile to codec profiles");
            }
            else
            {
                createdParts.Add("WARNING: could not resolve streaming profile UUID for codec linking");
            }

            _logger.LogInformation("Profile setup complete: {Parts}", string.Join("; ", createdParts));

            return new ProfileDetectionResult
            {
                Success = true,
                ProfileName = "jellyfin",
                ProfileClass = "profile-transcode",
                IsTranscodeProfile = true,
                Container = "mp4",
                VideoCodec = "h264",
                AudioCodec = "aac",
                VideoBitrate = 5000000,
                AudioBitrate = 128000,
                AudioChannels = 0,
                VideoHeight = 0,
                VideoIsInterlaced = false,
                VideoCodecProfile = videoCodecProfileName,
                AudioCodecProfile = audioCodecProfileName,
                Message = "Created: " + string.Join(", ", createdParts) + ". "
                    + "Set the Streaming Profile field to 'jellyfin', then click Save."
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

    /// <inheritdoc />
    public async Task<AuthTokenGenerationResult> GenerateAuthTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tokenResult = await _tokenService.GenerateValidTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!tokenResult.Success || string.IsNullOrWhiteSpace(tokenResult.AuthToken))
            {
                return tokenResult;
            }

            // Save the token to the plugin configuration
            var plugin = Plugin.Instance;
            if (plugin == null)
            {
                return new AuthTokenGenerationResult
                {
                    Success = false,
                    Message = "Plugin instance is not available."
                };
            }

            if (plugin.Configuration is Configuration.PluginConfiguration pluginConfig)
            {
                pluginConfig.AuthToken = tokenResult.AuthToken;
                plugin.SaveConfiguration();
                _logger.LogInformation("Auth token saved to plugin configuration.");
            }

            return new AuthTokenGenerationResult
            {
                Success = true,
                AuthToken = tokenResult.AuthToken,
                AttemptCount = tokenResult.AttemptCount,
                UsedRefresh = tokenResult.UsedRefresh,
                Message = tokenResult.Message + " Saved to plugin configuration."
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to TVHeadend for token generation.");
            return new AuthTokenGenerationResult
            {
                Success = false,
                Message = $"Cannot connect to TVHeadend: {ex.Message}. Check your connection and authentication settings."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating auth token.");
            return new AuthTokenGenerationResult { Success = false, Message = $"Unexpected error: {ex.Message}" };
        }
    }

    private async Task<bool> CodecProfileExistsAsync(HttpClient httpClient, string baseUrl, string webRoot, string profileName, CancellationToken cancellationToken)
    {
        try
        {
            return await FindCodecProfileEntryByReferenceAsync(httpClient, baseUrl, webRoot, profileName, cancellationToken).ConfigureAwait(false) != null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list codec profiles from TVHeadend.");
            return false;
        }
    }

    private async Task<bool> CreateCodecProfileAsync(HttpClient httpClient, string baseUrl, string webRoot, string codecClass, System.Text.Json.Nodes.JsonObject conf, CancellationToken cancellationToken)
    {
        try
        {
            var createUrl = $"{baseUrl}{webRoot}api/codec_profile/create";
            var jsonConf = conf.ToJsonString();
            _logger.LogDebug("Codec profile creation: class={Class}, conf={Json}", codecClass, jsonConf);

            using var response = await _tvheadendApiClient.PostFormAsync(
                httpClient,
                createUrl,
                new[]
                {
                    new KeyValuePair<string, string>("class", codecClass),
                    new KeyValuePair<string, string>("conf", jsonConf)
                },
                cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully created codec profile (class={Class}).", codecClass);
                return true;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Codec profile creation failed (class={Class}, HTTP {Status}): {Body}", codecClass, response.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception creating codec profile (class={Class}).", codecClass);
            return false;
        }
    }

    private async Task<CodecProfileListEntry?> GetCodecProfileByNameAsync(HttpClient httpClient, string baseUrl, string webRoot, string profileName, CancellationToken cancellationToken)
    {
        try
        {
            return await FindCodecProfileEntryByReferenceAsync(httpClient, baseUrl, webRoot, profileName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve codec profile '{ProfileName}'.", profileName);
            return null;
        }
    }

    private async Task<CodecProfileListEntry?> FindCodecProfileEntryByReferenceAsync(HttpClient httpClient, string baseUrl, string webRoot, string profileReference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileReference))
        {
            return null;
        }

        var listUrl = $"{baseUrl}{webRoot}api/codec_profile/list";
        var response = await _tvheadendApiClient.GetStringAsync(httpClient, listUrl, cancellationToken).ConfigureAwait(false);
        var list = JsonSerializer.Deserialize<CodecProfileListResponse>(response, JsonOptions);
        if (list?.Entries == null || list.Entries.Length == 0)
        {
            return null;
        }

        foreach (var entry in list.Entries)
        {
            var uuid = entry.EffectiveUuid;
            var title = entry.EffectiveTitle;
            var normalizedTitle = NormalizeCodecProfileTitle(title);

            if (string.Equals(profileReference, uuid, StringComparison.OrdinalIgnoreCase)
                || string.Equals(profileReference, title, StringComparison.OrdinalIgnoreCase)
                || string.Equals(profileReference, normalizedTitle, StringComparison.OrdinalIgnoreCase))
            {
                return new CodecProfileListEntry
                {
                    Key = uuid,
                    Val = string.IsNullOrWhiteSpace(normalizedTitle) ? title : normalizedTitle,
                };
            }
        }

        return null;
    }

    private async Task<ProfileListEntry?> GetStreamingProfileByNameAsync(HttpClient httpClient, string baseUrl, string webRoot, string profileName, CancellationToken cancellationToken)
    {
        try
        {
            var listUrl = $"{baseUrl}{webRoot}api/profile/list";
            var response = await _tvheadendApiClient.GetStringAsync(httpClient, listUrl, cancellationToken).ConfigureAwait(false);
            var list = JsonSerializer.Deserialize<ProfileListResponse>(response, JsonOptions);
            return list?.Entries?.FirstOrDefault(e => string.Equals(e.Val, profileName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve streaming profile '{ProfileName}'.", profileName);
            return null;
        }
    }

    private async Task<bool> LinkStreamingProfileToCodecProfilesAsync(HttpClient httpClient, string baseUrl, string webRoot, string streamProfileUuid, string videoCodecRef, string audioCodecRef, CancellationToken cancellationToken)
    {
        try
        {
            var loadUrl = $"{baseUrl}{webRoot}api/idnode/load?uuid={Uri.EscapeDataString(streamProfileUuid)}";
            var loadBody = await _tvheadendApiClient.GetStringAsync(httpClient, loadUrl, cancellationToken).ConfigureAwait(false);
            var loadResponse = JsonSerializer.Deserialize<IdNodeLoadResponse>(loadBody, JsonOptions);
            if (loadResponse?.Entries == null || loadResponse.Entries.Length == 0)
            {
                _logger.LogWarning("Could not load stream profile UUID {Uuid} before linking codec profiles.", streamProfileUuid);
                return false;
            }

            var entry = loadResponse.Entries[0];
            var saveUrl = $"{baseUrl}{webRoot}api/idnode/save";
            var sourceVideoCodecs = ReadStringArrayOrParam(entry.SourceVideoCodecs, entry.Params, "src_vcodec");
            var sourceAudioCodecs = ReadStringArrayOrParam(entry.SourceAudioCodecs, entry.Params, "src_acodec");
            var sourceSubtitleCodecs = ReadStringArrayOrParam(entry.SourceSubtitleCodecs, entry.Params, "src_scodec");
            var node = new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = ReadStringOrParam(entry.Name, entry.Params, "name") ?? "jellyfin",
                ["enabled"] = ReadBoolOrParam(entry.Enabled, entry.Params, "enabled") ?? true,
                ["default"] = ReadBoolOrParam(entry.IsDefault, entry.Params, "default") ?? false,
                ["comment"] = ReadStringOrParam(entry.Comment, entry.Params, "comment") ?? string.Empty,
                ["timeout"] = ReadIntOrParam(entry.Timeout, entry.Params, "timeout") ?? 0,
                ["timeout_start"] = ReadIntOrParam(entry.TimeoutStart, entry.Params, "timeout_start") ?? 0,
                ["priority"] = ReadIntOrParam(entry.Priority, entry.Params, "priority") ?? 0,
                ["fpriority"] = ReadIntOrParam(entry.FPriority, entry.Params, "fpriority") ?? 0,
                ["restart"] = ReadBoolOrParam(entry.Restart, entry.Params, "restart") ?? false,
                ["contaccess"] = ReadBoolOrParam(entry.ContinuousAccess, entry.Params, "contaccess") ?? true,
                ["catimeout"] = ReadIntOrParam(entry.CaTimeout, entry.Params, "catimeout") ?? 2000,
                ["swservice"] = ReadBoolOrParam(entry.SoftwareService, entry.Params, "swservice") ?? true,
                ["svfilter"] = ReadIntOrParam(entry.ServiceVideoFilter, entry.Params, "svfilter") ?? 0,
                ["container"] = ReadIntOrParam(entry.Container, entry.Params, "container") ?? 2,
                ["pro_vcodec"] = videoCodecRef,
                ["src_vcodec"] = ToJsonArray(sourceVideoCodecs.Count > 0 ? sourceVideoCodecs : DefaultSourceVideoCodecs),
                ["pro_acodec"] = audioCodecRef,
                ["src_acodec"] = ToJsonArray(sourceAudioCodecs.Count > 0 ? sourceAudioCodecs : DefaultSourceAudioCodecs),
                ["pro_scodec"] = ReadStringOrParam(entry.ProSubtitleCodec, entry.Params, "pro_scodec") ?? string.Empty,
                ["src_scodec"] = ToJsonArray(sourceSubtitleCodecs),
                ["uuid"] = streamProfileUuid,
            };

            using var response = await _tvheadendApiClient.PostFormAsync(
                httpClient,
                saveUrl,
                new[]
                {
                    new KeyValuePair<string, string>("node", node.ToJsonString())
                },
                cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Linked stream profile UUID {Uuid} to codec profiles pro_vcodec={Vcodec}, pro_acodec={Acodec}.", streamProfileUuid, videoCodecRef, audioCodecRef);
                return true;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Failed linking stream profile UUID {Uuid} (HTTP {Status}): {Body}", streamProfileUuid, response.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception linking stream profile UUID {Uuid} to codec profiles.", streamProfileUuid);
            return false;
        }
    }

    private static string NormalizeCodecProfileTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var separatorIndex = title.IndexOf(" (", StringComparison.Ordinal);
        return separatorIndex > 0 ? title[..separatorIndex] : title;
    }

    private static System.Text.Json.Nodes.JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new System.Text.Json.Nodes.JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static string? ReadStringOrParam(JsonElement directValue, IReadOnlyList<IdNodeParam> parameters, string parameterName)
    {
        return ReadString(directValue) ?? ReadString(GetParamValue(parameters, parameterName));
    }

    private static int? ReadIntOrParam(JsonElement directValue, IReadOnlyList<IdNodeParam> parameters, string parameterName)
    {
        return ReadInt(directValue) ?? ReadInt(GetParamValue(parameters, parameterName));
    }

    private static bool? ReadBoolOrParam(JsonElement directValue, IReadOnlyList<IdNodeParam> parameters, string parameterName)
    {
        return ReadBool(directValue) ?? ReadBool(GetParamValue(parameters, parameterName));
    }

    private static IReadOnlyList<string> ReadStringArrayOrParam(JsonElement directValue, IReadOnlyList<IdNodeParam> parameters, string parameterName)
    {
        var values = ReadStringArray(directValue);
        return values.Count > 0 ? values : ReadStringArray(GetParamValue(parameters, parameterName));
    }

    private static JsonElement GetParamValue(IReadOnlyList<IdNodeParam> parameters, string parameterName)
    {
        foreach (var parameter in parameters)
        {
            if (string.Equals(parameter.Id, parameterName, StringComparison.OrdinalIgnoreCase))
            {
                return parameter.Value;
            }
        }

        return default;
    }

    private static string? ReadString(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static int? ReadInt(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static bool? ReadBool(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
        {
            return intValue != 0;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var rawValue = value.GetString();
            if (bool.TryParse(rawValue, out var boolValue))
            {
                return boolValue;
            }

            if (int.TryParse(rawValue, out var parsedInt))
            {
                return parsedInt != 0;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var values = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            var stringValue = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
            if (!string.IsNullOrWhiteSpace(stringValue))
            {
                values.Add(stringValue);
            }
        }

        return values;
    }
}
