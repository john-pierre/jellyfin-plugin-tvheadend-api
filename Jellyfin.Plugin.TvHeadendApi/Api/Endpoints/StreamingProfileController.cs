// REST endpoints for streaming profile resolution testing and diagnostics.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TvHeadendApi.Api.Endpoints;

/// <summary>
/// Admin endpoints for streaming profile selection diagnostics, discovery, and resolution testing.
/// </summary>
[ApiController]
[Route("TvHeadendApi/StreamingProfiles")]
[Authorize(Policy = Policies.RequiresElevation)]
public class StreamingProfileController : ControllerBase
{
    private readonly IStreamingProfileResolver _resolver;
    private readonly IProfileDiscoveryService _discoveryService;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamingProfileController"/> class.
    /// </summary>
    /// <param name="resolver">Streaming profile resolver.</param>
    /// <param name="discoveryService">Profile discovery service.</param>
    public StreamingProfileController(
        IStreamingProfileResolver resolver,
        IProfileDiscoveryService discoveryService)
    {
        _resolver = resolver ?? throw new System.ArgumentNullException(nameof(resolver));
        _discoveryService = discoveryService ?? throw new System.ArgumentNullException(nameof(discoveryService));
    }

    /// <summary>
    /// Tests profile resolution for a given context without affecting playback.
    /// Returns the effective profile, playback mode, matched rule, and debug reasons.
    /// </summary>
    /// <param name="channelId">Optional TVHeadend channel UUID.</param>
    /// <param name="channelGroup">Optional channel group name.</param>
    /// <param name="clientName">Optional Jellyfin client name.</param>
    /// <param name="deviceName">Optional Jellyfin device name.</param>
    /// <param name="userId">Optional Jellyfin user ID.</param>
    /// <returns>The resolution result with debug information.</returns>
    [HttpGet("Resolve")]
    public ActionResult<StreamingProfileResolutionResult> Resolve(
        [FromQuery] string? channelId = null,
        [FromQuery] string? channelGroup = null,
        [FromQuery] string? clientName = null,
        [FromQuery] string? deviceName = null,
        [FromQuery] string? userId = null)
    {
        var context = new StreamingProfileContext
        {
            ChannelId = channelId,
            ChannelGroup = channelGroup,
            ClientName = clientName,
            DeviceName = deviceName,
            UserId = userId,
        };

        var result = _resolver.Resolve(context);
        return Ok(result);
    }

    /// <summary>
    /// Returns the list of streaming profiles discovered from TVHeadend.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Available TVHeadend streaming profiles.</returns>
    [HttpGet("Discovered")]
    public async Task<ActionResult<IReadOnlyList<DiscoveredProfile>>> GetDiscoveredProfiles(CancellationToken cancellationToken)
    {
        var profiles = await _discoveryService.GetAvailableProfilesAsync(cancellationToken).ConfigureAwait(false);
        return Ok(profiles);
    }

    /// <summary>
    /// Validates all configured profile names against discovered TVHeadend profiles.
    /// Returns warnings for any configured profile names that do not exist in TVHeadend.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation warnings (empty array if all profiles are valid).</returns>
    [HttpGet("Validate")]
    public async Task<ActionResult<IReadOnlyList<string>>> ValidateProfiles(CancellationToken cancellationToken)
    {
        var warnings = await _discoveryService.ValidateConfiguredProfilesAsync(cancellationToken).ConfigureAwait(false);
        return Ok(warnings);
    }

    /// <summary>
    /// Forces a refresh of the discovered profile cache.
    /// </summary>
    /// <returns>Confirmation message.</returns>
    [HttpPost("RefreshCache")]
    public ActionResult RefreshCache()
    {
        _discoveryService.InvalidateCache();
        return Ok(new { Message = "Profile discovery cache invalidated. Next request will fetch fresh data." });
    }
}
