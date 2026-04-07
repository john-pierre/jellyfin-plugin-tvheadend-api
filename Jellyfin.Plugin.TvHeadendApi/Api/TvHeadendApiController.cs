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
                : $"AnalyzeDuration: 0 (legacy auto mode) ? plugin falls back to 200 ms when stream details are available ? ffmpeg receives -analyzeduration 200000 �s. When probing, Jellyfin's global FFmpeg analyzeduration is used.");
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
                            profileClass = GetStringPropOrParam(entry, "class") ?? "unknown";
                            var container = MapContainer(GetStringPropOrParam(entry, "container") ?? GetIntPropOrParam(entry, "container")?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
                            if (string.IsNullOrWhiteSpace(container))
                            {
                                container = MapProfileClassToContainer(profileClass);
                            }

                            var proVideoCodec = GetStringPropOrParam(entry, "pro_vcodec")
                                ?? GetStringPropOrParam(entry, "vcodec")
                                ?? string.Empty;
                            var proAudioCodec = GetStringPropOrParam(entry, "pro_acodec")
                                ?? GetStringPropOrParam(entry, "acodec")
                                ?? string.Empty;
                            var srcVideoCodecs = GetStringArrayPropOrParam(entry, "src_vcodec");
                            var srcAudioCodecs = GetStringArrayPropOrParam(entry, "src_acodec");
                            var deinterlace = GetBoolPropOrParam(entry, "deinterlace");
                            if (deinterlace != true && !string.IsNullOrWhiteSpace(proVideoCodec))
                            {
                                deinterlace = await GetCodecProfileBoolSettingAsync(httpClient, baseUrl, webRoot, proVideoCodec, "deinterlace", cancellationToken).ConfigureAwait(false) ?? deinterlace;
                            }

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
                                        report.Checks.Add(new DiagnoseCheck { Category = "Streaming", Name = "Video Codec Link", Status = "WARNING", Message = "No linked video codec profile (pro_vcodec / vcodec).", Recommendation = "Link a video codec profile in TVHeadend for consistent output." });
                                        scoreDeductions += 5;
                                    }

                                    if (string.IsNullOrWhiteSpace(proAudioCodec))
                                    {
                                        report.Checks.Add(new DiagnoseCheck { Category = "Streaming", Name = "Audio Codec Link", Status = "WARNING", Message = "No linked audio codec profile (pro_acodec / acodec).", Recommendation = "Link an audio codec profile in TVHeadend for consistent output." });
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
        report.Checks.Add(new DiagnoseCheck { Category = "Playback", Name = "Playback Mode", Status = "OK", Message = $"DirectPlay={config.SupportsDirectPlay}, DirectStream={config.SupportsDirectStream}, Transcoding={config.SupportsTranscoding}" });

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
            report.Recommendations.Add("Probing is off and AnalyzeDuration is 0. This legacy auto mode falls back to 200ms when TVHeadend stream details are available. Consider setting an explicit AnalyzeDuration of 200ms to match the current default.");
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

            // -- Step 1b: Create video codec profile "jellyfin-h264-intel" (Intel QuickSync) ------
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

                var created = await CreateCodecProfileAsync(httpClient, baseUrl, webRoot, "hevc_qsv", videoIntelConf, cancellationToken).ConfigureAwait(false);
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

            // -- Step 2: Create audio codec profile "jellyfin-aac" -------
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
                        ["container"] = 9,
                        ["pro_vcodec"] = "jellyfin-h264",
                        ["pro_acodec"] = "jellyfin-aac",
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
            return await FindCodecProfileEntryByReferenceAsync(httpClient, baseUrl, webRoot, profileName, cancellationToken).ConfigureAwait(false) != null;
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
            return await FindCodecProfileEntryByReferenceAsync(httpClient, baseUrl, webRoot, profileName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve codec profile '{ProfileName}'.", profileName);
            return null;
        }
    }

    private async Task<bool?> GetCodecProfileBoolSettingAsync(HttpClient httpClient, string baseUrl, string webRoot, string codecProfileRef, string settingName, CancellationToken cancellationToken)
    {
        var codecProfile = await FindCodecProfileEntryByReferenceAsync(httpClient, baseUrl, webRoot, codecProfileRef, cancellationToken).ConfigureAwait(false);
        if (codecProfile == null || string.IsNullOrWhiteSpace(codecProfile.Key))
        {
            return null;
        }

        using var codecDoc = await LoadIdNodeByUuidAsync(httpClient, baseUrl, webRoot, codecProfile.Key, cancellationToken).ConfigureAwait(false);
        if (!codecDoc.RootElement.TryGetProperty("entries", out var entries) || entries.GetArrayLength() == 0)
        {
            return null;
        }

        return GetBoolPropOrParam(entries[0], settingName);
    }

    private async Task<CodecProfileListEntry?> FindCodecProfileEntryByReferenceAsync(HttpClient httpClient, string baseUrl, string webRoot, string profileReference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileReference))
        {
            return null;
        }

        var listUrl = $"{baseUrl}{webRoot}api/codec_profile/list";
        var response = await httpClient.GetStringAsync(listUrl, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(response);
        if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            var uuid = GetStringProp(entry, "uuid") ?? GetStringProp(entry, "key") ?? string.Empty;
            var title = GetStringProp(entry, "title") ?? GetStringProp(entry, "val") ?? string.Empty;
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

    private static string? GetStringPropOrParam(JsonElement element, string name)
    {
        return GetStringProp(element, name) ?? GetParamStringProp(element, name);
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

    private static int? GetIntPropOrParam(JsonElement element, string name)
    {
        return GetIntProp(element, name) ?? GetParamIntProp(element, name);
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

    private static bool? GetBoolPropOrParam(JsonElement element, string name)
    {
        return GetBoolProp(element, name) ?? GetParamBoolProp(element, name);
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

    private static IReadOnlyList<string> GetStringArrayPropOrParam(JsonElement element, string name)
    {
        var values = GetStringArrayProp(element, name);
        return values.Count > 0 ? values : GetParamStringArrayProp(element, name);
    }

    private static string? GetParamStringProp(JsonElement element, string name)
    {
        if (!TryGetParamValue(element, name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static int? GetParamIntProp(JsonElement element, string name)
    {
        if (!TryGetParamValue(element, name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private static bool? GetParamBoolProp(JsonElement element, string name)
    {
        if (!TryGetParamValue(element, name, out var value))
        {
            return null;
        }

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
            var stringValue = value.GetString();
            if (bool.TryParse(stringValue, out var boolValue))
            {
                return boolValue;
            }

            if (int.TryParse(stringValue, out var parsed))
            {
                return parsed != 0;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> GetParamStringArrayProp(JsonElement element, string name)
    {
        if (!TryGetParamValue(element, name, out var value) || value.ValueKind != JsonValueKind.Array)
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

    private static bool TryGetParamValue(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
        {
            foreach (var parameter in parameters.EnumerateArray())
            {
                var id = GetStringProp(parameter, "id");
                if (!string.Equals(id, name, StringComparison.OrdinalIgnoreCase)
                    || !parameter.TryGetProperty("value", out value))
                {
                    continue;
                }

                return true;
            }
        }

        value = default;
        return false;
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
            "9" => "mp4",
            "4" or "mp4" => "mp4",
            _ => raw.ToLowerInvariant()
        };
    }

    private static string MapProfileClassToContainer(string profileClass)
    {
        return profileClass.ToLowerInvariant() switch
        {
            "profile-matroska" => "matroska",
            "profile-mpegts" or "profile-mpegts-pass" or "profile-mpegts-spawn" => "mpegts",
            "profile-htsp" => "mpegts",
            _ => "mpegts"
        };
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
