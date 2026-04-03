using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Api;

/// <summary>
/// API controller that exposes helper endpoints for the plugin configuration page.
/// </summary>
[ApiController]
[Route("TvHeadendApi")]
[Authorize(Policy = Policies.RequiresElevation)]
public class TvHeadendApiController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<TvHeadendApiController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvHeadendApiController"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public TvHeadendApiController(ILogger<TvHeadendApiController> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Detects the configured TVHeadend streaming profile and returns its format settings
    /// so the Fast Channel Switching fields can be auto-filled.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A JSON object describing the detected profile, or an error message.</returns>
    [HttpGet("DetectProfile")]
    public async Task<ActionResult<ProfileDetectionResult>> DetectProfile(CancellationToken cancellationToken)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return BadRequest(new ProfileDetectionResult { Success = false, Message = "Plugin configuration is not available." });
            }

            if (string.IsNullOrWhiteSpace(config.StreamingProfile))
            {
                return BadRequest(new ProfileDetectionResult { Success = false, Message = "No streaming profile configured." });
            }

            using var httpClient = BuildHttpClient(config);
            var baseUrl = $"{(config.UseSSL ? "https" : "http")}://{config.Host}:{config.Port}";
            var webRoot = string.IsNullOrWhiteSpace(config.Webroot) ? "/" : config.Webroot.TrimEnd('/') + "/";

            // Step 1: Fetch profile list to find the UUID
            var listUrl = $"{baseUrl}{webRoot}api/profile/list";
            _logger.LogInformation("Fetching profile list from TVHeadend at {Url}.", listUrl);

            var listResponse = await httpClient.GetStringAsync(listUrl, cancellationToken).ConfigureAwait(false);
            var profileList = JsonSerializer.Deserialize<ProfileListResponse>(listResponse, JsonOptions);

            var matchingProfile = profileList?.Entries?.FirstOrDefault(e =>
                string.Equals(e.Val, config.StreamingProfile, StringComparison.OrdinalIgnoreCase));

            if (matchingProfile == null)
            {
                return Ok(new ProfileDetectionResult
                {
                    Success = false,
                    Message = $"Profile '{config.StreamingProfile}' not found in TVHeadend. Available profiles: {string.Join(", ", profileList?.Entries?.Select(e => e.Val) ?? Array.Empty<string>())}."
                });
            }

            // Step 2: Load the full profile details via idnode/load
            var loadUrl = $"{baseUrl}{webRoot}api/idnode/load?uuid={matchingProfile.Key}";
            _logger.LogInformation("Loading profile details from TVHeadend at {Url}.", loadUrl);

            var loadResponse = await httpClient.GetStringAsync(loadUrl, cancellationToken).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(loadResponse);
            var entries = doc.RootElement.GetProperty("entries");
            if (entries.GetArrayLength() == 0)
            {
                return Ok(new ProfileDetectionResult { Success = false, Message = "TVHeadend returned empty profile data." });
            }

            var entry = entries[0];

            // Determine profile class
            var profileClass = GetStringProp(entry, "class") ?? string.Empty;
            var profileName = GetStringProp(entry, "name") ?? config.StreamingProfile;

            var result = new ProfileDetectionResult
            {
                Success = true,
                ProfileName = profileName,
                ProfileClass = profileClass,
            };

            if (profileClass.Contains("transcode", StringComparison.OrdinalIgnoreCase))
            {
                result.IsTranscodeProfile = true;
                result.Container = MapContainer(GetStringProp(entry, "container") ?? GetIntProp(entry, "container")?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
                result.VideoCodec = MapVideoCodec(GetStringProp(entry, "vcodec") ?? string.Empty);
                result.AudioCodec = MapAudioCodec(GetStringProp(entry, "acodec") ?? string.Empty);
                result.VideoBitrate = (GetIntProp(entry, "vbitrate") ?? 0) * 1000; // TVH stores kbps, we want bps
                result.AudioBitrate = (GetIntProp(entry, "abitrate") ?? 0) * 1000;
                result.VideoHeight = GetIntProp(entry, "resolution") ?? 0;
                result.AudioChannels = GetIntProp(entry, "channels") ?? 0;
                result.Message = $"Transcode profile '{profileName}' detected. Format hints have been extracted.";
            }
            else
            {
                result.IsTranscodeProfile = false;
                result.Message = $"Profile '{profileName}' is of type '{profileClass}'. "
                    + "This is NOT a transcode profile – the output format varies per channel. "
                    + "Fast Channel Switching requires a transcode profile that outputs a fixed format for every channel.";
            }

            return Ok(result);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to TVHeadend.");
            return Ok(new ProfileDetectionResult
            {
                Success = false,
                Message = $"Cannot connect to TVHeadend: {ex.Message}. Check your connection and authentication settings."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting streaming profile.");
            return Ok(new ProfileDetectionResult { Success = false, Message = $"Unexpected error: {ex.Message}" });
        }
    }

    /// <summary>
    /// Performs a comprehensive health check: tests TVHeadend connectivity, reports
    /// server info, channel count, available profiles, DVR entries and gives
    /// configuration recommendations.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A structured health-check report.</returns>
    [HttpGet("HealthCheck")]
    public async Task<ActionResult<HealthCheckResult>> HealthCheck(CancellationToken cancellationToken)
    {
        var report = new HealthCheckResult();
        var config = Plugin.Instance?.Configuration;

        if (config == null)
        {
            report.Connection = "❌ Plugin configuration is not available.";
            return Ok(report);
        }

        // ── Plugin config summary ──────────────────────────────────────
        report.PluginSettings.Add($"Streaming Profile: {(string.IsNullOrWhiteSpace(config.StreamingProfile) ? "(not set)" : config.StreamingProfile)}");
        report.PluginSettings.Add($"Fast Channel Switching: {(config.EnableFastChannelSwitching ? "enabled" : "disabled")}");
        report.PluginSettings.Add($"Direct Play: {config.SupportsDirectPlay}, Direct Stream: {config.SupportsDirectStream}, Transcoding: {config.SupportsTranscoding}");
        report.PluginSettings.Add($"Probing: {config.SupportsProbing}, Infinite Stream: {config.IsInfiniteStream}, Ignore DTS: {config.IgnoreDts}");
        report.PluginSettings.Add($"DVR enabled: {config.EnableTvhDvr}, Recording Profile: {config.RecordingProfile}");

        if (config.EnableFastChannelSwitching)
        {
            report.PluginSettings.Add($"Format hints: {config.StreamContainer} / {config.VideoCodec} {config.VideoWidth}x{config.VideoHeight}@{config.VideoFramerate}fps / {config.AudioCodec} {config.AudioChannels}ch");
        }

        // ── Connectivity test ──────────────────────────────────────────
        HttpClient httpClient;
        string baseUrl;
        string webRoot;
        try
        {
            httpClient = BuildHttpClient(config);
            baseUrl = $"{(config.UseSSL ? "https" : "http")}://{config.Host}:{config.Port}";
            webRoot = string.IsNullOrWhiteSpace(config.Webroot) ? "/" : config.Webroot.TrimEnd('/') + "/";
        }
        catch (Exception ex)
        {
            report.Connection = $"❌ Failed to build HTTP client: {ex.Message}";
            return Ok(report);
        }

        using (httpClient)
        {
            // Test basic connectivity with serverinfo
            try
            {
                var infoUrl = $"{baseUrl}{webRoot}api/serverinfo";
                var infoResponse = await httpClient.GetStringAsync(infoUrl, cancellationToken).ConfigureAwait(false);
                using var infoDoc = JsonDocument.Parse(infoResponse);
                var root = infoDoc.RootElement;

                var swVersion = GetStringProp(root, "sw_version") ?? "unknown";
                var apiVersion = GetIntProp(root, "api_version")?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";
                var serverName = GetStringProp(root, "name") ?? string.Empty;

                report.Connection = $"✅ Connected to {(string.IsNullOrEmpty(serverName) ? config.Host : serverName)}";
                report.ServerVersion = $"TVHeadend {swVersion} (API v{apiVersion})";
            }
            catch (HttpRequestException ex)
            {
                report.Connection = $"❌ Cannot reach TVHeadend at {config.Host}:{config.Port} – {ex.Message}";
                return Ok(report);
            }
            catch (Exception ex)
            {
                report.Connection = $"⚠️ Connected but serverinfo failed: {ex.Message}";
            }

            // Channel count
            try
            {
                var chUrl = $"{baseUrl}{webRoot}api/channel/grid?limit=1";
                var chResponse = await httpClient.GetStringAsync(chUrl, cancellationToken).ConfigureAwait(false);
                using var chDoc = JsonDocument.Parse(chResponse);
                if (chDoc.RootElement.TryGetProperty("total", out var totalProp) && totalProp.TryGetInt32(out var total))
                {
                    report.ChannelCount = total;
                }
            }
            catch (Exception ex)
            {
                report.Warnings.Add($"Could not fetch channel count: {ex.Message}");
            }

            // Streaming profiles
            try
            {
                var profUrl = $"{baseUrl}{webRoot}api/profile/list";
                var profResponse = await httpClient.GetStringAsync(profUrl, cancellationToken).ConfigureAwait(false);
                var profList = JsonSerializer.Deserialize<ProfileListResponse>(profResponse, JsonOptions);
                var profiles = profList?.Entries?.Select(e => e.Val).ToList() ?? new List<string>();
                foreach (var p in profiles)
                {
                    report.AvailableProfiles.Add(p);
                }

                if (!string.IsNullOrWhiteSpace(config.StreamingProfile)
                    && !report.AvailableProfiles.Any(p => string.Equals(p, config.StreamingProfile, StringComparison.OrdinalIgnoreCase)))
                {
                    report.Warnings.Add($"Configured streaming profile '{config.StreamingProfile}' does not exist in TVHeadend.");
                }
            }
            catch (Exception ex)
            {
                report.Warnings.Add($"Could not fetch profile list: {ex.Message}");
            }

            // DVR entries count (upcoming)
            if (config.EnableTvhDvr)
            {
                try
                {
                    var dvrUrl = $"{baseUrl}{webRoot}api/dvr/entry/grid?limit=1";
                    var dvrResponse = await httpClient.GetStringAsync(dvrUrl, cancellationToken).ConfigureAwait(false);
                    using var dvrDoc = JsonDocument.Parse(dvrResponse);
                    if (dvrDoc.RootElement.TryGetProperty("total", out var dvrTotal) && dvrTotal.TryGetInt32(out var dvrCount))
                    {
                        report.DvrEntryCount = dvrCount;
                    }
                }
                catch (Exception ex)
                {
                    report.Warnings.Add($"Could not fetch DVR entries: {ex.Message}");
                }
            }
        }

        // ── Recommendations ────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(config.StreamingProfile))
        {
            report.Recommendations.Add("Set a Streaming Profile. Use 'pass' for original quality or create a transcode profile for fast channel switching.");
        }

        if (config.EnableFastChannelSwitching && config.SupportsProbing)
        {
            report.Recommendations.Add("Fast Channel Switching is enabled but Probing is also on. Probing is automatically disabled when fast switching is active, but you should uncheck it to avoid confusion.");
        }

        if (!config.EnableFastChannelSwitching && !config.SupportsProbing)
        {
            report.Recommendations.Add("Probing is disabled but Fast Channel Switching is off too. Jellyfin won't know the stream format. Either enable Probing or enable Fast Channel Switching with format hints.");
        }

        if (config.EnableFastChannelSwitching && string.IsNullOrWhiteSpace(config.VideoCodec))
        {
            report.Recommendations.Add("Fast Channel Switching is on but no Video Codec is configured. Set it to match your TVHeadend transcoding profile output.");
        }

        if (config.SupportsTranscoding && config.SupportsDirectPlay)
        {
            report.Recommendations.Add("Both Direct Play and Transcoding are enabled. For live TV, Direct Play alone is usually sufficient and faster.");
        }

        if (config.BufferMs > 2000)
        {
            report.Recommendations.Add($"Buffer is set to {config.BufferMs} ms which is quite high. Consider lowering it to reduce channel-switch latency.");
        }

        if (config.EnableTvhDvr && string.Equals(config.RecordingProfile, "default", StringComparison.OrdinalIgnoreCase)
            && report.AvailableProfiles.Count > 0
            && !report.AvailableProfiles.Any(p => string.Equals(p, "default", StringComparison.OrdinalIgnoreCase)))
        {
            report.Recommendations.Add("Recording profile is set to 'default' but it may not exist in TVHeadend. Check your DVR configuration profiles.");
        }

        if (report.ChannelCount == 0)
        {
            report.Recommendations.Add("No channels found. Make sure TVHeadend has scanned and mapped channels.");
        }

        if (report.Warnings.Count == 0 && report.Recommendations.Count == 0)
        {
            report.Recommendations.Add("✅ Everything looks good!");
        }

        return Ok(report);
    }

    /// <summary>
    /// Creates an optimised set of profiles in TVHeadend for fast channel switching:
    /// 1. A video codec profile "jellyfin-h264" (H.264 / libx264, 5 Mbps cap, faster preset, zerolatency tune, deinterlace)
    /// 2. An audio codec profile "jellyfin-aac" (AAC, 128 kbps)
    /// 3. A streaming transcode profile "jellyfin" that references both codec profiles in an MPEG-TS container.
    /// If any of these already exist, they are skipped.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A status message indicating success or failure.</returns>
    [HttpPost("CreateProfile")]
    public async Task<ActionResult<ProfileDetectionResult>> CreateProfile(CancellationToken cancellationToken)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return BadRequest(new ProfileDetectionResult { Success = false, Message = "Plugin configuration is not available." });
            }

            using var httpClient = BuildHttpClient(config);
            var baseUrl = $"{(config.UseSSL ? "https" : "http")}://{config.Host}:{config.Port}";
            var webRoot = string.IsNullOrWhiteSpace(config.Webroot) ? "/" : config.Webroot.TrimEnd('/') + "/";

            var createdParts = new List<string>();

            // ── Step 1: Create video codec profile "jellyfin-h264" ──────
            const string videoCodecProfileName = "jellyfin-h264";
            var videoCodecExists = await CodecProfileExistsAsync(httpClient, baseUrl, webRoot, videoCodecProfileName, cancellationToken).ConfigureAwait(false);

            if (!videoCodecExists)
            {
                _logger.LogInformation("Creating video codec profile '{Name}' in TVHeadend.", videoCodecProfileName);

                var videoConf = new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = videoCodecProfileName,
                    ["description"] = "Auto-created by Jellyfin plugin. H.264 with 5 Mbps cap for fast channel switching.",
                    ["deinterlace"] = true,
                    ["height"] = 0,
                    ["scaling_mode"] = 0,
                    ["hwaccel"] = false,
                    ["bit_rate"] = 5000,
                    ["crf"] = 0,
                    ["profile"] = -99,
                    ["pix_fmt"] = -1,
                    ["preset"] = "faster",
                    ["tune"] = "zerolatency",
                    ["params"] = string.Empty,
                };

                var created = await CreateCodecProfileAsync(httpClient, baseUrl, webRoot, "libx264", videoConf, cancellationToken).ConfigureAwait(false);
                if (created)
                {
                    createdParts.Add("video codec profile 'jellyfin-h264' (H.264 libx264, 5 Mbps)");
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

            // ── Step 2: Create audio codec profile "jellyfin-aac" ───────
            const string audioCodecProfileName = "jellyfin-aac";
            var audioCodecExists = await CodecProfileExistsAsync(httpClient, baseUrl, webRoot, audioCodecProfileName, cancellationToken).ConfigureAwait(false);

            if (!audioCodecExists)
            {
                _logger.LogInformation("Creating audio codec profile '{Name}' in TVHeadend.", audioCodecProfileName);

                var audioConf = new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = audioCodecProfileName,
                    ["description"] = "Auto-created by Jellyfin plugin. AAC 128 kbps for fast channel switching.",
                    ["bit_rate"] = 128,
                    ["profile"] = -99,
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

            // ── Step 3: Create streaming profile "jellyfin" ─────────────
            var listUrl = $"{baseUrl}{webRoot}api/profile/list";
            var listResponse = await httpClient.GetStringAsync(listUrl, cancellationToken).ConfigureAwait(false);
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

                // Reference the codec profiles by name so TVHeadend uses their
                // bitrate/preset/tune settings instead of defaults.
                var useVideoCodec = videoCodecExists || createdParts.Any(p => p.Contains("jellyfin-h264", StringComparison.Ordinal) && !p.Contains("Could not", StringComparison.Ordinal))
                    ? videoCodecProfileName
                    : "libx264";
                var useAudioCodec = audioCodecExists || createdParts.Any(p => p.Contains("jellyfin-aac", StringComparison.Ordinal) && !p.Contains("Could not", StringComparison.Ordinal))
                    ? audioCodecProfileName
                    : "aac";

                var confNode = new System.Text.Json.Nodes.JsonObject
                {
                    ["enabled"] = true,
                    ["name"] = "jellyfin",
                    ["comment"] = "Auto-created by Jellyfin TvHeadendApi plugin for fast channel switching.",
                    ["container"] = 2,
                    ["vcodec"] = useVideoCodec,
                    ["acodec"] = useAudioCodec,
                    ["resolution"] = 0,
                    ["channels"] = 0,
                    ["vbitrate"] = 0,
                    ["abitrate"] = 0,
                };

                var jsonConf = confNode.ToJsonString();
                _logger.LogDebug("Streaming profile creation payload: class=profile-transcode, conf={Json}", jsonConf);

                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("class", "profile-transcode"),
                    new KeyValuePair<string, string>("conf", jsonConf)
                });

                var response = await httpClient.PostAsync(createUrl, content, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    // Retry with minimal fields
                    _logger.LogWarning("First streaming profile attempt failed (HTTP {Status}). Retrying with minimal config.", response.StatusCode);

                    var minimalConf = new System.Text.Json.Nodes.JsonObject
                    {
                        ["enabled"] = true,
                        ["name"] = "jellyfin",
                        ["container"] = 2,
                        ["vcodec"] = useVideoCodec,
                        ["acodec"] = useAudioCodec,
                    };

                    var retryContent = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("class", "profile-transcode"),
                        new KeyValuePair<string, string>("conf", minimalConf.ToJsonString())
                    });

                    response = await httpClient.PostAsync(createUrl, retryContent, cancellationToken).ConfigureAwait(false);
                }

                if (response.IsSuccessStatusCode)
                {
                    createdParts.Add("streaming profile 'jellyfin'");
                }
                else
                {
                    var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    _logger.LogError("Failed to create streaming profile. HTTP {Status}: {Body}", response.StatusCode, errorBody);
                    return Ok(new ProfileDetectionResult
                    {
                        Success = false,
                        Message = $"Codec profiles were processed but the streaming profile creation failed (HTTP {(int)response.StatusCode}). "
                            + "Make sure the TVHeadend user has admin privileges. "
                            + $"Response: {errorBody}"
                    });
                }
            }

            _logger.LogInformation("Profile setup complete: {Parts}", string.Join("; ", createdParts));

            return Ok(new ProfileDetectionResult
            {
                Success = true,
                ProfileName = "jellyfin",
                ProfileClass = "profile-transcode",
                IsTranscodeProfile = true,
                Container = "mpegts",
                VideoCodec = "h264",
                AudioCodec = "aac",
                VideoBitrate = 5000000,
                AudioBitrate = 128000,
                AudioChannels = 0,
                VideoHeight = 0,
                VideoCodecProfile = videoCodecProfileName,
                AudioCodecProfile = audioCodecProfileName,
                Message = "Created: " + string.Join(", ", createdParts) + ". "
                    + "Set the Streaming Profile field to 'jellyfin', then click Save."
            });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to TVHeadend.");
            return Ok(new ProfileDetectionResult
            {
                Success = false,
                Message = $"Cannot connect to TVHeadend: {ex.Message}. Check your connection and authentication settings."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating profiles.");
            return Ok(new ProfileDetectionResult { Success = false, Message = $"Unexpected error: {ex.Message}" });
        }
    }

    /// <summary>
    /// Checks whether a codec profile with the given name already exists in TVHeadend.
    /// </summary>
    private async Task<bool> CodecProfileExistsAsync(HttpClient httpClient, string baseUrl, string webRoot, string profileName, CancellationToken cancellationToken)
    {
        try
        {
            var listUrl = $"{baseUrl}{webRoot}api/codec_profile/list";
            var response = await httpClient.GetStringAsync(listUrl, cancellationToken).ConfigureAwait(false);
            var list = JsonSerializer.Deserialize<CodecProfileListResponse>(response, JsonOptions);
            return list?.Entries?.Any(e => string.Equals(e.Val, profileName, StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list codec profiles from TVHeadend.");
            return false;
        }
    }

    /// <summary>
    /// Creates a codec profile in TVHeadend via <c>api/codec_profile/create</c>.
    /// </summary>
    /// <param name="httpClient">The HTTP client.</param>
    /// <param name="baseUrl">TVHeadend base URL.</param>
    /// <param name="webRoot">TVHeadend web root.</param>
    /// <param name="codecClass">The codec class name (e.g. "libx264", "aac").</param>
    /// <param name="conf">The configuration JSON object.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if creation succeeded.</returns>
    private async Task<bool> CreateCodecProfileAsync(HttpClient httpClient, string baseUrl, string webRoot, string codecClass, System.Text.Json.Nodes.JsonObject conf, CancellationToken cancellationToken)
    {
        try
        {
            var createUrl = $"{baseUrl}{webRoot}api/codec_profile/create";
            var jsonConf = conf.ToJsonString();
            _logger.LogDebug("Codec profile creation: class={Class}, conf={Json}", codecClass, jsonConf);

            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("class", codecClass),
                new KeyValuePair<string, string>("conf", jsonConf)
            });

            var response = await httpClient.PostAsync(createUrl, content, cancellationToken).ConfigureAwait(false);

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

    private static HttpClient BuildHttpClient(Configuration.PluginConfiguration config)
    {
        var handler = new HttpClientHandler { CheckCertificateRevocationList = true };
        if (config.UseSSL && config.IgnoreCertificateErrors)
        {
            handler.ServerCertificateCustomValidationCallback = static (_, _, _, _) => true;
        }

        var client = new HttpClient(handler);
        if (!config.AllowAnonymousAccess && !string.IsNullOrWhiteSpace(config.Username))
        {
            var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.Username}:{config.Password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", creds);
        }

        return client;
    }

    private static string? GetStringProp(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var prop))
        {
            return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
        }

        return null;
    }

    private static int? GetIntProp(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var intVal))
            {
                return intVal;
            }

            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>
    /// Maps TVHeadend container values (may be numeric enum or string) to FFmpeg container names.
    /// </summary>
    private static string MapContainer(string raw)
    {
        return raw.ToLowerInvariant() switch
        {
            "0" or "" or "not set" => string.Empty,
            "1" or "matroska" or "mkv" => "matroska",
            "2" or "mpegts" or "ts" => "mpegts",
            "3" or "mpegps" or "ps" => "mpegps",
            "4" or "mp4" => "mp4",
            _ => raw.ToLowerInvariant()
        };
    }

    /// <summary>
    /// Maps TVHeadend/libav video codec names to FFmpeg codec names.
    /// </summary>
    private static string MapVideoCodec(string raw)
    {
        return raw.ToLowerInvariant() switch
        {
            "" or "copy" or "do not use" => string.Empty,
            "libx264" or "h264" or "h264_vaapi" or "h264_nvenc" or "h264_qsv" => "h264",
            "libx265" or "hevc" or "hevc_vaapi" or "hevc_nvenc" or "hevc_qsv" => "hevc",
            "mpeg2video" or "mpeg2" => "mpeg2video",
            "libvpx" or "vp8" => "vp8",
            "libvpx-vp9" or "vp9" => "vp9",
            _ => raw.ToLowerInvariant()
        };
    }

    /// <summary>
    /// Maps TVHeadend/libav audio codec names to FFmpeg codec names.
    /// </summary>
    private static string MapAudioCodec(string raw)
    {
        return raw.ToLowerInvariant() switch
        {
            "" or "copy" or "do not use" => string.Empty,
            "libfdk_aac" or "aac" => "aac",
            "ac3" or "a52" => "ac3",
            "eac3" => "eac3",
            "libmp3lame" or "mp3" => "mp3",
            "mp2" or "libtwolame" => "mp2",
            "libvorbis" or "vorbis" => "vorbis",
            "libopus" or "opus" => "opus",
            _ => raw.ToLowerInvariant()
        };
    }

    /// <summary>
    /// Response model for TVHeadend's api/profile/list endpoint.
    /// </summary>
    private sealed class ProfileListResponse
    {
        /// <summary>Gets the profile entries.</summary>
        public IReadOnlyList<ProfileListEntry>? Entries { get; init; }
    }

    /// <summary>
    /// A single entry in the profile list.
    /// </summary>
    private sealed class ProfileListEntry
    {
        /// <summary>Gets the profile UUID.</summary>
        public string Key { get; init; } = string.Empty;

        /// <summary>Gets the profile display name.</summary>
        public string Val { get; init; } = string.Empty;
    }

    /// <summary>
    /// Response model for TVHeadend's api/codec_profile/list endpoint.
    /// </summary>
    private sealed class CodecProfileListResponse
    {
        /// <summary>Gets the codec profile entries.</summary>
        public IReadOnlyList<CodecProfileListEntry>? Entries { get; init; }
    }

    /// <summary>
    /// A single entry in the codec profile list.
    /// </summary>
    private sealed class CodecProfileListEntry
    {
        /// <summary>Gets the codec profile UUID.</summary>
        public string Key { get; init; } = string.Empty;

        /// <summary>Gets the codec profile display name.</summary>
        public string Val { get; init; } = string.Empty;
    }
}
