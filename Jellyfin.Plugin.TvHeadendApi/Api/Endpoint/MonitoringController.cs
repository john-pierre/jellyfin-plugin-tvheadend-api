using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Input;
using Jellyfin.Plugin.TvHeadendApi.Model.Status;
using Jellyfin.Plugin.TvHeadendApi.Model.Subscription;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Input;
using Jellyfin.Plugin.TvHeadendApi.Service.Status;
using Jellyfin.Plugin.TvHeadendApi.Service.Subscription;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TvHeadendApi.Api.Endpoint;

/// <summary>
/// API controller for TVHeadend server monitoring: status, connections, inputs, subscriptions, and health.
/// </summary>
[ApiController]
[Route("TvHeadendApi")]
[Authorize(Policy = Policies.RequiresElevation)]
public class MonitoringController : ControllerBase
{
    private readonly IStatusService _statusService;
    private readonly IInputMonitorService _inputMonitorService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IHealthService _healthService;

    /// <summary>
    /// Initializes a new instance of the <see cref="MonitoringController"/> class.
    /// </summary>
    /// <param name="statusService">Service for TVHeadend server status and connections.</param>
    /// <param name="inputMonitorService">Service for TVHeadend input/tuner monitoring.</param>
    /// <param name="subscriptionService">Service for TVHeadend subscription monitoring.</param>
    /// <param name="healthService">TVHeadend health tracking service.</param>
    public MonitoringController(
        IStatusService statusService,
        IInputMonitorService inputMonitorService,
        ISubscriptionService subscriptionService,
        IHealthService healthService)
    {
        ArgumentNullException.ThrowIfNull(statusService);
        ArgumentNullException.ThrowIfNull(inputMonitorService);
        ArgumentNullException.ThrowIfNull(subscriptionService);
        ArgumentNullException.ThrowIfNull(healthService);
        _statusService = statusService;
        _inputMonitorService = inputMonitorService;
        _subscriptionService = subscriptionService;
        _healthService = healthService;
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

    /// <summary>
    /// Returns the current TVHeadend upstream health snapshot.
    /// </summary>
    /// <returns>Health snapshot with status, circuit breaker state, and failure details.</returns>
    [HttpGet("Health")]
    public ActionResult<HealthSnapshot> GetHealth()
    {
        return Ok(_healthService.GetSnapshot());
    }

    /// <summary>
    /// Triggers an active TVHeadend health check and returns the updated snapshot.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated health snapshot after the check.</returns>
    [HttpPost("Health/Check")]
    public async Task<ActionResult<HealthSnapshot>> CheckHealth(CancellationToken cancellationToken)
    {
        return Ok(await _healthService.CheckHealthAsync(cancellationToken).ConfigureAwait(false));
    }
}
