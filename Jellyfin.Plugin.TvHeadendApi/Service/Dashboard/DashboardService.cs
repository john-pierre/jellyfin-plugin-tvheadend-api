using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Dashboard;
using Jellyfin.Plugin.TvHeadendApi.Model.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Model.Status;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Database;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Input;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Jellyfin.Plugin.TvHeadendApi.Service.Status;
using Jellyfin.Plugin.TvHeadendApi.Service.Subscription;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Dashboard;

/// <summary>
/// Aggregates data from multiple TVHeadend services into a single dashboard snapshot.
/// Each section is fetched independently so a failure in one does not break the others.
/// Correlates connections and subscriptions into unified sessions for accurate multi-device tracking.
/// </summary>
internal sealed class DashboardService : IDashboardService
{
    private readonly IDiagnosticService _diagnosticService;
    private readonly IStatusService _statusService;
    private readonly IInputMonitorService _inputMonitorService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IUrlBuilder _urlBuilder;
    private readonly IApiClient _apiClient;
    private readonly IHealthService _healthService;
    private readonly DatabaseHealthService _dbHealthService;
    private readonly ConfigurationProvider _configProvider;
    private readonly ILogger<DashboardService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DashboardService"/> class.
    /// </summary>
    /// <param name="diagnosticService">Service that builds diagnostic reports.</param>
    /// <param name="statusService">Service for TVHeadend server status and connections.</param>
    /// <param name="inputMonitorService">Service for TVHeadend input/tuner monitoring.</param>
    /// <param name="subscriptionService">Service for TVHeadend subscription monitoring.</param>
    /// <param name="urlBuilder">URL builder for TVHeadend API URLs.</param>
    /// <param name="apiClient">API client for TVHeadend configuration access.</param>
    /// <param name="healthService">TVHeadend health tracking service.</param>
    /// <param name="dbHealthService">Database health monitoring service.</param>
    /// <param name="configProvider">Plugin configuration provider.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public DashboardService(
        IDiagnosticService diagnosticService,
        IStatusService statusService,
        IInputMonitorService inputMonitorService,
        ISubscriptionService subscriptionService,
        IUrlBuilder urlBuilder,
        IApiClient apiClient,
        IHealthService healthService,
        DatabaseHealthService dbHealthService,
        ConfigurationProvider configProvider,
        ILogger<DashboardService> logger)
    {
        _diagnosticService = diagnosticService ?? throw new ArgumentNullException(nameof(diagnosticService));
        _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
        _inputMonitorService = inputMonitorService ?? throw new ArgumentNullException(nameof(inputMonitorService));
        _subscriptionService = subscriptionService ?? throw new ArgumentNullException(nameof(subscriptionService));
        _urlBuilder = urlBuilder ?? throw new ArgumentNullException(nameof(urlBuilder));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _healthService = healthService ?? throw new ArgumentNullException(nameof(healthService));
        _dbHealthService = dbHealthService ?? throw new ArgumentNullException(nameof(dbHealthService));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
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

        // Short-circuit: if TVHeadend credentials are not configured, return immediately
        // without contacting the TVHeadend backend.
        if (IsSetupRequired())
        {
            dashboard.RequiresSetup = true;
            _logger.LogInformation("Dashboard: plugin requires setup — skipping all TVHeadend backend calls");
            return dashboard;
        }

        await PopulateDiagnosticsAsync(dashboard, cancellationToken).ConfigureAwait(false);
        await PopulateActivityAsync(dashboard, cancellationToken).ConfigureAwait(false);
        await PopulateInputsAsync(dashboard, cancellationToken).ConfigureAwait(false);
        await PopulateSubscriptionsAsync(dashboard, cancellationToken).ConfigureAwait(false);
        await PopulateConnectionsAsync(dashboard, cancellationToken).ConfigureAwait(false);

        // Attach upstream health snapshot
        dashboard.UpstreamHealth = _healthService.GetSnapshot();

        // Attach database health snapshot
        dashboard.DatabaseHealth = _dbHealthService.GetSnapshot();

        if (dashboard.Activity == null)
        {
            dashboard.Activity = new ActivityStatus
            {
                SubscriptionCount = dashboard.Subscriptions.Count,
                ConnectionCount = dashboard.Connections.Count,
            };
        }

        _logger.LogDebug(
            "Dashboard status query completed: reachable={Reachable}, inputs={InputCount}, subs={SubCount}, conns={ConnCount}",
            dashboard.IsReachable,
            dashboard.Inputs.Count,
            dashboard.Subscriptions.Count,
            dashboard.Connections.Count);

        return dashboard;
    }

    private async Task PopulateDiagnosticsAsync(DashboardStatus dashboard, CancellationToken cancellationToken)
    {
        try
        {
            var diagnoseResult = await _diagnosticService.DiagnoseAsync(cancellationToken).ConfigureAwait(false);
            ApplyDiagnosticResult(dashboard, diagnoseResult);
            _logger.LogDebug(
                "Dashboard diagnostics completed: {Status}, score {Score}",
                diagnoseResult.OverallStatus,
                diagnoseResult.CompatibilityScore);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard diagnostics query failed");
            dashboard.ConnectionError = ex.Message;
        }
    }

    private async Task PopulateActivityAsync(DashboardStatus dashboard, CancellationToken cancellationToken)
    {
        try
        {
            dashboard.Activity = await _statusService.GetActivityStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard activity status query failed");
        }
    }

    private async Task PopulateInputsAsync(DashboardStatus dashboard, CancellationToken cancellationToken)
    {
        try
        {
            dashboard.Inputs = await _inputMonitorService.GetInputStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard tuner/input query failed");
            dashboard.InputsError = ex.Message;
        }
    }

    private async Task PopulateSubscriptionsAsync(DashboardStatus dashboard, CancellationToken cancellationToken)
    {
        try
        {
            dashboard.Subscriptions = await _subscriptionService.GetActiveSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard subscription query failed");
            dashboard.SubscriptionsError = ex.Message;
        }
    }

    private async Task PopulateConnectionsAsync(DashboardStatus dashboard, CancellationToken cancellationToken)
    {
        try
        {
            dashboard.Connections = await _statusService.GetConnectionsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard connection query failed");
            dashboard.ConnectionsError = ex.Message;
        }
    }

    private void ApplyDiagnosticResult(DashboardStatus dashboard, DiagnoseResult diagnoseResult)
    {
        dashboard.IsReachable = diagnoseResult.OverallStatus != "ERROR" || !string.IsNullOrEmpty(diagnoseResult.ServerVersion);
        dashboard.IsAuthenticated = dashboard.IsReachable && diagnoseResult.OverallStatus != "ERROR";
        dashboard.ServerVersion = diagnoseResult.ServerVersion;
        dashboard.LatencyMs = diagnoseResult.LatencyMs;
        dashboard.ChannelCount = diagnoseResult.ChannelCount;
        dashboard.DvrEntryCount = diagnoseResult.DvrEntryCount;
        dashboard.CompatibilityScore = diagnoseResult.CompatibilityScore;
        dashboard.DiagnosticStatus = diagnoseResult.OverallStatus;
        dashboard.BaseUrl = _apiClient.GetCurrentConfiguration() is { } config
            ? _urlBuilder.GetBaseUrl(config)
            : diagnoseResult.Connection;
        dashboard.Warnings = diagnoseResult.Warnings.ToList().AsReadOnly();

        if (diagnoseResult.OverallStatus == "ERROR")
        {
            dashboard.ConnectionError = diagnoseResult.Connection;
        }
    }

    /// <summary>
    /// Determines whether the plugin requires initial setup.
    /// Returns <c>true</c> when either the configuration is unavailable or
    /// no valid TVHeadend credentials are configured (no username/password and
    /// anonymous access is disabled).
    /// </summary>
    private bool IsSetupRequired()
    {
        var config = _configProvider.Configuration;
        if (config is null)
        {
            return true;
        }

        // If anonymous access is allowed, credentials are not required.
        if (config.AllowAnonymousAccess)
        {
            return false;
        }

        // Credentials are required but missing.
        return string.IsNullOrWhiteSpace(config.Username)
            && string.IsNullOrWhiteSpace(config.Password);
    }
}
