using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Auth;
using Jellyfin.Plugin.TvHeadendApi.Model.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Auth;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Stream;
using Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TvHeadendApi.Api;

/// <summary>
/// API controller that exposes helper endpoints for the plugin configuration page.
/// </summary>
[ApiController]
[Route("TvHeadendApi")]
[Authorize(Policy = Policies.RequiresElevation)]
public class PluginController : ControllerBase
{
    private readonly IDiagnosticService _diagnosticService;
    private readonly IDefaultProfileService _defaultProfileService;
    private readonly ITokenService _tokenService;
    private readonly IMediaInfoCacheService _cacheService;
    private readonly IProfileContainerResolver? _containerResolver;
    private readonly IProfileDiscoveryService? _profileDiscoveryService;
    private readonly IKnownClientsService? _knownClientsService;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginController"/> class.
    /// </summary>
    /// <param name="diagnosticService">Service that builds the diagnostic report.</param>
    /// <param name="defaultProfileService">Service that provisions recommended TVHeadend default profiles.</param>
    /// <param name="tokenService">Service that manages TVHeadend auth tokens.</param>
    /// <param name="cacheService">Service that manages mediainfo cache warmup and invalidation.</param>
    /// <param name="containerResolver">Optional profile container/codec snapshot cache to invalidate together with the mediainfo cache.</param>
    /// <param name="profileDiscoveryService">Optional discovered-profile-list cache to invalidate together with the mediainfo cache.</param>
    /// <param name="knownClientsService">Optional aggregator of known Jellyfin users, clients, and devices for the rule editor.</param>
    public PluginController(
        IDiagnosticService diagnosticService,
        IDefaultProfileService defaultProfileService,
        ITokenService tokenService,
        IMediaInfoCacheService cacheService,
        IProfileContainerResolver? containerResolver = null,
        IProfileDiscoveryService? profileDiscoveryService = null,
        IKnownClientsService? knownClientsService = null)
    {
        ArgumentNullException.ThrowIfNull(diagnosticService);
        ArgumentNullException.ThrowIfNull(defaultProfileService);
        ArgumentNullException.ThrowIfNull(tokenService);
        ArgumentNullException.ThrowIfNull(cacheService);
        _diagnosticService = diagnosticService;
        _defaultProfileService = defaultProfileService;
        _tokenService = tokenService;
        _cacheService = cacheService;
        _containerResolver = containerResolver;
        _profileDiscoveryService = profileDiscoveryService;
        _knownClientsService = knownClientsService;
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
        // Framework constraint: Plugin.Instance is required here for SaveConfiguration/UpdateConfiguration
        // which are instance methods on the Jellyfin BasePlugin class and cannot be injected.
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return BadRequest(new ProfileDetectionResult { Success = false, Message = "Plugin instance is not available." });
        }

        plugin.UpdateConfiguration(new Configuration.PluginConfiguration());

        return Ok(new ProfileDetectionResult
        {
            Success = true,
            Message = "Configuration has been reset to defaults."
        });
    }

    /// <summary>
    /// Performs a comprehensive TVHeadend diagnostic and returns a structured report.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A structured diagnostic report with compatibility score.</returns>
    [HttpGet("Diagnose")]
    public async Task<ActionResult<DiagnoseResult>> Diagnose(CancellationToken cancellationToken)
    {
        return Ok(await _diagnosticService.DiagnoseAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Provisions the plugin-managed TVHeadend streaming profile for fast, deterministic playback:
    /// 1. A video codec profile "jellyfin-h264" using the backend's best detected H.264 encoder
    ///    (libx264 / VAAPI / QuickSync / NVENC / V4L2 …), with auto profile/level and no bitrate cap.
    /// 2. An audio codec profile "jellyfin-aac".
    /// 3. A "smart-copy" transcode profile "jellyfin" (MPEG-TS) that copies streams already in the
    ///    target codec and transcodes only foreign codecs, yielding a fixed H.264/AAC output.
    /// Existing profiles are updated in place. On success the plugin's streaming profile is set to
    /// "jellyfin" and stale mediainfo caches are cleared so the deterministic cache rebuilds correctly.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A status message indicating success or failure.</returns>
    [HttpPost("CreateProfile")]
    public async Task<ActionResult<ProfileDetectionResult>> CreateProfile(CancellationToken cancellationToken)
    {
        var result = await _defaultProfileService.CreateProfileAsync(cancellationToken).ConfigureAwait(false);

        // On success, make the managed profile the active default. The effective profile is resolved
        // from StreamingProfileSettings first: in Auto/TvHeadendTranscode mode the resolver reads
        // DefaultTvHeadendProfile, falling back to the legacy StreamingProfile only when it is empty.
        // We must set DefaultTvHeadendProfile (it is often "pass") AND the legacy field. Per-channel,
        // client and user rules still override this global default.
        if (result.Success && !string.IsNullOrWhiteSpace(result.ProfileName))
        {
            var plugin = Plugin.Instance;
            if (plugin != null)
            {
                var config = plugin.Configuration;
                var settings = config.StreamingProfileSettings;
                var changed = false;

                if (!string.Equals(settings.DefaultTvHeadendProfile, result.ProfileName, StringComparison.OrdinalIgnoreCase))
                {
                    settings.DefaultTvHeadendProfile = result.ProfileName;
                    changed = true;
                }

                if (!string.Equals(config.StreamingProfile, result.ProfileName, StringComparison.OrdinalIgnoreCase))
                {
                    config.StreamingProfile = result.ProfileName;
                    changed = true;
                }

                if (changed)
                {
                    plugin.UpdateConfiguration(config);

                    // Drop caches written for the previous profile so they rebuild against the new one.
                    await _cacheService.InvalidateAllCachesAsync().ConfigureAwait(false);
                }
            }
        }

        return Ok(result);
    }

    /// <summary>
    /// Generates a new authentication token from TVHeadend and stores it in the plugin configuration.
    /// Uses the configured TVHeadend user credentials.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The token generation result with the new auth token.</returns>
    [HttpPost("GenerateAuthToken")]
    public async Task<ActionResult<AuthTokenGenerationResult>> GenerateAuthToken(CancellationToken cancellationToken)
    {
        return Ok(await _tokenService.GenerateAndStoreTokenAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Returns available streaming and DVR profiles for configuration dropdowns.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Streaming and recording profile names available in TVHeadend.</returns>
    [HttpGet("ProfileOptions")]
    public async Task<ActionResult<ProfileOptionsResult>> GetProfileOptions(CancellationToken cancellationToken)
    {
        var diagnose = await _diagnosticService.DiagnoseAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new ProfileOptionsResult
        {
            StreamingProfiles = diagnose.AvailableStreamingProfiles.ToArray(),
            RecordingProfiles = diagnose.AvailableRecordingProfiles.ToArray(),
        });
    }

    /// <summary>
    /// Warms the mediainfo cache for all known channels so that the first tune is fast.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A summary of how many channels were warmed, skipped, or failed.</returns>
    [HttpPost("WarmCache")]
    public async Task<ActionResult<CacheWarmupResult>> WarmCache(CancellationToken cancellationToken)
    {
        var result = await _cacheService.WarmAllChannelCachesAsync(cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Warms the mediainfo cache for all known channels and streams per-channel progress
    /// as Server-Sent Events (SSE). Each event contains a JSON-serialised
    /// <see cref="CacheWarmupProgress"/>. The final event has <c>event: done</c> and carries
    /// the full <see cref="CacheWarmupResult"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An SSE stream of progress events.</returns>
    [HttpPost("WarmCacheStream")]
    public async Task WarmCacheStream(CancellationToken cancellationToken)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";

        // Use a Channel to serialize writes — Progress<T> callbacks fire on thread-pool
        // threads and Response.WriteAsync is not thread-safe.
        var channel = Channel.CreateUnbounded<CacheWarmupProgress>(
            new UnboundedChannelOptions { SingleReader = true });

        var progress = new Progress<CacheWarmupProgress>(p => channel.Writer.TryWrite(p));

        // Start the warmup on a background task so we can drain the channel on this thread.
        var warmupTask = Task.Run(
            async () =>
            {
                try
                {
                    return await _cacheService.WarmAllChannelCachesAsync(progress, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    channel.Writer.Complete();
                }
            },
            cancellationToken);

        // Single reader loop — all Response writes happen here, sequentially.
        try
        {
            await foreach (var p in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var json = JsonSerializer.Serialize(p);
                await Response.WriteAsync($"data: {json}\n\n", cancellationToken).ConfigureAwait(false);
                await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — expected.
        }

        // Always observe the warmup task — when the client disconnects mid-warmup the task is
        // cancelled cooperatively and awaiting it rethrows. Swallow that like the surrounding
        // cancellation points instead of letting it escape the action as an unhandled exception.
        CacheWarmupResult result;
        try
        {
            result = await warmupTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — expected. No client is listening for the final event.
            return;
        }

        try
        {
            var resultJson = JsonSerializer.Serialize(result);
            await Response.WriteAsync($"event: done\ndata: {resultJson}\n\n", cancellationToken).ConfigureAwait(false);
            await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — expected.
        }
    }

    /// <summary>
    /// Returns the Jellyfin users, client application names, and device names known to this
    /// server. Used by the configuration UI to offer selectable match values for streaming
    /// profile rules instead of free-text entry.
    /// </summary>
    /// <returns>Known users (id + name), client names, and device names — deduplicated and sorted.</returns>
    [HttpGet("KnownClients")]
    public ActionResult<KnownClientsResult> GetKnownClients()
    {
        return Ok(_knownClientsService?.GetKnownClients() ?? new KnownClientsResult());
    }

    /// <summary>
    /// Deletes all mediainfo cache files. Useful after a streaming profile change.
    /// </summary>
    /// <returns>The number of cache files deleted.</returns>
    [HttpPost("InvalidateCache")]
    public async Task<ActionResult> InvalidateCache()
    {
        // Also drop the profile snapshot/discovery caches: rebuilding the mediainfo cache from a
        // stale profile snapshot would silently reintroduce the very state the admin is clearing.
        _containerResolver?.InvalidateCache();
        _profileDiscoveryService?.InvalidateCache();

        var count = await _cacheService.InvalidateAllCachesAsync().ConfigureAwait(false);
        return Ok(new { Success = true, DeletedFiles = count });
    }
}
