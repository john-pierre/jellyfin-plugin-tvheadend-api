using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Configuration;
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

    private static readonly string[] AvailableTestProfiles =
    {
        "pass",
        "jellyfin",
        "jellyfin-ultrafast",
        "jellyfin-fast",
        "jellyfin-balanced"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<TvHeadendApiController> _logger;
    private readonly IServerConfigurationManager _serverConfigManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvHeadendApiController"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="serverConfigManager">Jellyfin server configuration manager for accessing global encoding options.</param>
    public TvHeadendApiController(
        ILogger<TvHeadendApiController> logger,
        IServerConfigurationManager serverConfigManager)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serverConfigManager = serverConfigManager ?? throw new ArgumentNullException(nameof(serverConfigManager));
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
    /// for diagnostic purposes (profile type, container, codecs, bitrate, etc.).
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
                    + "This is NOT a transcode profile � the output format varies per channel. "
                    + "The plugin will query TVHeadend for stream details per channel at runtime.";
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
    /// Performs a comprehensive system diagnostic: tests TVHeadend connectivity, measures
    /// latency, reports server info, channel count, available profiles, DVR entries,
    /// validates configuration for best compatibility, and returns a scored result
    /// with per-category checks and recommendations.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A structured diagnostic report with compatibility score.</returns>
    [HttpGet("Diagnose")]
    public async Task<ActionResult<DiagnoseResult>> Diagnose(CancellationToken cancellationToken)
    {
        var report = new DiagnoseResult();
        var config = Plugin.Instance?.Configuration;
        var configuredStreamProfileExists = false;
        var configuredDvrProfileExists = false;
        var dvrProfileNames = new List<string>();
        var scoreDeductions = 0;

        if (config == null)
        {
            report.Connection = "? Plugin configuration is not available.";
            report.OverallStatus = "ERROR";
            report.CompatibilityScore = 0;
            return Ok(report);
        }

        // -- Plugin config summary --------------------------------------
        report.PluginSettings.Add($"Streaming Profile: {(string.IsNullOrWhiteSpace(config.StreamingProfile) ? "(not set)" : config.StreamingProfile)}");
        report.PluginSettings.Add($"Direct Play: {config.SupportsDirectPlay}, Direct Stream: {config.SupportsDirectStream}, Transcoding: {config.SupportsTranscoding}");
        report.PluginSettings.Add($"Probing: {config.SupportsProbing}, Infinite Stream: {config.IsInfiniteStream}, Ignore DTS: {config.IgnoreDts}");

        // -- Analyze Duration / Probing details -------------------------
        var effectiveAnalyzeDurationMs = config.AnalyzeDurationMs > 0 ? config.AnalyzeDurationMs : 200;
        var ffmpegMicroseconds = effectiveAnalyzeDurationMs * 1000;
        report.PluginSettings.Add(
            config.AnalyzeDurationMs > 0
                ? $"AnalyzeDuration: {config.AnalyzeDurationMs} ms (explicit) ? ffmpeg receives -analyzeduration {ffmpegMicroseconds} �s"
                : $"AnalyzeDuration: 0 (auto) ? plugin will default to 200 ms when stream details are available ? ffmpeg receives -analyzeduration 200000 �s. When probing, Jellyfin's global FFmpeg analyzeduration is used.");
        report.PluginSettings.Add($"BufferMs: {(config.BufferMs > 0 ? $"{config.BufferMs} ms" : "0 (Jellyfin default)")}");

        // -- Jellyfin global FFmpeg settings (via reflection) ------------
        var (jellyfinProbeSize, jellyfinAnalyzeDuration) = ReadJellyfinFfmpegSettings();
        var probeSizeDisplay = !string.IsNullOrWhiteSpace(jellyfinProbeSize)
            ? $"{jellyfinProbeSize} (from config or environment)"
            : "(not set / using Jellyfin default)";
        report.PluginSettings.Add($"Jellyfin FFmpeg ProbeSize: {probeSizeDisplay}");
        report.PluginSettings.Add($"Jellyfin FFmpeg AnalyzeDuration: {jellyfinAnalyzeDuration ?? "(not set / default)"}");

        report.PluginSettings.Add($"DVR enabled: {config.EnableTvhDvr}, Recording Profile: {config.RecordingProfile}");

        // -- Connectivity test with latency measurement ------------------
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
            report.Connection = $"? Failed to build HTTP client: {ex.Message}";
            report.OverallStatus = "ERROR";
            report.CompatibilityScore = 0;
            report.Checks.Add(new DiagnoseCheck { Category = "Connection", Name = "HTTP Client", Status = "ERROR", Message = ex.Message });
            return Ok(report);
        }

        var allChannelUuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (httpClient)
        {
            // Test basic connectivity with serverinfo + measure latency
            try
            {
                var infoUrl = $"{baseUrl}{webRoot}api/serverinfo";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var infoResponse = await httpClient.GetStringAsync(infoUrl, cancellationToken).ConfigureAwait(false);
                sw.Stop();
                report.LatencyMs = (int)sw.ElapsedMilliseconds;

                using var infoDoc = JsonDocument.Parse(infoResponse);
                var root = infoDoc.RootElement;

                var swVersion = GetStringProp(root, "sw_version") ?? "unknown";
                var apiVersion = GetIntProp(root, "api_version")?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";
                var serverName = GetStringProp(root, "name") ?? string.Empty;

                report.Connection = $"? Connected to {(string.IsNullOrEmpty(serverName) ? config.Host : serverName)}";
                report.ServerVersion = $"TVHeadend {swVersion} (API v{apiVersion})";

                report.Checks.Add(new DiagnoseCheck
                {
                    Category = "Connection",
                    Name = "TVHeadend Connectivity",
                    Status = "OK",
                    Message = $"Connected in {report.LatencyMs}ms to {config.Host}:{config.Port}"
                });

                // API version check
                var apiVer = GetIntProp(root, "api_version") ?? 0;
                if (apiVer >= 19)
                {
                    report.Checks.Add(new DiagnoseCheck { Category = "Connection", Name = "API Version", Status = "OK", Message = $"API v{apiVer} is supported." });
                }
                else
                {
                    report.Checks.Add(new DiagnoseCheck { Category = "Connection", Name = "API Version", Status = "WARNING", Message = $"API v{apiVer} is old. v19+ recommended.", Recommendation = "Consider updating TVHeadend." });
                    scoreDeductions += 5;
                }
            }
            catch (HttpRequestException ex)
            {
                report.Connection = $"? Cannot reach TVHeadend at {config.Host}:{config.Port} � {ex.Message}";
                report.Checks.Add(new DiagnoseCheck { Category = "Connection", Name = "TVHeadend Connectivity", Status = "ERROR", Message = ex.Message, Recommendation = "Check host, port, and network connectivity." });
                report.OverallStatus = "ERROR";
                report.CompatibilityScore = 0;
                return Ok(report);
            }
            catch (Exception ex)
            {
                report.Connection = $"?? Connected but serverinfo failed: {ex.Message}";
                report.Checks.Add(new DiagnoseCheck { Category = "Connection", Name = "Server Info", Status = "WARNING", Message = ex.Message });
                scoreDeductions += 10;
            }

            // Channel count + collect UUIDs for probe cache check later
            try
            {
                var chUrl = $"{baseUrl}{webRoot}api/channel/grid?limit=500&sort=number";
                var chResponse = await httpClient.GetStringAsync(chUrl, cancellationToken).ConfigureAwait(false);
                using var chDoc = JsonDocument.Parse(chResponse);
                if (chDoc.RootElement.TryGetProperty("total", out var totalProp) && totalProp.TryGetInt32(out var total))
                {
                    report.ChannelCount = total;
                }

                if (chDoc.RootElement.TryGetProperty("entries", out var chEntries) && chEntries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var chEntry in chEntries.EnumerateArray())
                    {
                        var uuid = GetStringProp(chEntry, "uuid");
                        if (!string.IsNullOrWhiteSpace(uuid))
                        {
                            allChannelUuids.Add(uuid);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                report.Warnings.Add($"Could not fetch channel count: {ex.Message}");
            }

            // Streaming profiles
            string? profileClass = null;
            try
            {
                var profUrl = $"{baseUrl}{webRoot}api/profile/list";
                var profResponse = await httpClient.GetStringAsync(profUrl, cancellationToken).ConfigureAwait(false);
                var profList = JsonSerializer.Deserialize<ProfileListResponse>(profResponse, JsonOptions);
                var profiles = profList?.Entries?.Select(e => e.Val).ToList() ?? new List<string>();
                foreach (var p in profiles)
                {
                    report.AvailableStreamingProfiles.Add(p);
                }

                var matchingStreamProfile = profList?.Entries?.FirstOrDefault(e =>
                    string.Equals(e.Val, config.StreamingProfile, StringComparison.OrdinalIgnoreCase));

                configuredStreamProfileExists = matchingStreamProfile != null;

                if (configuredStreamProfileExists)
                {
                    report.Checks.Add(new DiagnoseCheck { Category = "Streaming", Name = "Profile Exists", Status = "OK", Message = $"Streaming profile '{config.StreamingProfile}' found in TVHeadend." });
                }
                else if (!string.IsNullOrWhiteSpace(config.StreamingProfile))
                {
                    report.Checks.Add(new DiagnoseCheck
                    {
                        Category = "Streaming",
                        Name = "Profile Exists",
                        Status = "ERROR",
                        Message = $"Streaming profile '{config.StreamingProfile}' not found in TVHeadend.",
                        Recommendation = $"Available profiles: {string.Join(", ", profiles)}. Set one of these in the Streaming Profile field."
                    });
                    scoreDeductions += 30;
                }

                if (matchingStreamProfile != null)
                {
                    try
                    {
                        using var profileDoc = await LoadIdNodeByUuidAsync(httpClient, baseUrl, webRoot, matchingStreamProfile.Key, cancellationToken).ConfigureAwait(false);
                        if (profileDoc.RootElement.TryGetProperty("entries", out var entries) && entries.GetArrayLength() > 0)
                        {
                            var entry = entries[0];
                            profileClass = GetStringProp(entry, "class") ?? "unknown";
                            var container = MapContainer(GetStringProp(entry, "container") ?? GetIntProp(entry, "container")?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
                            var proVideoCodec = GetStringProp(entry, "pro_vcodec") ?? string.Empty;
                            var proAudioCodec = GetStringProp(entry, "pro_acodec") ?? string.Empty;
                            var srcVideoCodecs = GetStringArrayProp(entry, "src_vcodec");
                            var srcAudioCodecs = GetStringArrayProp(entry, "src_acodec");
                            var deinterlace = GetBoolProp(entry, "deinterlace");

                            report.PluginSettings.Add($"TVH stream profile: class={profileClass}, container={(string.IsNullOrWhiteSpace(container) ? "(unknown)" : container)}");
                            report.PluginSettings.Add($"TVH codec links: video={(string.IsNullOrWhiteSpace(proVideoCodec) ? "(not linked)" : proVideoCodec)}, audio={(string.IsNullOrWhiteSpace(proAudioCodec) ? "(not linked)" : proAudioCodec)}");

                            // Profile type check
                            if (profileClass.Contains("transcode", StringComparison.OrdinalIgnoreCase))
                            {
                                report.Checks.Add(new DiagnoseCheck { Category = "Streaming", Name = "Profile Type", Status = "OK", Message = $"Transcode profile detected. Output format is fixed per channel." });

                                // Codec links check
                                if (!string.IsNullOrWhiteSpace(proVideoCodec) && !string.IsNullOrWhiteSpace(proAudioCodec))
                                {
                                    report.Checks.Add(new DiagnoseCheck { Category = "Streaming", Name = "Codec Profiles", Status = "OK", Message = $"Video: {proVideoCodec}, Audio: {proAudioCodec}" });
                                }
                                else
                                {
                                    if (string.IsNullOrWhiteSpace(proVideoCodec))
                                    {
                                        report.Checks.Add(new DiagnoseCheck { Category = "Streaming", Name = "Video Codec Link", Status = "WARNING", Message = "No linked video codec profile (pro_vcodec).", Recommendation = "Link a video codec profile in TVHeadend for consistent output." });
                                        scoreDeductions += 5;
                                    }

                                    if (string.IsNullOrWhiteSpace(proAudioCodec))
                                    {
                                        report.Checks.Add(new DiagnoseCheck { Category = "Streaming", Name = "Audio Codec Link", Status = "WARNING", Message = "No linked audio codec profile (pro_acodec).", Recommendation = "Link an audio codec profile in TVHeadend for consistent output." });
                                        scoreDeductions += 5;
                                    }
                                }

                                // Deinterlacing check
                                if (deinterlace == true)
                                {
                                    report.Checks.Add(new DiagnoseCheck { Category = "Streaming", Name = "Deinterlacing", Status = "OK", Message = "Deinterlacing is enabled." });
                                }
                                else
                                {
                                    report.Checks.Add(new DiagnoseCheck { Category = "Streaming", Name = "Deinterlacing", Status = "WARNING", Message = "Deinterlacing is not enabled.", Recommendation = "Enable deinterlacing in the TVHeadend video codec profile so clients can more often direct play." });
                                    scoreDeductions += 5;
                                }

                                // Source codec filter check
                                if (srcVideoCodecs.Count == 0)
                                {
                                    report.Checks.Add(new DiagnoseCheck { Category = "Streaming", Name = "Source Video Codecs", Status = "INFO", Message = "No source video codec filter set (all codecs accepted)." });
                                }

                                if (srcAudioCodecs.Count == 0)
                                {
                                    report.Checks.Add(new DiagnoseCheck { Category = "Streaming", Name = "Source Audio Codecs", Status = "INFO", Message = "No source audio codec filter set (all codecs accepted)." });
                                }
                            }
                            else
                            {
                                report.Checks.Add(new DiagnoseCheck { Category = "Streaming", Name = "Profile Type", Status = "INFO", Message = $"Pass-through profile ({profileClass}). Output format varies per channel. Plugin queries TVHeadend for stream details at runtime." });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        report.Warnings.Add($"Could not inspect stream profile '{config.StreamingProfile}': {ex.Message}");
                    }
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

                        // Add DVR profiles to the report
                        foreach (var profile in dvrProfileNames)
                        {
                            report.AvailableRecordingProfiles.Add(profile);
                        }

                        if (!string.IsNullOrWhiteSpace(config.RecordingProfile) && configuredDvrProfileExists)
                        {
                            report.Checks.Add(new DiagnoseCheck { Category = "Recording", Name = "DVR Profile", Status = "OK", Message = $"Recording profile '{config.RecordingProfile}' exists in TVHeadend." });
                        }
                        else if (!string.IsNullOrWhiteSpace(config.RecordingProfile) && !configuredDvrProfileExists)
                        {
                            report.Checks.Add(new DiagnoseCheck
                            {
                                Category = "Recording",
                                Name = "DVR Profile",
                                Status = "WARNING",
                                Message = $"Recording profile '{config.RecordingProfile}' not found in TVHeadend.",
                                Recommendation = $"Available DVR profiles: {string.Join(", ", dvrProfileNames)}."
                            });
                            scoreDeductions += 10;
                        }
                    }
                }
                catch (Exception ex)
                {
                    report.Warnings.Add($"Could not inspect DVR profiles: {ex.Message}");
                }
            }
        }

        // -- Playback behaviour checks ------------------------------------
        if (config.SupportsTranscoding && config.SupportsDirectPlay)
        {
            report.Checks.Add(new DiagnoseCheck
            {
                Category = "Playback",
                Name = "Direct Play + Transcoding",
                Status = "WARNING",
                Message = "Both Direct Play and Transcoding are enabled.",
                Recommendation = "For live TV, Direct Play alone is usually sufficient and faster."
            });
            scoreDeductions += 5;
        }
        else
        {
            report.Checks.Add(new DiagnoseCheck { Category = "Playback", Name = "Playback Mode", Status = "OK", Message = $"DirectPlay={config.SupportsDirectPlay}, DirectStream={config.SupportsDirectStream}, Transcoding={config.SupportsTranscoding}" });
        }

        if (config.SupportsProbing)
        {
            report.Checks.Add(new DiagnoseCheck
            {
                Category = "Playback",
                Name = "Stream Probing",
                Status = "INFO",
                Message = "Probing is enabled. Channel switching may be slower due to stream analysis.",
                Recommendation = "Disable probing for faster channel switching. The plugin queries TVHeadend for stream details automatically."
            });
            scoreDeductions += 10;
        }
        else
        {
            report.Checks.Add(new DiagnoseCheck { Category = "Playback", Name = "Stream Probing", Status = "OK", Message = "Probing disabled. Plugin provides stream details from TVHeadend or uses AnalyzeDuration as fallback." });
        }

        // AnalyzeDuration checks
        if (config.AnalyzeDurationMs > 1000)
        {
            report.Checks.Add(new DiagnoseCheck
            {
                Category = "Playback",
                Name = "AnalyzeDuration",
                Status = "WARNING",
                Message = $"AnalyzeDuration is {config.AnalyzeDurationMs}ms ({config.AnalyzeDurationMs * 1000}�s) which is very high.",
                Recommendation = "Consider 200ms or less for faster channel switching."
            });
            scoreDeductions += 15;
        }
        else if (config.AnalyzeDurationMs > 0 && config.AnalyzeDurationMs < 50)
        {
            report.Checks.Add(new DiagnoseCheck
            {
                Category = "Playback",
                Name = "AnalyzeDuration",
                Status = "WARNING",
                Message = $"AnalyzeDuration is {config.AnalyzeDurationMs}ms which is very low.",
                Recommendation = "FFmpeg may not detect all streams. Recommended minimum: 100ms."
            });
            scoreDeductions += 5;
        }
        else
        {
            var effectiveMs = config.AnalyzeDurationMs > 0 ? config.AnalyzeDurationMs : 200;
            report.Checks.Add(new DiagnoseCheck { Category = "Playback", Name = "AnalyzeDuration", Status = "OK", Message = $"Effective AnalyzeDuration: {effectiveMs}ms (good for fast channel switching)." });
        }

        if (!config.SupportsProbing && config.AnalyzeDurationMs == 0)
        {
            report.Recommendations.Add("Probing is off and AnalyzeDuration is 0. The plugin will query TVHeadend for stream details and default to 200ms. Consider setting an explicit AnalyzeDuration (e.g. 200ms) as fallback.");
        }

        // Buffer check
        if (config.BufferMs > 2000)
        {
            report.Checks.Add(new DiagnoseCheck
            {
                Category = "Playback",
                Name = "Buffer Size",
                Status = "WARNING",
                Message = $"Buffer is {config.BufferMs}ms which is quite high.",
                Recommendation = "Lower the buffer to reduce channel-switch latency."
            });
            scoreDeductions += 5;
        }
        else
        {
            report.Checks.Add(new DiagnoseCheck { Category = "Playback", Name = "Buffer Size", Status = "OK", Message = $"Buffer: {(config.BufferMs > 0 ? $"{config.BufferMs}ms" : "Jellyfin default")}" });
        }

        // -- FFmpeg global settings checks --------------------------------
        if (!string.IsNullOrWhiteSpace(jellyfinProbeSize))
        {
            if (int.TryParse(jellyfinProbeSize, out var probeSizeVal) && probeSizeVal <= 1000000)
            {
                report.Checks.Add(new DiagnoseCheck { Category = "FFmpeg", Name = "ProbeSize", Status = "OK", Message = $"Jellyfin FFmpeg ProbeSize: {jellyfinProbeSize} (good)." });
            }
            else
            {
                report.Checks.Add(new DiagnoseCheck
                {
                    Category = "FFmpeg",
                    Name = "ProbeSize",
                    Status = "WARNING",
                    Message = $"Jellyfin FFmpeg ProbeSize: {jellyfinProbeSize} (may be high).",
                    Recommendation = "Set ProbeSize to 1000000 or lower in Dashboard ? Playback ? FFmpeg for faster startup."
                });
                scoreDeductions += 5;
            }
        }
        else
        {
            report.Checks.Add(new DiagnoseCheck
            {
                Category = "FFmpeg",
                Name = "ProbeSize",
                Status = "INFO",
                Message = "Jellyfin FFmpeg ProbeSize: not set (using Jellyfin default).",
                Recommendation = "Set ProbeSize to 1000000 in Dashboard → Playback → FFmpeg, or via JELLYFIN_FFmpeg__ProbeSize environment variable for faster channel switching."
            });
        }

        if (!string.IsNullOrWhiteSpace(jellyfinAnalyzeDuration))
        {
            report.Checks.Add(new DiagnoseCheck { Category = "FFmpeg", Name = "AnalyzeDuration (global)", Status = "INFO", Message = $"Jellyfin FFmpeg AnalyzeDuration: {jellyfinAnalyzeDuration}" });
        }

        // -- General recommendations --------------------------------------
        if (string.IsNullOrWhiteSpace(config.StreamingProfile))
        {
            report.Recommendations.Add("Set a Streaming Profile. Use 'pass' for original quality or create a transcode profile for consistent output.");
        }

        if (report.ChannelCount == 0)
        {
            report.Recommendations.Add("No channels found. Make sure TVHeadend has scanned and mapped channels.");
        }

        // -- Probe cache coverage check ------------------------------------
        try
        {
            var cachePath = Plugin.Instance?.CachePath;
            var mediaInfoDir = !string.IsNullOrWhiteSpace(cachePath)
                ? System.IO.Path.Combine(cachePath, "mediainfo")
                : null;

            if (mediaInfoDir != null && Directory.Exists(mediaInfoDir) && allChannelUuids.Count > 0)
            {
                // Scan cache files and match channel UUIDs from the Path field
                var channelIdRegex = new System.Text.RegularExpressions.Regex(
                    @"stream/channel/([0-9a-f]{32})",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                var cachedChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var files = Directory.GetFiles(mediaInfoDir, "*.json");
                foreach (var file in files)
                {
                    try
                    {
                        var fileJson = await System.IO.File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                        using var fileDoc = JsonDocument.Parse(fileJson);
                        if (!fileDoc.RootElement.TryGetProperty("Path", out var pathEl))
                        {
                            continue;
                        }

                        var path = pathEl.GetString();
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            continue;
                        }

                        var match = channelIdRegex.Match(path);
                        if (match.Success && allChannelUuids.Contains(match.Groups[1].Value))
                        {
                            cachedChannels.Add(match.Groups[1].Value);
                        }
                    }
                    catch
                    {
                        // Skip unreadable files.
                    }
                }

                int cachedCount = cachedChannels.Count;
                report.CacheStatus = $"{cachedCount}/{allChannelUuids.Count} channels have a Jellyfin mediainfo probe cache file.";

                var cacheStatus = cachedCount == 0 ? "WARNING" : cachedCount < allChannelUuids.Count ? "INFO" : "OK";
                report.Checks.Add(new DiagnoseCheck
                {
                    Category = "Cache",
                    Name = "Probe Cache Coverage",
                    Status = cacheStatus,
                    Message = report.CacheStatus,
                    Recommendation = cachedCount == 0
                        ? "No probe cache files found. Tune to channels once so Jellyfin creates the cache. Subsequent tunes will be faster."
                        : cachedCount < allChannelUuids.Count
                            ? $"{allChannelUuids.Count - cachedCount} channel(s) still need an initial tune to build probe cache."
                            : "All channels have probe cache data. Channel switching should be fast."
                });
            }
            else
            {
                report.CacheStatus = mediaInfoDir != null && !Directory.Exists(mediaInfoDir)
                    ? "Mediainfo cache directory does not exist yet. Tune to a channel to create it."
                    : allChannelUuids.Count == 0
                        ? "No channel UUIDs available to check probe cache."
                        : "Cache status not available (missing cache path).";
                report.Checks.Add(new DiagnoseCheck { Category = "Cache", Name = "Probe Cache Coverage", Status = "INFO", Message = report.CacheStatus });
            }
        }
        catch (Exception ex)
        {
            report.CacheStatus = $"Could not check probe cache: {ex.Message}";
            report.Checks.Add(new DiagnoseCheck { Category = "Cache", Name = "Probe Cache Coverage", Status = "WARNING", Message = report.CacheStatus });
        }

        // -- Calculate final score ----------------------------------------
        report.CompatibilityScore = Math.Max(0, 100 - scoreDeductions);
        report.OverallStatus = report.CompatibilityScore >= 80 ? "OK" : report.CompatibilityScore >= 50 ? "WARNING" : "ERROR";

        if (report.Warnings.Count == 0 && report.Recommendations.Count == 0 && report.CompatibilityScore >= 80)
        {
            report.Recommendations.Add("? Everything looks good!");
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

            // -- Step 1: Create video codec profile "jellyfin-h264" ------
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

            // -- Step 2: Create audio codec profile "jellyfin-aac" -------
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

            // -- Step 3: Create streaming profile "jellyfin" -------------
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
    /// Creates multiple TVHeadend profile variants optimised for fast channel switching benchmarking.
    /// Creates: jellyfin-ultrafast (2 Mbps), jellyfin-fast (3 Mbps), jellyfin-balanced (5 Mbps) codec profiles
    /// and corresponding streaming profiles: jellyfin-ultrafast, jellyfin-fast, jellyfin-balanced.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A JSON object describing the created profiles.</returns>
    [HttpPost("CreateTestProfiles")]
    public async Task<ActionResult<object>> CreateTestProfiles(CancellationToken cancellationToken)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return BadRequest(new { Success = false, Message = "Plugin configuration is not available." });
            }

            using var httpClient = BuildHttpClient(config);
            var baseUrl = $"{(config.UseSSL ? "https" : "http")}://{config.Host}:{config.Port}";
            var webRoot = string.IsNullOrWhiteSpace(config.Webroot) ? "/" : config.Webroot.TrimEnd('/') + "/";

            var created = new List<string>();
            var errors = new List<string>();

            // Define video codec profile variants
            var videoProfiles = new[]
            {
                new
                {
                    Name = "jellyfin-h264-ultrafast",
                    Description = "Jellyfin: H.264 ultrafast preset, 2 Mbps, zerolatency. Fastest startup.",
                    BitRate = 2000,
                    Preset = "ultrafast",
                    Tune = "zerolatency",
                    Params = "repeat-headers=1:aud=1:keyint=25:min-keyint=25:scenecut=0:bframes=0",
                },
                new
                {
                    Name = "jellyfin-h264-fast",
                    Description = "Jellyfin: H.264 superfast preset, 3 Mbps, zerolatency. Fast startup.",
                    BitRate = 3000,
                    Preset = "superfast",
                    Tune = "zerolatency",
                    Params = "repeat-headers=1:aud=1:keyint=25:min-keyint=25:bframes=0",
                },
                new
                {
                    Name = "jellyfin-h264-balanced",
                    Description = "Jellyfin: H.264 faster preset, 5 Mbps, zerolatency. Balanced quality/speed.",
                    BitRate = 5000,
                    Preset = "faster",
                    Tune = "zerolatency",
                    Params = "repeat-headers=1:aud=1",
                },
            };

            // Audio codec profile (shared)
            const string audioProfileName = "jellyfin-aac";
            var audioCodecExists = await CodecProfileExistsAsync(httpClient, baseUrl, webRoot, audioProfileName, cancellationToken).ConfigureAwait(false);
            if (!audioCodecExists)
            {
                var audioConf = new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = audioProfileName,
                    ["description"] = "Jellyfin: AAC 128 kbps for fast channel switching.",
                    ["bit_rate"] = 128,
                    ["profile"] = -99,
                };

                var ok = await CreateCodecProfileAsync(httpClient, baseUrl, webRoot, "aac", audioConf, cancellationToken).ConfigureAwait(false);
                created.Add(ok ? $"audio codec '{audioProfileName}'" : $"FAILED audio codec '{audioProfileName}'");
            }
            else
            {
                created.Add($"audio codec '{audioProfileName}' (exists)");
            }

            // Create each video codec profile
            foreach (var vp in videoProfiles)
            {
                var exists = await CodecProfileExistsAsync(httpClient, baseUrl, webRoot, vp.Name, cancellationToken).ConfigureAwait(false);
                if (!exists)
                {
                    var videoConf = new System.Text.Json.Nodes.JsonObject
                    {
                        ["name"] = vp.Name,
                        ["description"] = vp.Description,
                        ["deinterlace"] = true,
                        ["height"] = 0,
                        ["scaling_mode"] = 0,
                        ["hwaccel"] = false,
                        ["bit_rate"] = vp.BitRate,
                        ["crf"] = 0,
                        ["profile"] = -99,
                        ["pix_fmt"] = -1,
                        ["preset"] = vp.Preset,
                        ["tune"] = vp.Tune,
                        ["params"] = vp.Params,
                    };

                    var ok = await CreateCodecProfileAsync(httpClient, baseUrl, webRoot, "libx264", videoConf, cancellationToken).ConfigureAwait(false);
                    created.Add(ok ? $"video codec '{vp.Name}' ({vp.BitRate}k, {vp.Preset})" : $"FAILED video codec '{vp.Name}'");
                }
                else
                {
                    created.Add($"video codec '{vp.Name}' (exists)");
                }
            }

            // Create streaming profiles for each variant
            var streamingVariants = new[]
            {
                new { Name = "jellyfin-ultrafast", VideoCodecProfile = "jellyfin-h264-ultrafast" },
                new { Name = "jellyfin-fast", VideoCodecProfile = "jellyfin-h264-fast" },
                new { Name = "jellyfin-balanced", VideoCodecProfile = "jellyfin-h264-balanced" },
            };

            var listUrl = $"{baseUrl}{webRoot}api/profile/list";
            var listResponse = await httpClient.GetStringAsync(listUrl, cancellationToken).ConfigureAwait(false);
            var profileList = JsonSerializer.Deserialize<ProfileListResponse>(listResponse, JsonOptions);

            foreach (var sv in streamingVariants)
            {
                var existingProfile = profileList?.Entries?.FirstOrDefault(e =>
                    string.Equals(e.Val, sv.Name, StringComparison.OrdinalIgnoreCase));

                if (existingProfile != null)
                {
                    created.Add($"streaming profile '{sv.Name}' (exists)");
                    // Still re-link codec profiles
                    var videoCodecP = await GetCodecProfileByNameAsync(httpClient, baseUrl, webRoot, sv.VideoCodecProfile, cancellationToken).ConfigureAwait(false);
                    var audioCodecP = await GetCodecProfileByNameAsync(httpClient, baseUrl, webRoot, audioProfileName, cancellationToken).ConfigureAwait(false);
                    if (videoCodecP != null && audioCodecP != null)
                    {
                        await LinkStreamingProfileToCodecProfilesAsync(httpClient, baseUrl, webRoot, existingProfile.Key, videoCodecP.Val, audioCodecP.Val, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                var confNode = new System.Text.Json.Nodes.JsonObject
                {
                    ["enabled"] = true,
                    ["name"] = sv.Name,
                    ["comment"] = $"Jellyfin benchmark profile ({sv.VideoCodecProfile})",
                    ["container"] = 2,
                    ["vcodec"] = sv.VideoCodecProfile,
                    ["acodec"] = audioProfileName,
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
                if (response.IsSuccessStatusCode)
                {
                    created.Add($"streaming profile '{sv.Name}'");

                    // Link codec profiles
                    var newProfile = await GetStreamingProfileByNameAsync(httpClient, baseUrl, webRoot, sv.Name, cancellationToken).ConfigureAwait(false);
                    var videoCodecP = await GetCodecProfileByNameAsync(httpClient, baseUrl, webRoot, sv.VideoCodecProfile, cancellationToken).ConfigureAwait(false);
                    var audioCodecP = await GetCodecProfileByNameAsync(httpClient, baseUrl, webRoot, audioProfileName, cancellationToken).ConfigureAwait(false);
                    if (newProfile != null && videoCodecP != null && audioCodecP != null)
                    {
                        await LinkStreamingProfileToCodecProfilesAsync(httpClient, baseUrl, webRoot, newProfile.Key, videoCodecP.Val, audioCodecP.Val, cancellationToken).ConfigureAwait(false);
                        created.Add($"linked '{sv.Name}' ? {sv.VideoCodecProfile} + {audioProfileName}");
                    }
                }
                else
                {
                    errors.Add($"Failed to create streaming profile '{sv.Name}'");
                }
            }

            // Also ensure the base 'jellyfin' profile exists (5Mbps faster preset)
            var jellyfinExists = profileList?.Entries?.Any(e =>
                string.Equals(e.Val, "jellyfin", StringComparison.OrdinalIgnoreCase)) == true;
            if (!jellyfinExists)
            {
                // Check if jellyfin-h264 codec profile exists
                var jellyfinH264Exists = await CodecProfileExistsAsync(httpClient, baseUrl, webRoot, "jellyfin-h264", cancellationToken).ConfigureAwait(false);
                if (!jellyfinH264Exists)
                {
                    var videoConf = new System.Text.Json.Nodes.JsonObject
                    {
                        ["name"] = "jellyfin-h264",
                        ["description"] = "Jellyfin: H.264 with 5 Mbps cap, faster preset, zerolatency.",
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
                    await CreateCodecProfileAsync(httpClient, baseUrl, webRoot, "libx264", videoConf, cancellationToken).ConfigureAwait(false);
                }

                var confNode = new System.Text.Json.Nodes.JsonObject
                {
                    ["enabled"] = true,
                    ["name"] = "jellyfin",
                    ["container"] = 2,
                    ["vcodec"] = "jellyfin-h264",
                    ["acodec"] = audioProfileName,
                };

                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("class", "profile-transcode"),
                    new KeyValuePair<string, string>("conf", confNode.ToJsonString())
                });

                var createUrl = $"{baseUrl}{webRoot}api/profile/create";
                await httpClient.PostAsync(createUrl, content, cancellationToken).ConfigureAwait(false);
                created.Add("streaming profile 'jellyfin'");
            }

            return Ok(new
            {
                Success = errors.Count == 0,
                Created = created,
                Errors = errors,
                Message = $"Created/verified: {string.Join(", ", created)}" + (errors.Count > 0 ? $" | Errors: {string.Join(", ", errors)}" : string.Empty),
                AvailableProfiles = AvailableTestProfiles
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating test profiles.");
            return Ok(new { Success = false, Message = $"Error: {ex.Message}" });
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

    /// <summary>
    /// Reads the current FFmpeg probesize and analyzeduration from Jellyfin's global encoding options.
    /// Uses reflection because the <c>FFmpegProbeSize</c> and <c>FFmpegAnalyzeDuration</c> properties
    /// exist on the runtime <c>EncodingOptions</c> type but are not exposed in the public SDK NuGet packages.
    /// Returns (probeSize, analyzeDuration) � both as raw strings, or null if not available.
    /// </summary>
    private (string? ProbeSize, string? AnalyzeDuration) ReadJellyfinFfmpegSettings()
    {
        try
        {
            var encoding = _serverConfigManager.GetConfiguration("encoding");
            var type = encoding.GetType();

            var probeSize = type.GetProperty("FFmpegProbeSize")?.GetValue(encoding) as string;
            var analyzeDuration = type.GetProperty("FFmpegAnalyzeDuration")?.GetValue(encoding) as string;

            // Fallback to environment variables if not set in config
            probeSize ??= Environment.GetEnvironmentVariable("JELLYFIN_FFmpeg__ProbeSize");
            analyzeDuration ??= Environment.GetEnvironmentVariable("JELLYFIN_FFmpeg__AnalyzeDuration");

            return (probeSize, analyzeDuration);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read Jellyfin's encoding options via reflection.");
            // Still try environment variables as fallback
            var probeSize = Environment.GetEnvironmentVariable("JELLYFIN_FFmpeg__ProbeSize");
            var analyzeDuration = Environment.GetEnvironmentVariable("JELLYFIN_FFmpeg__AnalyzeDuration");
            return (probeSize, analyzeDuration);
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
