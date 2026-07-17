using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Model.Dvr;
using Jellyfin.Plugin.TvHeadendApi.Model.Guide;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Common;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Jellyfin.Plugin.TvHeadendApi.Service.Storage;
using Jellyfin.Plugin.TvHeadendApi.Service.Stream;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;

/// <summary>
/// Executes the TVHeadend diagnostics workflow and builds a structured report.
/// </summary>
internal sealed class DiagnosticService : IDiagnosticService
{
    /// <summary>
    /// Maximum score deduction when no channel has a probe cache file. The deduction scales
    /// linearly with the share of uncovered channels because the MediaInfo probe cache is the
    /// primary lever that keeps playback on the fast Direct Play startup path — missing
    /// coverage directly translates into slow channel starts.
    /// </summary>
    internal const int ProbeCacheMaxScoreDeduction = 25;

    /// <summary>Coverage ratio at or above which probe cache coverage is considered good.</summary>
    internal const double ProbeCacheOkCoverage = 0.9;

    /// <summary>Coverage ratio below which probe cache coverage is considered critically low.</summary>
    internal const double ProbeCacheErrorCoverage = 0.2;

    private readonly ILogger<DiagnosticService> _logger;
    private readonly IServerConfigurationManager _serverConfigManager;
    private readonly IEncodingOptionsReader _encodingOptionsReader;
    private readonly IProfileResolver _streamProfileResolver;
    private readonly IApiClient _tvheadendApiClient;
    private readonly IUrlBuilder _tvheadendUrlBuilder;
    private readonly CachePathProvider _cachePathProvider;
    private readonly IMediaInfoCacheService? _mediaInfoCacheService;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiagnosticService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="serverConfigManager">Jellyfin server configuration manager.</param>
    /// <param name="encodingOptionsReader">Reader for Jellyfin FFmpeg encoding options.</param>
    /// <param name="streamProfileResolver">Service for TVHeadend stream profile inspection.</param>
    /// <param name="tvheadendApiClient">TVHeadend API client.</param>
    /// <param name="tvheadendUrlBuilder">TVHeadend URL builder.</param>
    /// <param name="cachePathProvider">Provider for the plugin cache path.</param>
    /// <param name="mediaInfoCacheService">Optional: cache service for proper cache file name computation.</param>
    public DiagnosticService(
        ILogger<DiagnosticService> logger,
        IServerConfigurationManager serverConfigManager,
        IEncodingOptionsReader encodingOptionsReader,
        IProfileResolver streamProfileResolver,
        IApiClient tvheadendApiClient,
        IUrlBuilder tvheadendUrlBuilder,
        CachePathProvider cachePathProvider,
        IMediaInfoCacheService? mediaInfoCacheService = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serverConfigManager = serverConfigManager ?? throw new ArgumentNullException(nameof(serverConfigManager));
        _encodingOptionsReader = encodingOptionsReader ?? throw new ArgumentNullException(nameof(encodingOptionsReader));
        _streamProfileResolver = streamProfileResolver ?? throw new ArgumentNullException(nameof(streamProfileResolver));
        _tvheadendApiClient = tvheadendApiClient ?? throw new ArgumentNullException(nameof(tvheadendApiClient));
        _tvheadendUrlBuilder = tvheadendUrlBuilder ?? throw new ArgumentNullException(nameof(tvheadendUrlBuilder));
        _cachePathProvider = cachePathProvider ?? throw new ArgumentNullException(nameof(cachePathProvider));
        _mediaInfoCacheService = mediaInfoCacheService;
    }

    /// <inheritdoc />
    public async Task<DiagnoseResult> DiagnoseAsync(CancellationToken cancellationToken)
    {
        var report = new DiagnoseResult();
        var config = _tvheadendApiClient.GetCurrentConfiguration();
        var scoreDeductions = 0;

        if (config == null)
        {
            report.Connection = "? Plugin configuration is not available.";
            report.OverallStatus = "ERROR";
            report.CompatibilityScore = 0;
            return report;
        }

        AddPluginSettingsToReport(report, config);
        scoreDeductions += AuthTokenChecker.Check(report, config);

        HttpClient httpClient;
        string baseUrl;
        string webRoot;
        try
        {
            httpClient = _tvheadendApiClient.CreateApiHttpClient(config);
            baseUrl = _tvheadendUrlBuilder.GetBaseUrl(config);
            webRoot = _tvheadendUrlBuilder.GetWebRoot(config);
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
            scoreDeductions += await CheckServerConnectivityAsync(report, config, httpClient, baseUrl, webRoot, cancellationToken).ConfigureAwait(false);
            if (report.OverallStatus == "ERROR")
            {
                return report;
            }

            await FetchChannelGridAsync(report, config, httpClient, baseUrl, webRoot, allChannelUuids, cancellationToken).ConfigureAwait(false);
            scoreDeductions += await CheckStreamingProfilesAsync(report, config, httpClient, baseUrl, webRoot, cancellationToken).ConfigureAwait(false);
            await CheckTranscodeCapabilityAsync(report, config, httpClient, cancellationToken).ConfigureAwait(false);
            scoreDeductions += await CheckDvrProfilesAsync(report, config, httpClient, baseUrl, webRoot, cancellationToken).ConfigureAwait(false);
        }

        scoreDeductions += PlaybackSettingsChecker.Check(report, config.SupportsDirectPlay, config.SupportsDirectStream, config.SupportsTranscoding, config.SupportsProbing, config.AnalyzeDurationMs, config.BufferMs);
        CheckFfmpegSettings(report, config);
        scoreDeductions += CheckProbeCacheStatus(report, config, allChannelUuids);
        RelayChecker.Check(report, config.RelayEnabled, config.RelayHostOverride);

        report.CompatibilityScore = Math.Max(0, 100 - scoreDeductions);
        report.OverallStatus = report.CompatibilityScore >= 80 ? "OK" : report.CompatibilityScore >= 50 ? "WARNING" : "ERROR";

        if (report.Warnings.Count == 0 && report.Recommendations.Count == 0 && report.CompatibilityScore >= 80)
        {
            report.Recommendations.Add("Everything looks good!");
        }

        return report;
    }

    private void AddPluginSettingsToReport(DiagnoseResult report, Jellyfin.Plugin.TvHeadendApi.Configuration.PluginConfiguration config)
    {
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

        report.PluginSettings.Add($"Recording Profile: {config.RecordingProfile}");
    }

    private async Task<int> CheckServerConnectivityAsync(DiagnoseResult report, Jellyfin.Plugin.TvHeadendApi.Configuration.PluginConfiguration config, HttpClient httpClient, string baseUrl, string webRoot, CancellationToken cancellationToken)
    {
        var scoreDeductions = 0;
        try
        {
            var infoUrl = _tvheadendUrlBuilder.BuildApiUrl(config, "api/serverinfo");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var infoResponse = await _tvheadendApiClient.GetStringAsync(httpClient, infoUrl, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            report.LatencyMs = (int)sw.ElapsedMilliseconds;

            var serverInfo = JsonSerializer.Deserialize<ServerInfoResponse>(infoResponse, JsonDefaults.Api) ?? new ServerInfoResponse();

            var swVersion = string.IsNullOrWhiteSpace(serverInfo.SwVersion) ? "unknown" : serverInfo.SwVersion;
            var apiVersion = serverInfo.ApiVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";
            var serverName = serverInfo.Name;

            report.Connection = $"Connected to {(string.IsNullOrEmpty(serverName) ? config.Host : serverName)} ({config.Host}:{config.Port})";
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
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("circuit breaker", StringComparison.OrdinalIgnoreCase))
        {
            // The resilience layer refuses calls because recent requests kept failing —
            // functionally the backend is unreachable. Reporting this as a soft warning made
            // the dashboard claim "OK, score 90" while nothing worked (health-panel contradiction).
            report.Connection = $"Cannot reach TVHeadend at {config.Host}:{config.Port} — circuit breaker is open after repeated failures";
            report.Checks.Add(new DiagnoseCheck { Category = "Connection", Name = "TVHeadend Connectivity", Status = "ERROR", Message = ex.Message, Recommendation = "Check host, port, credentials, and whether TVHeadend is running; the breaker retries automatically." });
            report.OverallStatus = "ERROR";
            report.CompatibilityScore = 0;
        }
        catch (Exception ex)
        {
            report.Connection = $"?? Connected but serverinfo failed: {ex.Message}";
            report.Checks.Add(new DiagnoseCheck { Category = "Connection", Name = "Server Info", Status = "WARNING", Message = ex.Message });
            scoreDeductions += 10;
        }

        return scoreDeductions;
    }

    private async Task FetchChannelGridAsync(DiagnoseResult report, Jellyfin.Plugin.TvHeadendApi.Configuration.PluginConfiguration config, HttpClient httpClient, string baseUrl, string webRoot, HashSet<string> allChannelUuids, CancellationToken cancellationToken)
    {
        try
        {
            var chUrl = _tvheadendUrlBuilder.BuildApiUrl(config, "api/channel/grid?limit=500&sort=number");
            var chResponse = await _tvheadendApiClient.GetStringAsync(httpClient, chUrl, cancellationToken).ConfigureAwait(false);
            var channelGrid = JsonSerializer.Deserialize<ChannelGridResponse>(chResponse, JsonDefaults.Api);
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
    }

    private async Task<int> CheckStreamingProfilesAsync(DiagnoseResult report, Jellyfin.Plugin.TvHeadendApi.Configuration.PluginConfiguration config, HttpClient httpClient, string baseUrl, string webRoot, CancellationToken cancellationToken)
    {
        var scoreDeductions = 0;
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

            var configuredStreamProfileExists = matchingStreamProfile != null;

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
                scoreDeductions += await InspectStreamProfileDetailsAsync(report, config, httpClient, baseUrl, webRoot, matchingStreamProfile, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            report.Warnings.Add($"Could not fetch profile list: {ex.Message}");
        }

        return scoreDeductions;
    }

    private async Task<int> InspectStreamProfileDetailsAsync(DiagnoseResult report, Jellyfin.Plugin.TvHeadendApi.Configuration.PluginConfiguration config, HttpClient httpClient, string baseUrl, string webRoot, ProfileReference matchingStreamProfile, CancellationToken cancellationToken)
    {
        var scoreDeductions = 0;
        try
        {
            var profileDetails = await _streamProfileResolver
                .GetProfileDetailsByUuidAsync(httpClient, baseUrl, webRoot, matchingStreamProfile.Key, matchingStreamProfile.Name, cancellationToken)
                .ConfigureAwait(false);
            if (profileDetails != null)
            {
                var profileClass = string.IsNullOrWhiteSpace(profileDetails.ProfileClass) ? "unknown" : profileDetails.ProfileClass;
                var container = profileDetails.Container;
                var proVideoCodec = profileDetails.ProVideoCodec;
                var proAudioCodec = profileDetails.ProAudioCodec;
                var srcVideoCodecs = profileDetails.SrcVideoCodecs;
                var srcAudioCodecs = profileDetails.SrcAudioCodecs;
                var deinterlace = profileDetails.Deinterlace;
                if (deinterlace != true && !string.IsNullOrWhiteSpace(proVideoCodec))
                {
                    deinterlace = await GetCodecProfileBoolSettingAsync(httpClient, config, baseUrl, webRoot, proVideoCodec, "deinterlace", cancellationToken).ConfigureAwait(false) ?? deinterlace;
                }

                report.PluginSettings.Add($"TVH stream profile: class={profileClass}, container={(string.IsNullOrWhiteSpace(container) ? "(unknown)" : container)}");
                report.PluginSettings.Add($"TVH codec links: video={(string.IsNullOrWhiteSpace(proVideoCodec) ? "(not linked)" : proVideoCodec)}, audio={(string.IsNullOrWhiteSpace(proAudioCodec) ? "(not linked)" : proAudioCodec)}");

                if (profileClass.Contains("transcode", StringComparison.OrdinalIgnoreCase))
                {
                    scoreDeductions += PlaybackSettingsChecker.CheckTranscodeProfile(report, proVideoCodec, proAudioCodec, srcVideoCodecs, srcAudioCodecs, deinterlace);
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

        return scoreDeductions;
    }

    /// <summary>
    /// Checks whether TVHeadend can actually encode video. Many builds ship without a software
    /// H.264 encoder (libx264) and expose only hardware encoders (VAAPI/QSV) that need a configured
    /// GPU — in that case any video-transcode profile (including the plugin's 'jellyfin' profile,
    /// which uses libx264) silently produces audio-only. Surfacing this prevents users from creating
    /// a transcode profile and hitting audio-only playback.
    /// </summary>
    private async Task CheckTranscodeCapabilityAsync(DiagnoseResult report, Jellyfin.Plugin.TvHeadendApi.Configuration.PluginConfiguration config, HttpClient httpClient, CancellationToken cancellationToken)
    {
        try
        {
            var url = _tvheadendUrlBuilder.BuildApiUrl(config, "api/codec/list");
            var json = await _tvheadendApiClient.GetStringAsync(httpClient, url, cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var videoEncoders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasSoftwareH264 = false;
            var hasHardwareVideo = false;

            foreach (var entry in entries.EnumerateArray())
            {
                var caption = entry.TryGetProperty("caption", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? string.Empty : string.Empty;
                var className = entry.TryGetProperty("class", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? string.Empty : string.Empty;
                var id = (caption + " " + className).ToLowerInvariant();

                var isVideo = id.Contains("264", StringComparison.Ordinal) || id.Contains("hevc", StringComparison.Ordinal) || id.Contains("265", StringComparison.Ordinal)
                    || id.Contains("mpeg2video", StringComparison.Ordinal) || id.Contains("mpeg4", StringComparison.Ordinal)
                    || id.Contains("vp8", StringComparison.Ordinal) || id.Contains("vp9", StringComparison.Ordinal) || id.Contains("av1", StringComparison.Ordinal) || id.Contains("theora", StringComparison.Ordinal);
                if (!isVideo)
                {
                    continue;
                }

                videoEncoders.Add(string.IsNullOrWhiteSpace(caption) ? className : caption);

                if (id.Contains("libx264", StringComparison.Ordinal))
                {
                    hasSoftwareH264 = true;
                }

                if (id.Contains("vaapi", StringComparison.Ordinal) || id.Contains("qsv", StringComparison.Ordinal) || id.Contains("nvenc", StringComparison.Ordinal)
                    || id.Contains("v4l2m2m", StringComparison.Ordinal) || id.Contains("videotoolbox", StringComparison.Ordinal) || id.Contains("_omx", StringComparison.Ordinal))
                {
                    hasHardwareVideo = true;
                }
            }

            if (videoEncoders.Count > 0)
            {
                report.PluginSettings.Add("TVHeadend video encoders: " + string.Join(", ", videoEncoders));
            }

            if (hasSoftwareH264)
            {
                report.Checks.Add(new DiagnoseCheck
                {
                    Category = "Streaming",
                    Name = "Transcode Capability",
                    Status = "OK",
                    Message = "TVHeadend has a software H.264 encoder (libx264); transcode profiles can produce video.",
                });
                return;
            }

            var message = hasHardwareVideo
                ? "TVHeadend exposes only hardware video encoders (VAAPI/QSV/etc.) and no software H.264 (libx264). Hardware transcoding requires a configured GPU/render device; without one, the plugin's 'jellyfin' transcode profile (libx264) produces AUDIO-ONLY."
                : "TVHeadend has no usable H.264 video encoder (no libx264). Any video-transcode profile — including the plugin's 'jellyfin' profile — produces AUDIO-ONLY.";

            report.Checks.Add(new DiagnoseCheck
            {
                Category = "Streaming",
                Name = "Transcode Capability",
                Status = "WARNING",
                Message = message,
                Recommendation = "Use a pass-through profile for Direct Play (the plugin default), or configure a working video encoder in TVHeadend before using transcode profiles.",
            });
            report.Warnings.Add("TVHeadend cannot software-transcode H.264 video — transcode profiles will be audio-only. Use Direct Play (pass).");
        }
        catch (Exception ex)
        {
            report.Checks.Add(new DiagnoseCheck { Category = "Streaming", Name = "Transcode Capability", Status = "INFO", Message = "Could not query TVHeadend codec list: " + ex.Message });
        }
    }

    private async Task<int> CheckDvrProfilesAsync(DiagnoseResult report, Jellyfin.Plugin.TvHeadendApi.Configuration.PluginConfiguration config, HttpClient httpClient, string baseUrl, string webRoot, CancellationToken cancellationToken)
    {
        var scoreDeductions = 0;

        try
        {
            var dvrUrl = _tvheadendUrlBuilder.BuildApiUrl(config, "api/dvr/entry/grid?limit=1");
            var dvrResponse = await _tvheadendApiClient.GetStringAsync(httpClient, dvrUrl, cancellationToken).ConfigureAwait(false);
            var dvrEntries = JsonSerializer.Deserialize<DvrEntryGridResponse>(dvrResponse, JsonDefaults.Api);
            report.DvrEntryCount = dvrEntries?.Total ?? 0;
        }
        catch (Exception ex)
        {
            report.Warnings.Add($"Could not fetch DVR entries: {ex.Message}");
        }

        try
        {
            var dvrLoadUrl = _tvheadendUrlBuilder.BuildApiUrl(config, "api/idnode/load");
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
            var dvrConfigList = JsonSerializer.Deserialize<DvrConfigListResponse>(dvrBody, JsonDefaults.Api);
            if (dvrConfigList != null && dvrConfigList.Entries.Length > 0)
            {
                var dvrProfileNames = new List<string>();
                foreach (var entry in dvrConfigList.Entries)
                {
                    var name = entry.EffectiveName;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        dvrProfileNames.Add(name);
                    }
                }

                var configuredDvrProfileExists = dvrProfileNames.Any(name => string.Equals(name, config.RecordingProfile, StringComparison.OrdinalIgnoreCase));
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

        return scoreDeductions;
    }

    private void CheckFfmpegSettings(DiagnoseResult report, Jellyfin.Plugin.TvHeadendApi.Configuration.PluginConfiguration config)
    {
        var (_, jellyfinAnalyzeDuration) = _encodingOptionsReader.ReadFfmpegSettings(_serverConfigManager, _logger);

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
    }

    private int CheckProbeCacheStatus(DiagnoseResult report, Jellyfin.Plugin.TvHeadendApi.Configuration.PluginConfiguration config, HashSet<string> allChannelUuids)
    {
        try
        {
            var cachePath = _cachePathProvider.Path;
            var mediaInfoDir = !string.IsNullOrWhiteSpace(cachePath)
                ? Path.Combine(cachePath, "mediainfo")
                : null;

            if (mediaInfoDir != null && Directory.Exists(mediaInfoDir) && allChannelUuids.Count > 0 && _mediaInfoCacheService != null)
            {
                var cachedCount = 0;
                foreach (var channelUuid in allChannelUuids)
                {
                    // Use the same deterministic cache file name computation that
                    // MediaInfoCacheService uses when writing/reading cache files.
                    var cacheFileName = _mediaInfoCacheService.BuildChannelCacheFileName(channelUuid, channelUuid);
                    var cacheFilePath = Path.Combine(mediaInfoDir, cacheFileName);
                    if (File.Exists(cacheFilePath))
                    {
                        cachedCount++;
                    }
                }

                report.CacheStatus = $"{cachedCount}/{allChannelUuids.Count} channels have a Jellyfin mediainfo probe cache file.";
                return AddProbeCacheCoverageCheck(report, config, cachedCount, allChannelUuids.Count);
            }

            report.CacheStatus = mediaInfoDir != null && !Directory.Exists(mediaInfoDir)
                ? "Mediainfo cache directory does not exist yet. Run the cache warm-up on the plugin settings page (or tune to a channel) to create it."
                : allChannelUuids.Count == 0
                    ? "No channel UUIDs available to check probe cache."
                    : _mediaInfoCacheService == null
                        ? "Cache status not available (media info cache service not available)."
                        : "Cache status not available (missing cache path).";
            report.Checks.Add(new DiagnoseCheck { Category = "Cache", Name = "Probe Cache Coverage", Status = "INFO", Message = report.CacheStatus });
            return 0;
        }
        catch (Exception ex)
        {
            report.CacheStatus = $"Could not check probe cache: {ex.Message}";
            report.Checks.Add(new DiagnoseCheck { Category = "Cache", Name = "Probe Cache Coverage", Status = "WARNING", Message = report.CacheStatus });
            return 0;
        }
    }

    /// <summary>
    /// Adds the probe-cache coverage check and returns the proportional score deduction.
    /// Coverage is weighted into the compatibility score because the MediaInfo probe cache is
    /// what keeps channel starts on the fast Direct Play path — the plugin's core purpose.
    /// </summary>
    /// <param name="report">The diagnostics report being built.</param>
    /// <param name="config">The current plugin configuration.</param>
    /// <param name="cachedCount">Number of channels with a probe cache file.</param>
    /// <param name="totalCount">Total number of channels.</param>
    /// <returns>The score deduction (0 when coverage is complete or probing is disabled).</returns>
    private static int AddProbeCacheCoverageCheck(DiagnoseResult report, Jellyfin.Plugin.TvHeadendApi.Configuration.PluginConfiguration config, int cachedCount, int totalCount)
    {
        // The cache is effective only when probing is enabled — SupportsProbing is what the
        // media source build actually consults (the old EnableMediaInfoCacheWrite flag is
        // deprecated and has no backend effect).
        if (!config.SupportsProbing)
        {
            report.Checks.Add(new DiagnoseCheck
            {
                Category = "Cache",
                Name = "Probe Cache Coverage",
                Status = "INFO",
                Message = report.CacheStatus + " Probing is disabled in the plugin settings, so the MediaInfo cache is inactive.",
                Recommendation = "Enable probing (SupportsProbing) in the plugin settings to speed up channel starts.",
            });
            return 0;
        }

        var coverage = (double)cachedCount / totalCount;
        var status = coverage >= ProbeCacheOkCoverage ? "OK"
            : coverage >= ProbeCacheErrorCoverage ? "WARNING"
            : "ERROR";
        var deduction = (int)Math.Round((1 - coverage) * ProbeCacheMaxScoreDeduction);

        report.Checks.Add(new DiagnoseCheck
        {
            Category = "Cache",
            Name = "Probe Cache Coverage",
            Status = status,
            Message = report.CacheStatus,
            Recommendation = status == "OK"
                ? "Probe cache coverage is high. Channel switching should be fast."
                : $"{totalCount - cachedCount} channel(s) have no probe cache file yet, which slows their channel start. Run the cache warm-up on the plugin settings page (or tune each channel once) to build full coverage.",
        });

        return deduction;
    }

    private async Task<bool?> GetCodecProfileBoolSettingAsync(HttpClient httpClient, Jellyfin.Plugin.TvHeadendApi.Configuration.PluginConfiguration config, string baseUrl, string webRoot, string codecProfileRef, string settingName, CancellationToken cancellationToken)
    {
        var codecProfile = await Profile.ProfileMappingHelper.FindCodecProfileEntryByReferenceAsync(_tvheadendApiClient, httpClient, baseUrl, webRoot, codecProfileRef, cancellationToken).ConfigureAwait(false);
        if (codecProfile == null || string.IsNullOrWhiteSpace(codecProfile.Key))
        {
            return null;
        }

        var codecResponse = await LoadIdNodeByUuidAsync(httpClient, config, codecProfile.Key, cancellationToken).ConfigureAwait(false);
        if (codecResponse?.Entries == null || codecResponse.Entries.Length == 0)
        {
            return null;
        }

        var codecEntry = codecResponse.Entries[0];
        var directValue = GetIdNodeProperty(codecEntry, settingName);
        return ReadBoolOrParam(directValue, codecEntry.Params, settingName);
    }

    private async Task<IdNodeLoadResponse?> LoadIdNodeByUuidAsync(HttpClient httpClient, Jellyfin.Plugin.TvHeadendApi.Configuration.PluginConfiguration config, string uuid, CancellationToken cancellationToken)
    {
        var url = _tvheadendUrlBuilder.BuildApiUrl(config, $"api/idnode/load?uuid={Uri.EscapeDataString(uuid)}");
        var body = await _tvheadendApiClient.GetStringAsync(httpClient, url, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<IdNodeLoadResponse>(body, JsonDefaults.Api);
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
        return IdNodeValueHelper.ReadBoolOrParam(directValue, parameters, parameterName);
    }
}
