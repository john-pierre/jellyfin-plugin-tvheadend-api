using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Dashboard;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Input;
using Jellyfin.Plugin.TvHeadendApi.Service.Status;
using Jellyfin.Plugin.TvHeadendApi.Service.Subscription;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Dashboard;

/// <summary>
/// Aggregates data from multiple TVHeadend services into a single dashboard snapshot.
/// Each section is fetched independently so a failure in one does not break the others.
/// </summary>
internal sealed class DashboardService : IDashboardService
{
    private readonly IDiagnosticService _diagnosticService;
    private readonly IStatusService _statusService;
    private readonly IInputMonitorService _inputMonitorService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly ILogger<DashboardService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DashboardService"/> class.
    /// </summary>
    /// <param name="diagnosticService">Service that builds diagnostic reports.</param>
    /// <param name="statusService">Service for TVHeadend server status and connections.</param>
    /// <param name="inputMonitorService">Service for TVHeadend input/tuner monitoring.</param>
    /// <param name="subscriptionService">Service for TVHeadend subscription monitoring.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public DashboardService(
        IDiagnosticService diagnosticService,
        IStatusService statusService,
        IInputMonitorService inputMonitorService,
        ISubscriptionService subscriptionService,
        ILogger<DashboardService> logger)
    {
        _diagnosticService = diagnosticService ?? throw new ArgumentNullException(nameof(diagnosticService));
        _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
        _inputMonitorService = inputMonitorService ?? throw new ArgumentNullException(nameof(inputMonitorService));
        _subscriptionService = subscriptionService ?? throw new ArgumentNullException(nameof(subscriptionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<DashboardStatus> GetDashboardStatusAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Dashboard status query started");

        var dashboard = new DashboardStatus
        {
            PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown",
            Timestamp = DateTimeOffset.UtcNow,
        };

        // 1. Diagnostics — provides connection status, server version, latency, channel/DVR counts.
        try
        {
            var diag = await _diagnosticService.DiagnoseAsync(cancellationToken).ConfigureAwait(false);
            dashboard.IsReachable = diag.OverallStatus != "ERROR" || !string.IsNullOrEmpty(diag.ServerVersion);
            dashboard.IsAuthenticated = dashboard.IsReachable && diag.OverallStatus != "ERROR";
            dashboard.ServerVersion = diag.ServerVersion;
            dashboard.LatencyMs = diag.LatencyMs;
            dashboard.ChannelCount = diag.ChannelCount;
            dashboard.DvrEntryCount = diag.DvrEntryCount;
            dashboard.CompatibilityScore = diag.CompatibilityScore;
            dashboard.DiagnosticStatus = diag.OverallStatus;
            dashboard.BaseUrl = diag.Connection;
            dashboard.Warnings = diag.Warnings.ToList().AsReadOnly();

            if (diag.OverallStatus == "ERROR")
            {
                dashboard.ConnectionError = diag.Connection;
            }

            _logger.LogDebug("Dashboard diagnostics completed: {Status}, score {Score}", diag.OverallStatus, diag.CompatibilityScore);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard diagnostics query failed");
            dashboard.ConnectionError = ex.Message;
        }

        // 2. Activity status
        try
        {
            dashboard.Activity = await _statusService.GetActivityStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard activity status query failed");
        }

        // 3. Inputs / tuners
        try
        {
            dashboard.Inputs = await _inputMonitorService.GetInputStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard tuner/input query failed");
            dashboard.InputsError = ex.Message;
        }

        // 4. Subscriptions
        try
        {
            dashboard.Subscriptions = await _subscriptionService.GetActiveSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard subscription query failed");
            dashboard.SubscriptionsError = ex.Message;
        }

        // 5. Connections
        try
        {
            dashboard.Connections = await _statusService.GetConnectionsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard connection query failed");
            dashboard.ConnectionsError = ex.Message;
        }

        _logger.LogDebug(
            "Dashboard status query completed: reachable={Reachable}, inputs={InputCount}, subs={SubCount}, conns={ConnCount}",
            dashboard.IsReachable,
            dashboard.Inputs.Count,
            dashboard.Subscriptions.Count,
            dashboard.Connections.Count);

        return dashboard;
    }
}
