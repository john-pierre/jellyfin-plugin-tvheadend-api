// REST endpoints for streaming profile resolution testing and diagnostics.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.Guide;
using Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TvHeadendApi.Api.Endpoint;

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
    private readonly IGuideService _guideService;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamingProfileController"/> class.
    /// </summary>
    /// <param name="resolver">Streaming profile resolver.</param>
    /// <param name="discoveryService">Profile discovery service.</param>
    /// <param name="guideService">Guide service for channel and tag queries.</param>
    public StreamingProfileController(
        IStreamingProfileResolver resolver,
        IProfileDiscoveryService discoveryService,
        IGuideService guideService)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
        _guideService = guideService ?? throw new ArgumentNullException(nameof(guideService));
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

    /// <summary>
    /// Returns all enabled channels from TVHeadend for use in configuration dropdowns.
    /// Each entry contains the channel UUID and display name.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Channel list sorted by name.</returns>
    [HttpGet("Channels")]
    public async Task<ActionResult<IReadOnlyList<ChannelOption>>> GetChannels(CancellationToken cancellationToken)
    {
        var channels = await _guideService.GetChannelsAsync(cancellationToken).ConfigureAwait(false);
        var options = channels
            .Where(c => !string.IsNullOrWhiteSpace(c.Id) && !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => new ChannelOption(c.Id, c.Name))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Ok(options);
    }

    /// <summary>
    /// Returns all channel tags/groups from TVHeadend for use in configuration dropdowns.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Channel group list sorted by name.</returns>
    [HttpGet("ChannelGroups")]
    public async Task<ActionResult<IReadOnlyList<ChannelGroupOption>>> GetChannelGroups(CancellationToken cancellationToken)
    {
        var tags = await _guideService.GetChannelTagsAsync(cancellationToken).ConfigureAwait(false);
        var options = tags
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => new ChannelGroupOption(kv.Key, kv.Value))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Ok(options);
    }
}
