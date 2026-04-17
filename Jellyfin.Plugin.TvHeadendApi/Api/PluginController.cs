using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Auth;
using Jellyfin.Plugin.TvHeadendApi.Model.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Model.Input;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Jellyfin.Plugin.TvHeadendApi.Model.Statistics;
using Jellyfin.Plugin.TvHeadendApi.Model.Status;
using Jellyfin.Plugin.TvHeadendApi.Model.Subscription;
using Jellyfin.Plugin.TvHeadendApi.Service.Auth;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Input;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Statistics;
using Jellyfin.Plugin.TvHeadendApi.Service.Status;
using Jellyfin.Plugin.TvHeadendApi.Service.Subscription;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
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
    private readonly IDiagnosticService _diagnoseService;
    private readonly IDefaultProfileService _defaultProfileService;
    private readonly ITokenService _tokenService;
    private readonly IStatisticsService _statisticsService;
    private readonly IStatusService _statusService;
    private readonly IInputMonitorService _inputMonitorService;
    private readonly ISubscriptionService _subscriptionService;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginController"/> class.
    /// </summary>
    /// <param name="diagnoseService">Service that builds the diagnostic report.</param>
    /// <param name="defaultProfileService">Service that provisions recommended TVHeadend default profiles.</param>
    /// <param name="tokenService">Service that manages TVHeadend auth tokens.</param>
    /// <param name="statisticsService">Service that tracks live TV viewing statistics.</param>
    /// <param name="statusService">Service for TVHeadend server status and connections.</param>
    /// <param name="inputMonitorService">Service for TVHeadend input/tuner monitoring.</param>
    /// <param name="subscriptionService">Service for TVHeadend subscription monitoring.</param>
    public PluginController(
        IDiagnosticService diagnoseService,
        IDefaultProfileService defaultProfileService,
        ITokenService tokenService,
        IStatisticsService statisticsService,
        IStatusService statusService,
        IInputMonitorService inputMonitorService,
        ISubscriptionService subscriptionService)
    {
        _diagnoseService = diagnoseService ?? throw new ArgumentNullException(nameof(diagnoseService));
        _defaultProfileService = defaultProfileService ?? throw new ArgumentNullException(nameof(defaultProfileService));
        _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        _statisticsService = statisticsService ?? throw new ArgumentNullException(nameof(statisticsService));
        _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
        _inputMonitorService = inputMonitorService ?? throw new ArgumentNullException(nameof(inputMonitorService));
        _subscriptionService = subscriptionService ?? throw new ArgumentNullException(nameof(subscriptionService));
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
    /// Performs a comprehensive TVHeadend diagnostic and returns a structured report.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A structured diagnostic report with compatibility score.</returns>
    [HttpGet("Diagnose")]
    public async Task<ActionResult<DiagnoseResult>> Diagnose(CancellationToken cancellationToken)
    {
        return Ok(await _diagnoseService.DiagnoseAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Creates an optimised set of profiles in TVHeadend for fast channel switching:
    /// 1. A video codec profile "jellyfin-h264" (H.264 / libx264, 5 Mbps cap, faster preset, zerolatency tune, deinterlace)
    /// 2. An audio codec profile "jellyfin-aac" (AAC, 128 kbps)
    /// 3. A streaming transcode profile "jellyfin" that references both codec profiles in an MP4 container.
    /// If any of these already exist, they are skipped.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A status message indicating success or failure.</returns>
    [HttpPost("CreateProfile")]
    public async Task<ActionResult<ProfileDetectionResult>> CreateProfile(CancellationToken cancellationToken)
    {
        return Ok(await _defaultProfileService.CreateProfileAsync(cancellationToken).ConfigureAwait(false));
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
        var diagnose = await _diagnoseService.DiagnoseAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new ProfileOptionsResult
        {
            StreamingProfiles = diagnose.AvailableStreamingProfiles.ToArray(),
            RecordingProfiles = diagnose.AvailableRecordingProfiles.ToArray(),
        });
    }

    /// <summary>
    /// Returns live TV viewing statistics for the given time range.
    /// </summary>
    /// <param name="days">Number of days to look back. 0 = all history.</param>
    /// <returns>Viewing sessions with user, device, channel, and play method data.</returns>
    [HttpGet("Statistics")]
    public ActionResult<ViewingStatisticsResult> GetStatistics([FromQuery] int days = 30)
    {
        return Ok(_statisticsService.GetStatistics(days));
    }

    /// <summary>
    /// Clears all recorded viewing statistics.
    /// </summary>
    /// <returns>Success confirmation.</returns>
    [HttpDelete("Statistics")]
    public ActionResult ClearStatistics()
    {
        _statisticsService.ClearStatistics();
        return Ok(new { Success = true, Message = "Viewing statistics cleared." });
    }

    /// <summary>
    /// Returns the TVHeadend server activity status (connection/subscription counts, next activity).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Server activity status.</returns>
    [HttpGet("Status")]
    public async Task<ActionResult<ActivityStatus>> GetStatus(CancellationToken cancellationToken)
    {
        var result = await _statusService.GetActivityStatusAsync(cancellationToken).ConfigureAwait(false);
        return result != null ? Ok(result) : BadRequest(new { Message = "Plugin configuration is not available." });
    }

    /// <summary>
    /// Returns the list of active client connections to TVHeadend.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Active connections.</returns>
    [HttpGet("Connections")]
    public async Task<ActionResult<IReadOnlyList<ConnectionEntry>>> GetConnections(CancellationToken cancellationToken)
    {
        return Ok(await _statusService.GetConnectionsAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Returns the status of all TV inputs (tuners/adapters) in TVHeadend.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Input status entries.</returns>
    [HttpGet("Inputs")]
    public async Task<ActionResult<IReadOnlyList<InputStatusEntry>>> GetInputs(CancellationToken cancellationToken)
    {
        return Ok(await _inputMonitorService.GetInputStatusAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Returns the list of active streaming subscriptions in TVHeadend.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Active subscription entries.</returns>
    [HttpGet("Subscriptions")]
    public async Task<ActionResult<IReadOnlyList<SubscriptionEntry>>> GetSubscriptions(CancellationToken cancellationToken)
    {
        return Ok(await _subscriptionService.GetActiveSubscriptionsAsync(cancellationToken).ConfigureAwait(false));
    }
}
