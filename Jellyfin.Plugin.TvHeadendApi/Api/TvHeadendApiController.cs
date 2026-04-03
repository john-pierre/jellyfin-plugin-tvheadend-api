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
    /// Creates an optimised transcode streaming profile called "jellyfin" in TVHeadend.
    /// The profile uses H.264 (veryfast preset) + AAC in an MPEG-TS container and is
    /// designed for the fastest possible channel switching in Jellyfin.
    /// If a profile with that name already exists, the endpoint returns a hint instead.
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

            // Check if a profile named "jellyfin" already exists
            var listUrl = $"{baseUrl}{webRoot}api/profile/list";
            var listResponse = await httpClient.GetStringAsync(listUrl, cancellationToken).ConfigureAwait(false);
            var profileList = JsonSerializer.Deserialize<ProfileListResponse>(listResponse, JsonOptions);

            var existing = profileList?.Entries?.FirstOrDefault(e =>
                string.Equals(e.Val, "jellyfin", StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                return Ok(new ProfileDetectionResult
                {
                    Success = true,
                    ProfileName = "jellyfin",
                    IsTranscodeProfile = true,
                    Message = "A profile named 'jellyfin' already exists in TVHeadend. "
                        + "Set the Streaming Profile field to 'jellyfin' and click 'Detect Profile' to read its settings."
                });
            }

            // Create the profile via TVHeadend API
            var createUrl = $"{baseUrl}{webRoot}api/profile/create";
            _logger.LogInformation("Creating 'jellyfin' transcode profile in TVHeadend at {Url}.", createUrl);

            // Optimised settings for fast channel switching:
            // - MPEG-TS container: lowest mux overhead, instant-start friendly
            // - libx264 / veryfast: fast encode, wide client compatibility
            // - AAC stereo 128 kbps: universally supported audio
            // - Resolution 0 = keep original; no unnecessary scaling
            var profileConf = new Dictionary<string, object>
            {
                { "class", "profile-transcode" },
                { "enabled", true },
                { "name", "jellyfin" },
                { "comment", "Auto-created by Jellyfin TvHeadendApi plugin for fast channel switching." },
                { "container", 2 },           // MPEG-TS
                { "vcodec", "libx264" },
                { "vcodec_preset", "veryfast" },
                { "acodec", "aac" },
                { "resolution", 0 },          // keep original
                { "channels", 0 },            // keep original audio channels
                { "vbitrate", 0 },            // auto / source bitrate
                { "abitrate", 128 },          // 128 kbps AAC
            };

            var jsonConf = JsonSerializer.Serialize(profileConf, JsonOptions);
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("conf", jsonConf)
            });

            var response = await httpClient.PostAsync(createUrl, content, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully created 'jellyfin' transcode profile in TVHeadend.");
                return Ok(new ProfileDetectionResult
                {
                    Success = true,
                    ProfileName = "jellyfin",
                    ProfileClass = "profile-transcode",
                    IsTranscodeProfile = true,
                    Container = "mpegts",
                    VideoCodec = "h264",
                    AudioCodec = "aac",
                    AudioBitrate = 128000,
                    AudioChannels = 0,
                    VideoHeight = 0,
                    VideoBitrate = 0,
                    Message = "Profile 'jellyfin' created successfully in TVHeadend (H.264 veryfast + AAC in MPEG-TS). "
                        + "Set the Streaming Profile field to 'jellyfin', then click Save."
                });
            }

            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogError("Failed to create profile. HTTP {Status}: {Body}", response.StatusCode, errorBody);
            return Ok(new ProfileDetectionResult
            {
                Success = false,
                Message = $"TVHeadend rejected the profile creation (HTTP {(int)response.StatusCode}). "
                    + "Make sure the TVHeadend user has admin privileges. "
                    + $"Response: {errorBody}"
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
            _logger.LogError(ex, "Error creating streaming profile.");
            return Ok(new ProfileDetectionResult { Success = false, Message = $"Unexpected error: {ex.Message}" });
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
}
