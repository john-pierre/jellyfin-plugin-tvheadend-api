using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Auth;
using Jellyfin.Plugin.TvHeadendApi.Model.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Auth;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
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
    private readonly IDiagnosticService _diagnosticService;
    private readonly IDefaultProfileService _defaultProfileService;
    private readonly ITokenService _tokenService;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginController"/> class.
    /// </summary>
    /// <param name="diagnosticService">Service that builds the diagnostic report.</param>
    /// <param name="defaultProfileService">Service that provisions recommended TVHeadend default profiles.</param>
    /// <param name="tokenService">Service that manages TVHeadend auth tokens.</param>
    public PluginController(
        IDiagnosticService diagnosticService,
        IDefaultProfileService defaultProfileService,
        ITokenService tokenService)
    {
        ArgumentNullException.ThrowIfNull(diagnosticService);
        ArgumentNullException.ThrowIfNull(defaultProfileService);
        ArgumentNullException.ThrowIfNull(tokenService);
        _diagnosticService = diagnosticService;
        _defaultProfileService = defaultProfileService;
        _tokenService = tokenService;
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
        var diagnose = await _diagnosticService.DiagnoseAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new ProfileOptionsResult
        {
            StreamingProfiles = diagnose.AvailableStreamingProfiles.ToArray(),
            RecordingProfiles = diagnose.AvailableRecordingProfiles.ToArray(),
        });
    }
}
