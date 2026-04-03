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
    /// Returns basic plugin metadata used by the configuration UI.
    /// </summary>
    /// <returns>Plugin name and version.</returns>
    [HttpGet("PluginInfo")]
    public ActionResult<object> GetPluginInfo()
    {
        var version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown";
        return Ok(new
        {
            Name = "TvHeadendApi",
            Version = version
        });
    }

    /// <summary>
    /// Resets the plugin configuration to default values and saves it.
    /// </summary>
    /// <returns>A status message indicating success or failure.</returns>
    [HttpPost("ResetToDefaults")]
    public ActionResult<ProfileDetectionResult> ResetToDefaults()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return BadRequest(new ProfileDetectionResult { Success = false, Message = "Plugin instance is not available." });
        }

        var defaults = new Configuration.PluginConfiguration();
        var config = plugin.Configuration;
        foreach (var property in typeof(Configuration.PluginConfiguration).GetProperties())
        {
            if (!property.CanRead || !property.CanWrite)
            {
                continue;
            }

            var defaultValue = property.GetValue(defaults);
            property.SetValue(config, defaultValue);
        }

        plugin.SaveConfiguration();

        return Ok(new ProfileDetectionResult
        {
            Success = true,
            Message = "Configuration has been reset to defaults."
        });
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
                var videoCodecProfileRef = GetStringProp(entry, "pro_vcodec") ?? string.Empty;
                var audioCodecProfileRef = GetStringProp(entry, "pro_acodec") ?? string.Empty;
                var rawVideoCodec = GetStringProp(entry, "vcodec") ?? string.Empty;
                var rawAudioCodec = GetStringProp(entry, "acodec") ?? string.Empty;
                var videoCodecProfileDetails = await GetCodecProfileDetailsByNameAsync(httpClient, baseUrl, webRoot, videoCodecProfileRef, cancellationToken).ConfigureAwait(false);
                var audioCodecProfileDetails = await GetCodecProfileDetailsByNameAsync(httpClient, baseUrl, webRoot, audioCodecProfileRef, cancellationToken).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(rawVideoCodec) && !string.IsNullOrWhiteSpace(videoCodecProfileDetails?.CodecClass))
                {
                    rawVideoCodec = videoCodecProfileDetails.CodecClass;
                }

                if (string.IsNullOrWhiteSpace(rawAudioCodec) && !string.IsNullOrWhiteSpace(audioCodecProfileDetails?.CodecClass))
                {
                    rawAudioCodec = audioCodecProfileDetails.CodecClass;
                }

                result.IsTranscodeProfile = true;
                result.Container = MapContainer(GetStringProp(entry, "container") ?? GetIntProp(entry, "container")?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
                result.VideoCodecProfile = videoCodecProfileRef;
                result.AudioCodecProfile = audioCodecProfileRef;
                result.VideoCodec = ResolveVideoCodec(rawVideoCodec, videoCodecProfileRef);
                result.AudioCodec = ResolveAudioCodec(rawAudioCodec, audioCodecProfileRef);
                // Prefer codec-profile deinterlace flag because stream profile itself often doesn't carry it.
                var deinterlaceEnabled = videoCodecProfileDetails?.Deinterlace ?? GetBoolProp(entry, "deinterlace") ?? false;
                result.VideoIsInterlaced = !deinterlaceEnabled;

                var streamVideoBitrate = GetIntProp(entry, "vbitrate") ?? 0;
                var streamAudioBitrate = GetIntProp(entry, "abitrate") ?? 0;
                result.VideoBitrate = (streamVideoBitrate > 0 ? streamVideoBitrate : videoCodecProfileDetails?.BitRateKbps ?? 0) * 1000;
                result.AudioBitrate = (streamAudioBitrate > 0 ? streamAudioBitrate : audioCodecProfileDetails?.BitRateKbps ?? 0) * 1000;
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
        var configuredStreamProfileExists = false;
        var configuredDvrProfileExists = false;
        var dvrProfileNames = new List<string>();

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

                var matchingStreamProfile = profList?.Entries?.FirstOrDefault(e =>
                    string.Equals(e.Val, config.StreamingProfile, StringComparison.OrdinalIgnoreCase));

                configuredStreamProfileExists = matchingStreamProfile != null;

                if (matchingStreamProfile != null)
                {
                    try
                    {
                        using var profileDoc = await LoadIdNodeByUuidAsync(httpClient, baseUrl, webRoot, matchingStreamProfile.Key, cancellationToken).ConfigureAwait(false);
                        if (profileDoc.RootElement.TryGetProperty("entries", out var entries) && entries.GetArrayLength() > 0)
                        {
                            var entry = entries[0];
                            var profileClass = GetStringProp(entry, "class") ?? "unknown";
                            var container = MapContainer(GetStringProp(entry, "container") ?? GetIntProp(entry, "container")?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
                            var proVideoCodec = GetStringProp(entry, "pro_vcodec") ?? string.Empty;
                            var proAudioCodec = GetStringProp(entry, "pro_acodec") ?? string.Empty;
                            var srcVideoCodecs = GetStringArrayProp(entry, "src_vcodec");
                            var srcAudioCodecs = GetStringArrayProp(entry, "src_acodec");
                            var srcSubtitleCodecs = GetStringArrayProp(entry, "src_scodec");
                            var deinterlace = GetBoolProp(entry, "deinterlace");

                            report.PluginSettings.Add($"TVH stream profile details: class={profileClass}, container={(string.IsNullOrWhiteSpace(container) ? "(unknown)" : container)}");
                            report.PluginSettings.Add($"TVH codec links: video={(string.IsNullOrWhiteSpace(proVideoCodec) ? "(not linked)" : proVideoCodec)}, audio={(string.IsNullOrWhiteSpace(proAudioCodec) ? "(not linked)" : proAudioCodec)}");
                            report.PluginSettings.Add($"TVH source codec filters: video={srcVideoCodecs.Count}, audio={srcAudioCodecs.Count}, subtitles={srcSubtitleCodecs.Count}");

                            if (config.EnableFastChannelSwitching && !profileClass.Contains("transcode", StringComparison.OrdinalIgnoreCase))
                            {
                                report.Warnings.Add($"Configured stream profile '{config.StreamingProfile}' is not a transcode profile.");
                            }

                            if (config.EnableFastChannelSwitching && string.IsNullOrWhiteSpace(proVideoCodec))
                            {
                                report.Warnings.Add($"Stream profile '{config.StreamingProfile}' is missing a linked video codec profile (pro_vcodec).");
                            }

                            if (config.EnableFastChannelSwitching && string.IsNullOrWhiteSpace(proAudioCodec))
                            {
                                report.Warnings.Add($"Stream profile '{config.StreamingProfile}' is missing a linked audio codec profile (pro_acodec).");
                            }

                            if (config.EnableFastChannelSwitching && srcVideoCodecs.Count == 0)
                            {
                                report.Warnings.Add($"Stream profile '{config.StreamingProfile}' has no allowed source video codecs (src_vcodec is empty).");
                            }

                            if (config.EnableFastChannelSwitching && srcAudioCodecs.Count == 0)
                            {
                                report.Warnings.Add($"Stream profile '{config.StreamingProfile}' has no allowed source audio codecs (src_acodec is empty).");
                            }

                            if (config.EnableFastChannelSwitching && deinterlace == false)
                            {
                                report.Warnings.Add($"Stream profile '{config.StreamingProfile}' does not enable deinterlacing. Interlaced output often forces Jellyfin transcoding.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        report.Warnings.Add($"Could not inspect stream profile '{config.StreamingProfile}': {ex.Message}");
                    }
                }

                if (!string.IsNullOrWhiteSpace(config.StreamingProfile) && !configuredStreamProfileExists)
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

                try
                {
                    using var dvrProfilesDoc = await LoadDvrConfigsAsync(httpClient, baseUrl, webRoot, cancellationToken).ConfigureAwait(false);
                    if (dvrProfilesDoc.RootElement.TryGetProperty("entries", out var dvrEntries) && dvrEntries.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in dvrEntries.EnumerateArray())
                        {
                            var name = GetStringProp(entry, "name")
                                ?? GetStringProp(entry, "text")
                                ?? GetStringProp(entry, "val")
                                ?? string.Empty;

                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                dvrProfileNames.Add(name);
                            }
                        }

                        configuredDvrProfileExists = dvrProfileNames.Any(name => string.Equals(name, config.RecordingProfile, StringComparison.OrdinalIgnoreCase));
                        report.PluginSettings.Add($"TVH DVR profiles: {(dvrProfileNames.Count == 0 ? "(none found)" : string.Join(", ", dvrProfileNames))}");

                        if (!string.IsNullOrWhiteSpace(config.RecordingProfile) && !configuredDvrProfileExists)
                        {
                            report.Warnings.Add($"Configured DVR profile '{config.RecordingProfile}' does not exist in TVHeadend.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    report.Warnings.Add($"Could not inspect DVR profiles: {ex.Message}");
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

        if (config.EnableTvhDvr && !string.IsNullOrWhiteSpace(config.RecordingProfile) && !configuredDvrProfileExists)
        {
            report.Recommendations.Add($"Recording profile '{config.RecordingProfile}' is configured in the plugin but was not found in TVHeadend. Check your TVH DVR configuration profiles.");
        }

        if (config.EnableFastChannelSwitching && configuredStreamProfileExists && string.Equals(config.StreamingProfile, "pass", StringComparison.OrdinalIgnoreCase))
        {
            report.Recommendations.Add("Fast Channel Switching is enabled but the streaming profile is 'pass'. Use a fixed transcode profile such as 'jellyfin' instead.");
        }

        if (config.EnableFastChannelSwitching && report.Warnings.Any(w => w.Contains("pro_vcodec", StringComparison.OrdinalIgnoreCase) || w.Contains("pro_acodec", StringComparison.OrdinalIgnoreCase)))
        {
            report.Recommendations.Add("Re-run 'Enable Fast Channel Switching (One-Click Setup)' to re-link the TVHeadend streaming profile to its codec profiles.");
        }

        if (config.EnableFastChannelSwitching && report.Warnings.Any(w => w.Contains("deinterlacing", StringComparison.OrdinalIgnoreCase)))
        {
            report.Recommendations.Add("Enable deinterlacing in the TVHeadend video codec profile so Jellyfin clients can more often direct play the output.");
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
                    ["description"] = "Auto-created by Jellyfin plugin. H.264 with 5 Mbps cap and repeated SPS/PPS headers for fast channel switching.",
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
                    ["params"] = "repeat-headers=1:aud=1",
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

            // Explicitly link the stream profile to codec profiles via idnode/save.
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
                VideoIsInterlaced = false,
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
    /// One-click Fast Channel Switching setup.
    /// Creates all required TVHeadend profiles (codec + streaming) and configures the
    /// Jellyfin plugin for optimal fast channel switching in a single operation.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A status message indicating what was done.</returns>
    [HttpPost("SetupFastSwitching")]
    public async Task<ActionResult<ProfileDetectionResult>> SetupFastSwitching(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin?.Configuration == null)
        {
            return BadRequest(new ProfileDetectionResult { Success = false, Message = "Plugin configuration is not available." });
        }

        var config = plugin.Configuration;
        var steps = new List<string>();

        try
        {
            using var httpClient = BuildHttpClient(config);
            var baseUrl = $"{(config.UseSSL ? "https" : "http")}://{config.Host}:{config.Port}";
            var webRoot = string.IsNullOrWhiteSpace(config.Webroot) ? "/" : config.Webroot.TrimEnd('/') + "/";

            // ── 1. Verify connectivity ──────────────────────────────────
            try
            {
                var infoUrl = $"{baseUrl}{webRoot}api/serverinfo";
                await httpClient.GetStringAsync(infoUrl, cancellationToken).ConfigureAwait(false);
                steps.Add("TVHeadend connection OK");
            }
            catch (HttpRequestException ex)
            {
                return Ok(new ProfileDetectionResult
                {
                    Success = false,
                    Message = $"Cannot reach TVHeadend at {config.Host}:{config.Port}: {ex.Message}. Configure connection settings first."
                });
            }

            // ── 2. Create video codec profile ──────────────────────────
            const string videoCodecProfileName = "jellyfin-h264";
            var videoCodecOk = await CodecProfileExistsAsync(httpClient, baseUrl, webRoot, videoCodecProfileName, cancellationToken).ConfigureAwait(false);

            if (!videoCodecOk)
            {
                var videoConf = new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = videoCodecProfileName,
                    ["description"] = "Auto-created by Jellyfin plugin. H.264 with 5 Mbps cap and repeated SPS/PPS headers for fast channel switching.",
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
                    ["params"] = "repeat-headers=1:aud=1",
                };

                videoCodecOk = await CreateCodecProfileAsync(httpClient, baseUrl, webRoot, "libx264", videoConf, cancellationToken).ConfigureAwait(false);
                steps.Add(videoCodecOk ? "Created codec profile 'jellyfin-h264' (H.264, 5 Mbps)" : "WARNING: Could not create video codec profile");
            }
            else
            {
                steps.Add("Codec profile 'jellyfin-h264' already exists");
            }

            // ── 3. Create audio codec profile ──────────────────────────
            const string audioCodecProfileName = "jellyfin-aac";
            var audioCodecOk = await CodecProfileExistsAsync(httpClient, baseUrl, webRoot, audioCodecProfileName, cancellationToken).ConfigureAwait(false);

            if (!audioCodecOk)
            {
                var audioConf = new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = audioCodecProfileName,
                    ["description"] = "Auto-created by Jellyfin plugin. AAC 128 kbps for fast channel switching.",
                    ["bit_rate"] = 128,
                    ["profile"] = -99,
                };

                audioCodecOk = await CreateCodecProfileAsync(httpClient, baseUrl, webRoot, "aac", audioConf, cancellationToken).ConfigureAwait(false);
                steps.Add(audioCodecOk ? "Created codec profile 'jellyfin-aac' (AAC, 128 kbps)" : "WARNING: Could not create audio codec profile");
            }
            else
            {
                steps.Add("Codec profile 'jellyfin-aac' already exists");
            }

            // ── 4. Create streaming profile ────────────────────────────
            var listUrl = $"{baseUrl}{webRoot}api/profile/list";
            var listResponse = await httpClient.GetStringAsync(listUrl, cancellationToken).ConfigureAwait(false);
            var profileList = JsonSerializer.Deserialize<ProfileListResponse>(listResponse, JsonOptions);

            var existingStreamProfile = profileList?.Entries?.FirstOrDefault(e =>
                string.Equals(e.Val, "jellyfin", StringComparison.OrdinalIgnoreCase));

            if (existingStreamProfile != null)
            {
                steps.Add("Streaming profile 'jellyfin' already exists");
            }
            else
            {
                var useVideoCodec = videoCodecOk ? videoCodecProfileName : "libx264";
                var useAudioCodec = audioCodecOk ? audioCodecProfileName : "aac";

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

                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("class", "profile-transcode"),
                    new KeyValuePair<string, string>("conf", confNode.ToJsonString())
                });

                var createUrl = $"{baseUrl}{webRoot}api/profile/create";
                var response = await httpClient.PostAsync(createUrl, content, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
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
                    steps.Add("Created streaming profile 'jellyfin' (MPEG-TS)");
                }
                else
                {
                    var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    return Ok(new ProfileDetectionResult
                    {
                        Success = false,
                        Message = "TVHeadend profiles partially created but streaming profile failed (HTTP "
                            + $"{(int)response.StatusCode}). Check admin privileges. Response: {errorBody}"
                    });
                }
            }

            // Explicitly link the stream profile to codec profiles via idnode/save.
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

                steps.Add(linked
                    ? "Streaming profile linked to codec profiles"
                    : "WARNING: could not explicitly link streaming profile to codec profiles");
            }
            else
            {
                steps.Add("WARNING: could not resolve streaming profile UUID for codec linking");
            }

            // ── 5. Update and save plugin configuration ────────────────
            config.StreamingProfile = "jellyfin";
            config.EnableFastChannelSwitching = true;
            config.SupportsProbing = false;
            config.SupportsDirectPlay = true;
            config.SupportsDirectStream = true;
            config.SupportsTranscoding = false;
            config.IgnoreDts = false;
            config.IsInfiniteStream = true;
            config.StreamContainer = "mpegts";
            config.VideoCodec = "h264";
            config.AudioCodec = "aac";
            config.VideoBitrate = 5000000;
            config.AudioBitrate = 128000;
            config.AudioChannels = 2;
            config.AudioSampleRate = 48000;
            config.VideoFramerate = 25;
            config.VideoIsInterlaced = false;
            config.BufferMs = 0;
            config.AnalyzeDurationMs = 0;

            plugin.SaveConfiguration();
            steps.Add("Plugin configuration saved (Fast Channel Switching enabled)");

            _logger.LogInformation("Fast Switching setup complete: {Steps}", string.Join("; ", steps));

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
                AudioChannels = 2,
                VideoHeight = 0,
                VideoIsInterlaced = false,
                VideoCodecProfile = videoCodecProfileName,
                AudioCodecProfile = audioCodecProfileName,
                Message = string.Join(" → ", steps)
            });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to TVHeadend during fast switching setup.");
            return Ok(new ProfileDetectionResult
            {
                Success = false,
                Message = $"Cannot connect to TVHeadend: {ex.Message}. Check your connection and authentication settings."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during fast switching setup.");
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

    private async Task<CodecProfileListEntry?> GetCodecProfileByNameAsync(HttpClient httpClient, string baseUrl, string webRoot, string profileName, CancellationToken cancellationToken)
    {
        try
        {
            var listUrl = $"{baseUrl}{webRoot}api/codec_profile/list";
            var response = await httpClient.GetStringAsync(listUrl, cancellationToken).ConfigureAwait(false);
            var list = JsonSerializer.Deserialize<CodecProfileListResponse>(response, JsonOptions);
            return list?.Entries?.FirstOrDefault(e => string.Equals(e.Val, profileName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve codec profile '{ProfileName}'.", profileName);
            return null;
        }
    }

    private async Task<CodecProfileDetails?> GetCodecProfileDetailsByNameAsync(HttpClient httpClient, string baseUrl, string webRoot, string profileName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return null;
        }

        var codecProfile = await GetCodecProfileByNameAsync(httpClient, baseUrl, webRoot, profileName, cancellationToken).ConfigureAwait(false);
        if (codecProfile == null)
        {
            return null;
        }

        try
        {
            using var doc = await LoadIdNodeByUuidAsync(httpClient, baseUrl, webRoot, codecProfile.Key, cancellationToken).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.GetArrayLength() == 0)
            {
                return null;
            }

            var entry = entries[0];
            return new CodecProfileDetails
            {
                CodecClass = GetStringProp(entry, "class") ?? string.Empty,
                BitRateKbps = GetIntProp(entry, "bit_rate") ?? 0,
                Deinterlace = GetBoolProp(entry, "deinterlace")
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load codec profile details for '{ProfileName}'.", profileName);
            return null;
        }
    }

    private async Task<ProfileListEntry?> GetStreamingProfileByNameAsync(HttpClient httpClient, string baseUrl, string webRoot, string profileName, CancellationToken cancellationToken)
    {
        try
        {
            var listUrl = $"{baseUrl}{webRoot}api/profile/list";
            var response = await httpClient.GetStringAsync(listUrl, cancellationToken).ConfigureAwait(false);
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
            var loadUrl = $"{baseUrl}{webRoot}api/idnode/load?uuid={streamProfileUuid}";
            var loadResponse = await httpClient.GetStringAsync(loadUrl, cancellationToken).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(loadResponse);
            if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.GetArrayLength() == 0)
            {
                _logger.LogWarning("Could not load stream profile UUID {Uuid} before linking codec profiles.", streamProfileUuid);
                return false;
            }

            var entry = entries[0];
            var saveUrl = $"{baseUrl}{webRoot}api/idnode/save";
            var sourceVideoCodecs = GetStringArrayProp(entry, "src_vcodec");
            var sourceAudioCodecs = GetStringArrayProp(entry, "src_acodec");
            var sourceSubtitleCodecs = GetStringArrayProp(entry, "src_scodec");
            var node = new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = GetStringProp(entry, "name") ?? "jellyfin",
                ["enabled"] = GetBoolProp(entry, "enabled") ?? true,
                ["default"] = GetBoolProp(entry, "default") ?? false,
                ["comment"] = GetStringProp(entry, "comment") ?? string.Empty,
                ["timeout"] = GetIntProp(entry, "timeout") ?? 0,
                ["timeout_start"] = GetIntProp(entry, "timeout_start") ?? 0,
                ["priority"] = GetIntProp(entry, "priority") ?? 0,
                ["fpriority"] = GetIntProp(entry, "fpriority") ?? 0,
                ["restart"] = GetBoolProp(entry, "restart") ?? false,
                ["contaccess"] = GetBoolProp(entry, "contaccess") ?? true,
                ["catimeout"] = GetIntProp(entry, "catimeout") ?? 2000,
                ["swservice"] = GetBoolProp(entry, "swservice") ?? true,
                ["svfilter"] = GetIntProp(entry, "svfilter") ?? 0,
                ["container"] = GetIntProp(entry, "container") ?? 2,
                ["pro_vcodec"] = videoCodecRef,
                ["src_vcodec"] = ToJsonArray(sourceVideoCodecs.Count > 0 ? sourceVideoCodecs : DefaultSourceVideoCodecs),
                ["pro_acodec"] = audioCodecRef,
                ["src_acodec"] = ToJsonArray(sourceAudioCodecs.Count > 0 ? sourceAudioCodecs : DefaultSourceAudioCodecs),
                ["pro_scodec"] = GetStringProp(entry, "pro_scodec") ?? string.Empty,
                ["src_scodec"] = ToJsonArray(sourceSubtitleCodecs),
                ["uuid"] = streamProfileUuid,
            };

            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("node", node.ToJsonString())
            });

            var response = await httpClient.PostAsync(saveUrl, content, cancellationToken).ConfigureAwait(false);
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

    private static async Task<JsonDocument> LoadIdNodeByUuidAsync(HttpClient httpClient, string baseUrl, string webRoot, string uuid, CancellationToken cancellationToken)
    {
        var loadUrl = $"{baseUrl}{webRoot}api/idnode/load?uuid={Uri.EscapeDataString(uuid)}";
        var response = await httpClient.GetStringAsync(loadUrl, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(response);
    }

    private static async Task<JsonDocument> LoadDvrConfigsAsync(HttpClient httpClient, string baseUrl, string webRoot, CancellationToken cancellationToken)
    {
        var loadUrl = $"{baseUrl}{webRoot}api/idnode/load";
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("enum", "1"),
            new KeyValuePair<string, string>("class", "dvrconfig")
        });

        using var response = await httpClient.PostAsync(loadUrl, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(body);
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

    private static bool? GetBoolProp(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (prop.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var intVal))
        {
            return intVal != 0;
        }

        if (prop.ValueKind == JsonValueKind.String)
        {
            var strVal = prop.GetString();
            if (bool.TryParse(strVal, out var boolVal))
            {
                return boolVal;
            }

            if (int.TryParse(strVal, out var parsedInt))
            {
                return parsedInt != 0;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> GetStringArrayProp(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var values = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            var value = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
            }
        }

        return values;
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

    private static string ResolveVideoCodec(string rawCodec, string codecProfileRef)
    {
        var mapped = MapVideoCodec(rawCodec);
        if (!string.IsNullOrWhiteSpace(mapped) && !string.Equals(mapped, rawCodec, StringComparison.OrdinalIgnoreCase))
        {
            return mapped;
        }

        var profile = codecProfileRef.ToLowerInvariant();
        if (profile.Contains("264", StringComparison.Ordinal) || profile.Contains("avc", StringComparison.Ordinal))
        {
            return "h264";
        }

        if (profile.Contains("265", StringComparison.Ordinal) || profile.Contains("hevc", StringComparison.Ordinal))
        {
            return "hevc";
        }

        if (profile.Contains("mpeg2", StringComparison.Ordinal))
        {
            return "mpeg2video";
        }

        return mapped;
    }

    private static string ResolveAudioCodec(string rawCodec, string codecProfileRef)
    {
        var mapped = MapAudioCodec(rawCodec);
        if (!string.IsNullOrWhiteSpace(mapped) && !string.Equals(mapped, rawCodec, StringComparison.OrdinalIgnoreCase))
        {
            return mapped;
        }

        var profile = codecProfileRef.ToLowerInvariant();
        if (profile.Contains("aac", StringComparison.Ordinal))
        {
            return "aac";
        }

        if (profile.Contains("ac3", StringComparison.Ordinal) || profile.Contains("a52", StringComparison.Ordinal))
        {
            return "ac3";
        }

        if (profile.Contains("eac3", StringComparison.Ordinal))
        {
            return "eac3";
        }

        if (profile.Contains("opus", StringComparison.Ordinal))
        {
            return "opus";
        }

        if (profile.Contains("mp3", StringComparison.Ordinal))
        {
            return "mp3";
        }

        return mapped;
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

    private sealed class CodecProfileDetails
    {
        public string CodecClass { get; init; } = string.Empty;

        public int BitRateKbps { get; init; }

        public bool? Deinterlace { get; init; }
    }
}
