using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostics;
using Jellyfin.Plugin.TvHeadendApi.Service.Images;
using Jellyfin.Plugin.TvHeadendApi.Service.Profiles;
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
public class TvHeadendApiController : ControllerBase
{
    private readonly IImageProxyService _imageProxyService;
    private readonly IDiagnoseService _diagnoseService;
    private readonly IProfileProvisioningService _profileProvisioningService;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvHeadendApiController"/> class.
    /// </summary>
    /// <param name="imageProxyService">Service that proxies TVHeadend image requests.</param>
    /// <param name="diagnoseService">Service that builds the diagnostic report.</param>
    /// <param name="profileProvisioningService">Service that provisions recommended TVHeadend profiles.</param>
    public TvHeadendApiController(
        IImageProxyService imageProxyService,
        IDiagnoseService diagnoseService,
        IProfileProvisioningService profileProvisioningService)
    {
        _imageProxyService = imageProxyService ?? throw new ArgumentNullException(nameof(imageProxyService));
        _diagnoseService = diagnoseService ?? throw new ArgumentNullException(nameof(diagnoseService));
        _profileProvisioningService = profileProvisioningService ?? throw new ArgumentNullException(nameof(profileProvisioningService));
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
    /// Proxies image requests from Jellyfin to TVHeadend, ensuring all image access goes through
    /// Jellyfin's authorization layer instead of exposing direct credentials or tokens.
    /// This endpoint accepts an image path and returns the raw image data from TVHeadend.
    /// </summary>
    /// <param name="imagePath">The TVHeadend image endpoint relative path, e.g., "imagecache/1715".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The image data as a stream if successful; 404 or 500 error otherwise.</returns>
    [HttpGet("ImageProxy")]
    public async Task<IActionResult> GetImageProxy([FromQuery] string? imagePath, CancellationToken cancellationToken)
    {
        return await _imageProxyService.ProxyImageAsync(imagePath, cancellationToken).ConfigureAwait(false);
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
        return Ok(await _profileProvisioningService.CreateProfileAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Generates a new authentication token from TVHeadend and stores it in the plugin configuration.
    /// Requires TVHeadend admin privileges.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The token generation result with the new auth token.</returns>
    [HttpPost("GenerateAuthToken")]
    public async Task<ActionResult<AuthTokenGenerationResult>> GenerateAuthToken(CancellationToken cancellationToken)
    {
        return Ok(await _profileProvisioningService.GenerateAuthTokenAsync(cancellationToken).ConfigureAwait(false));
    }
}
