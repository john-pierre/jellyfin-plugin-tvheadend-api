using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Model.Dvr;
using Jellyfin.Plugin.TvHeadendApi.Model.Guide;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Auth;
using Jellyfin.Plugin.TvHeadendApi.Service.Infrastructure;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Stream;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;

/// <summary>
/// Executes the TVHeadend diagnostics workflow and builds a structured report.
/// </summary>
internal sealed class DiagnosticService : IDiagnosticService
{
    private static readonly System.Text.Json.JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<DiagnosticService> _logger;
    private readonly IServerConfigurationManager _serverConfigManager;
    private readonly IEncodingOptionsReader _encodingOptionsReader;
    private readonly IProfileResolver _streamProfileResolver;
    private readonly IApiClient _tvheadendApiClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiagnosticService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="serverConfigManager">Jellyfin server configuration manager.</param>
    /// <param name="encodingOptionsReader">Reader for Jellyfin FFmpeg encoding options.</param>
    /// <param name="streamProfileResolver">Service for TVHeadend stream profile inspection.</param>
    /// <param name="tvheadendApiClient">TVHeadend API client.</param>
    public DiagnosticService(
        ILogger<DiagnosticService> logger,
        IServerConfigurationManager serverConfigManager,
        IEncodingOptionsReader encodingOptionsReader,
        IProfileResolver streamProfileResolver,
        IApiClient tvheadendApiClient)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serverConfigManager = serverConfigManager ?? throw new ArgumentNullException(nameof(serverConfigManager));
        _encodingOptionsReader = encodingOptionsReader ?? throw new ArgumentNullException(nameof(encodingOptionsReader));
        _streamProfileResolver = streamProfileResolver ?? throw new ArgumentNullException(nameof(streamProfileResolver));
        _tvheadendApiClient = tvheadendApiClient ?? throw new ArgumentNullException(nameof(tvheadendApiClient));
    }

    /// <inheritdoc />
    public async Task<DiagnoseResult> DiagnoseAsync(CancellationToken cancellationToken)
    {
        var report = new DiagnoseResult();
        var config = _tvheadendApiClient.GetCurrentConfiguration();
        var configuredStreamProfileExists = false;
        var configuredDvrProfileExists = false;
        var dvrProfileNames = new List<string>();
        var scoreDeductions = 0;

        if (config == null)
        {
            report.Connection = "? Plugin configuration is not available.";
            report.OverallStatus = "ERROR";
            report.CompatibilityScore = 0;
            return report;
        }

        report.PluginSettings.Add($"Streaming Profile: {(string.IsNullOrWhiteSpace(config.StreamingProfile) ? "(not set)" : config.StreamingProfile)}");
        report.PluginSettings.Add($"Direct Play: {config.SupportsDirectPlay}, Direct Stream: {config.SupportsDirectStream}, Transcoding: {config.SupportsTranscoding}");
        report.PluginSettings.Add($"Probing: {config.SupportsProbing}, Infinite Stream: {config.IsInfiniteStream}, Ignore DTS: {config.IgnoreDts}");

        var effectiveAnalyzeDurationMs = config.AnalyzeDurationMs > 0 ? config.AnalyzeDurationMs : 200;
        var ffmpegMicroseconds = effectiveAnalyzeDurationMs * 1000;
        report.PluginSettings.Add(
            config.AnalyzeDurationMs > 0
                ? $"AnalyzeDuration: {config.AnalyzeDurationMs} ms (explicit) -> ffmpeg receives -analyzeduration {ffmpegMicroseconds} us"
                : "AnalyzeDuration: 0 (legacy auto mode) -> plugin falls back to 200 ms when stream details are available -> ffmpeg receives -analyzeduration 200000 us. When probing, Jellyfin's global FFmpeg analyzeduration is used.");
        report.PluginSettings.Add($"BufferMs: {(config.BufferMs > 0 ? $"{config.BufferMs} ms" : "0 (Jellyfin default)")}");

        var (jellyfinProbeSize, jellyfinAnalyzeDuration) = _encodingOptionsReader.ReadFfmpegSettings(_serverConfigManager, _logger);
        var probeSizeDisplay = !string.IsNullOrWhiteSpace(jellyfinProbeSize)
            ? $"{jellyfinProbeSize} (from config or environment)"
            : "(not set / using Jellyfin default)";
        report.PluginSettings.Add($"Jellyfin FFmpeg ProbeSize: {probeSizeDisplay}");
        report.PluginSettings.Add($"Jellyfin FFmpeg AnalyzeDuration: {jellyfinAnalyzeDuration ?? "(not set / default)"}");

        report.PluginSettings.Add($"DVR enabled: {config.EnableTvhDvr}, Recording Profile: {config.RecordingProfile}");

        if (string.IsNullOrWhiteSpace(config.AuthToken))
        {
            report.Checks.Add(new DiagnoseCheck
            {
                Category = "Authentication",
                Name = "Auth Token Format",
                Status = "ERROR",
                Message = "Auth token is empty.",
                Recommendation = "Generate a token and use only letters and numbers (A-Z, a-z, 0-9)."
            });
            scoreDeductions += 20;
        }
        else if (!TokenValidator.IsAlphanumeric(config.AuthToken))
        {
            report.Checks.Add(new DiagnoseCheck
            {
                Category = "Authentication",
                Name = "Auth Token Format",
                Status = "ERROR",
                Message = "Auth token contains unsupported characters.",
                Recommendation = "Use only letters and numbers (A-Z, a-z, 0-9)."
            });
            scoreDeductions += 20;
        }
        else
        {
            report.Checks.Add(new DiagnoseCheck
            {
                Category = "Authentication",
                Name = "Auth Token Format",
                Status = "OK",
                Message = "Auth token format is alphanumeric."
            });
        }

        HttpClient httpClient;
        string baseUrl;
        string webRoot;
        try
        {
            httpClient = _tvheadendApiClient.BuildHttpClient(config);
            baseUrl = _tvheadendApiClient.GetBaseUrl(config);
            webRoot = _tvheadendApiClient.GetWebRoot(config);
        }
        catch (Exception ex)
        {
            report.Connection = $"? Failed to build HTTP client: {ex.Message}";
            report.OverallStatus = "ERROR";
            report.CompatibilityScore = 0;
            report.Checks.Add(new DiagnoseCheck { Category = "Connection", Name = "HTTP Client", Status = "ERROR", Message = ex.Message });
            return report;
        }

        var allChannelUuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (httpClient)
        {
            try
            {
                var infoUrl = $"{baseUrl}{webRoot}api/serverinfo";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var infoResponse = await _tvheadendApiClient.GetStringAsync(httpClient, infoUrl, cancellationToken).ConfigureAwait(false);
                sw.Stop();
                report.LatencyMs = (int)sw.ElapsedMilliseconds;

                var serverInfo = JsonSerializer.Deserialize<ServerInfoResponse>(infoResponse, SerializerOptions) ?? new ServerInfoResponse();

                var swVersion = string.IsNullOrWhiteSpace(serverInfo.SwVersion) ? "unknown" : serverInfo.SwVersion;
                var apiVersion = serverInfo.ApiVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";
                var serverName = serverInfo.Name;

                report.Connection = $"? Connected to {(string.IsNullOrEmpty(serverName) ? config.Host : serverName)}";
                report.ServerVersion = $"TVHeadend {swVersion} (API v{apiVersion})";

                report.Checks.Add(new DiagnoseCheck
                {
                    Category = "Connection",
                    Name = "TVHeadend Connectivity",
                    Status = "OK",
                    Message = $"Connected in {report.LatencyMs}ms to {config.Host}:{config.Port}"
                });

                var apiVer = serverInfo.ApiVersion ?? 0;
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
                report.Connection = $"Cannot reach TVHeadend at {config.Host}:{config.Port} - {ex.Message}";
                report.Checks.Add(new DiagnoseCheck { Category = "Connection", Name = "TVHeadend Connectivity", Status = "ERROR", Message = ex.Message, Recommendation = "Check host, port, and network connectivity." });
                report.OverallStatus = "ERROR";
                report.CompatibilityScore = 0;
                return report;
            }
            catch (Exception ex)
            {
                report.Connection = $"?? Connected but serverinfo failed: {ex.Message}";
                report.Checks.Add(new DiagnoseCheck { Category = "Connection", Name = "Server Info", Status = "WARNING", Message = ex.Message });
                scoreDeductions += 10;
            }

            try
            {
                var chUrl = $"{baseUrl}{webRoot}api/channel/grid?limit=500&sort=number";
                var chResponse = await _tvheadendApiClient.GetStringAsync(httpClient, chUrl, cancellationToken).ConfigureAwait(false);
                var channelGrid = JsonSerializer.Deserialize<ChannelGridResponse>(chResponse, SerializerOptions);
                if (channelGrid != null)
                {
                    report.ChannelCount = channelGrid.Total;
                    foreach (var chEntry in channelGrid.Entries)
                    {
                        var uuid = chEntry.Uuid;
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

            string? profileClass = null;
            try
            {
                var profileReferences = await _streamProfileResolver.GetProfilesAsync(httpClient, baseUrl, webRoot, cancellationToken).ConfigureAwait(false);
                var profiles = profileReferences.Select(e => e.Name).ToList();
                foreach (var p in profiles)
                {
                    report.AvailableStreamingProfiles.Add(p);
                }

                var matchingStreamProfile = profileReferences.FirstOrDefault(e =>
                    string.Equals(e.Name, config.StreamingProfile, StringComparison.OrdinalIgnoreCase));

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
                        var profileDetails = await _streamProfileResolver
                            .GetProfileDetailsByUuidAsync(httpClient, baseUrl, webRoot, matchingStreamProfile.Key, matchingStreamProfile.Name, cancellationToken)
                            .ConfigureAwait(false);
                        if (profileDetails != null)
                        {
                            profileClass = string.IsNullOrWhiteSpace(profileDetails.ProfileClass) ? "unknown" : profileDetails.ProfileClass;
                            var container = profileDetails.Container;
                            var proVideoCodec = profileDetails.ProVideoCodec;
                            var proAudioCodec = profileDetails.ProAudioCodec;
                            var srcVideoCodecs = profileDetails.SrcVideoCodecs;
                            var srcAudioCodecs = profileDetails.SrcAudioCodecs;
                            var deinterlace = profileDetails.Deinterlace;
                            if (deinterlace != true && !string.IsNullOrWhiteSpace(proVideoCodec))
                            {
                                deinterlace = await GetCodecProfileBoolSettingAsync(httpClient, baseUrl, webRoot, proVideoCodec, "deinterlace", cancellationToken).ConfigureAwait(false) ?? deinterlace;
                            }

                            report.PluginSettings.Add($"TVH stream profile: class={profileClass}, container={(string.IsNullOrWhiteSpace(container) ? "(unknown)" : container)}");
                            report.PluginSettings.Add($"TVH codec links: video={(string.IsNullOrWhiteSpace(proVideoCodec) ? "(not linked)" : proVideoCodec)}, audio={(string.IsNullOrWhiteSpace(proAudioCodec) ? "(not linked)" : proAudioCodec)}");

                            if (profileClass.Contains("transcode", StringComparison.OrdinalIgnoreCase))
                            {
                                report.Checks.Add(new DiagnoseCheck { Category = "Streaming", Name = "Profile Type", Status = "OK", Message = "Transcode profile detected. Output format is fixed per channel." });

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

                                if (deinterlace == true)
                                {
                                    report.Checks.Add(new DiagnoseCheck { Category = "Streaming", Name = "Deinterlacing", Status = "OK", Message = "Deinterlacing is enabled." });
                                }
                                else
                                {
                                    report.Checks.Add(new DiagnoseCheck { Category = "Streaming", Name = "Deinterlacing", Status = "WARNING", Message = "Deinterlacing is not enabled.", Recommendation = "Enable deinterlacing in the TVHeadend video codec profile so clients can more often direct play." });
                                    scoreDeductions += 5;
                                }

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

            if (config.EnableTvhDvr)
            {
                try
                {
                    var dvrUrl = $"{baseUrl}{webRoot}api/dvr/entry/grid?limit=1";
                    var dvrResponse = await _tvheadendApiClient.GetStringAsync(httpClient, dvrUrl, cancellationToken).ConfigureAwait(false);
                    var dvrEntries = JsonSerializer.Deserialize<DvrEntryGridResponse>(dvrResponse, SerializerOptions);
                    report.DvrEntryCount = dvrEntries?.Total ?? 0;
                }
                catch (Exception ex)
                {
                    report.Warnings.Add($"Could not fetch DVR entries: {ex.Message}");
                }

                try
                {
                    var dvrLoadUrl = $"{baseUrl}{webRoot}api/idnode/load";
                    using var dvrHttpResponse = await _tvheadendApiClient.PostFormAsync(
                        httpClient,
                        dvrLoadUrl,
                        new[]
                        {
                            new KeyValuePair<string, string>("enum", "1"),
                            new KeyValuePair<string, string>("class", "dvrconfig"),
                        },
                        cancellationToken).ConfigureAwait(false);
                    dvrHttpResponse.EnsureSuccessStatusCode();
                    var dvrBody = await dvrHttpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    var dvrConfigList = JsonSerializer.Deserialize<DvrConfigListResponse>(dvrBody, SerializerOptions);
                    if (dvrConfigList != null && dvrConfigList.Entries.Length > 0)
                    {
                        foreach (var entry in dvrConfigList.Entries)
                        {
                            var name = entry.EffectiveName;
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                dvrProfileNames.Add(name);
                            }
                        }

                        configuredDvrProfileExists = dvrProfileNames.Any(name => string.Equals(name, config.RecordingProfile, StringComparison.OrdinalIgnoreCase));
                        report.PluginSettings.Add($"TVH DVR profiles: {(dvrProfileNames.Count == 0 ? "(none found)" : string.Join(", ", dvrProfileNames))}");

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

        report.Checks.Add(new DiagnoseCheck { Category = "Playback", Name = "Playback Mode", Status = "OK", Message = $"DirectPlay={config.SupportsDirectPlay}, DirectStream={config.SupportsDirectStream}, Transcoding={config.SupportsTranscoding}" });

        if (config.AnalyzeDurationMs > 1000)
        {
            report.Checks.Add(new DiagnoseCheck
            {
                Category = "Playback",
                Name = "AnalyzeDuration",
                Status = "WARNING",
                Message = $"AnalyzeDuration is {config.AnalyzeDurationMs}ms ({config.AnalyzeDurationMs * 1000}us) which is very high.",
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

        if (!string.IsNullOrWhiteSpace(jellyfinAnalyzeDuration))
        {
            report.Checks.Add(new DiagnoseCheck { Category = "FFmpeg", Name = "AnalyzeDuration (global)", Status = "INFO", Message = $"Jellyfin FFmpeg AnalyzeDuration: {jellyfinAnalyzeDuration}" });
        }

        if (string.IsNullOrWhiteSpace(config.StreamingProfile))
        {
            report.Recommendations.Add("Set a Streaming Profile. Use 'pass' for original quality or create a transcode profile for consistent output.");
        }

        if (report.ChannelCount == 0)
        {
            report.Recommendations.Add("No channels found. Make sure TVHeadend has scanned and mapped channels.");
        }

        try
        {
            var cachePath = Plugin.Instance?.CachePath;
            var mediaInfoDir = !string.IsNullOrWhiteSpace(cachePath)
                ? Path.Combine(cachePath, "mediainfo")
                : null;

            if (mediaInfoDir != null && Directory.Exists(mediaInfoDir) && allChannelUuids.Count > 0)
            {
                var channelIdRegex = new System.Text.RegularExpressions.Regex(
                    @"stream/channel/([0-9a-f]{32})",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                var cachedChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var files = Directory.GetFiles(mediaInfoDir, "*.json");
                foreach (var file in files)
                {
                    try
                    {
                        var fileJson = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
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

        report.CompatibilityScore = Math.Max(0, 100 - scoreDeductions);
        report.OverallStatus = report.CompatibilityScore >= 80 ? "OK" : report.CompatibilityScore >= 50 ? "WARNING" : "ERROR";

        if (report.Warnings.Count == 0 && report.Recommendations.Count == 0 && report.CompatibilityScore >= 80)
        {
            report.Recommendations.Add("? Everything looks good!");
        }

        return report;
    }

    private async Task<bool?> GetCodecProfileBoolSettingAsync(HttpClient httpClient, string baseUrl, string webRoot, string codecProfileRef, string settingName, CancellationToken cancellationToken)
    {
        var codecProfile = await FindCodecProfileEntryByReferenceAsync(httpClient, baseUrl, webRoot, codecProfileRef, cancellationToken).ConfigureAwait(false);
        if (codecProfile == null || string.IsNullOrWhiteSpace(codecProfile.Key))
        {
            return null;
        }

        var codecResponse = await LoadIdNodeByUuidAsync(httpClient, baseUrl, webRoot, codecProfile.Key, cancellationToken).ConfigureAwait(false);
        if (codecResponse?.Entries == null || codecResponse.Entries.Length == 0)
        {
            return null;
        }

        var codecEntry = codecResponse.Entries[0];
        var directValue = GetIdNodeProperty(codecEntry, settingName);
        return ReadBoolOrParam(directValue, codecEntry.Params, settingName);
    }

    private async Task<CodecProfileListEntry?> FindCodecProfileEntryByReferenceAsync(HttpClient httpClient, string baseUrl, string webRoot, string profileReference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileReference))
        {
            return null;
        }

        var listUrl = $"{baseUrl}{webRoot}api/codec_profile/list";
        var response = await _tvheadendApiClient.GetStringAsync(httpClient, listUrl, cancellationToken).ConfigureAwait(false);
        var list = JsonSerializer.Deserialize<CodecProfileListResponse>(response, SerializerOptions);
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

    private static string NormalizeCodecProfileTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var separatorIndex = title.IndexOf(" (", StringComparison.Ordinal);
        return separatorIndex > 0 ? title[..separatorIndex] : title;
    }

    private async Task<IdNodeLoadResponse?> LoadIdNodeByUuidAsync(HttpClient httpClient, string baseUrl, string webRoot, string uuid, CancellationToken cancellationToken)
    {
        var url = $"{baseUrl}{webRoot}api/idnode/load?uuid={Uri.EscapeDataString(uuid)}";
        var body = await _tvheadendApiClient.GetStringAsync(httpClient, url, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<IdNodeLoadResponse>(body, SerializerOptions);
    }

    private static JsonElement GetIdNodeProperty(IdNodeEntry entry, string name)
    {
        return name switch
        {
            "deinterlace" => entry.Deinterlace,
            "enabled" => entry.Enabled,
            "default" => entry.IsDefault,
            "container" => entry.Container,
            "pro_vcodec" => entry.ProVideoCodec,
            "vcodec" => entry.VideoCodec,
            "pro_acodec" => entry.ProAudioCodec,
            "acodec" => entry.AudioCodec,
            "src_vcodec" => entry.SourceVideoCodecs,
            "src_acodec" => entry.SourceAudioCodecs,
            "pro_scodec" => entry.ProSubtitleCodec,
            "src_scodec" => entry.SourceSubtitleCodecs,
            "name" => entry.Name,
            "class" => entry.ProfileClass,
            "codec" => entry.Codec,
            "timeout" => entry.Timeout,
            "timeout_start" => entry.TimeoutStart,
            "priority" => entry.Priority,
            "fpriority" => entry.FPriority,
            "restart" => entry.Restart,
            "contaccess" => entry.ContinuousAccess,
            "catimeout" => entry.CaTimeout,
            "swservice" => entry.SoftwareService,
            "svfilter" => entry.ServiceVideoFilter,
            _ => default,
        };
    }

    private static bool? ReadBoolOrParam(JsonElement directValue, IReadOnlyList<IdNodeParam> parameters, string parameterName)
    {
        return ReadBool(directValue) ?? ReadBool(GetParamValue(parameters, parameterName));
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

    private sealed class CodecProfileListEntry
    {
        public string Key { get; init; } = string.Empty;

        public string Val { get; init; } = string.Empty;
    }
}
