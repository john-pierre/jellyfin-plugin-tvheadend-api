using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Statistic;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Database;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Jellyfin.Plugin.TvHeadendApi.Service.Statistic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

// Central TVHeadend health tracking service — monitors upstream connectivity and exposes health state.

namespace Jellyfin.Plugin.TvHeadendApi.Service.Health;

/// <summary>
/// Central singleton that tracks TVHeadend upstream health state, circuit breaker, and degraded mode.
/// Thread-safe. Used by all services and the dashboard.
/// Persists health transitions to SQLite for trend analysis.
/// </summary>
internal sealed class HealthService : IHealthService
{
    // Default circuit breaker thresholds (overridable via config)
    private const int DefaultCircuitOpenThreshold = 5;
    private static readonly TimeSpan DefaultCircuitOpenDuration = TimeSpan.FromSeconds(30);

    private readonly object _lock = new();
    private readonly IApiClient _apiClient;
    private readonly IUrlBuilder _urlBuilder;
    private readonly ILogger<HealthService> _logger;
    private readonly ConfigurationProvider? _configProvider;
    private readonly DatabaseProvider? _databaseProvider;
    private readonly DatabaseWriteCoordinator? _writeCoordinator;
    private DbContextOptions<ViewingSessionContext>? _lazyDbContextOptions;

    // Mutable state — guarded by _lock
    private HealthStatus _status = HealthStatus.Unknown;
    private CircuitState _circuitState = CircuitState.Closed;
    private DateTimeOffset? _lastSuccessUtc;
    private DateTimeOffset? _lastFailureUtc;
    private FailureReason _lastFailureReason = FailureReason.None;
    private int _consecutiveFailures;
    private int? _lastResponseTimeMs;
    private DateTimeOffset _circuitOpenUntilUtc = DateTimeOffset.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="HealthService"/> class.
    /// </summary>
    /// <param name="apiClient">TVHeadend API client.</param>
    /// <param name="urlBuilder">TVHeadend URL builder.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="configProvider">Optional configuration provider for user-configurable thresholds.</param>
    /// <param name="databaseProvider">Optional database provider for health transition persistence.</param>
    /// <param name="writeCoordinator">Optional write coordinator for serialized DB writes.</param>
    public HealthService(
        IApiClient apiClient,
        IUrlBuilder urlBuilder,
        ILogger<HealthService> logger,
        ConfigurationProvider? configProvider = null,
        DatabaseProvider? databaseProvider = null,
        DatabaseWriteCoordinator? writeCoordinator = null)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _urlBuilder = urlBuilder ?? throw new ArgumentNullException(nameof(urlBuilder));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider;
        _databaseProvider = databaseProvider;
        _writeCoordinator = writeCoordinator;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HealthService"/> class
    /// with pre-built context options for unit testing.
    /// </summary>
    /// <param name="apiClient">TVHeadend API client.</param>
    /// <param name="urlBuilder">TVHeadend URL builder.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="configProvider">Optional configuration provider for user-configurable thresholds.</param>
    /// <param name="dbContextOptions">Optional pre-built DB context options for health transition persistence.</param>
    /// <param name="writeCoordinator">Optional write coordinator for serialized DB writes.</param>
    internal HealthService(
        IApiClient apiClient,
        IUrlBuilder urlBuilder,
        ILogger<HealthService> logger,
        ConfigurationProvider? configProvider,
        DbContextOptions<ViewingSessionContext>? dbContextOptions,
        DatabaseWriteCoordinator? writeCoordinator = null)
        : this(apiClient, urlBuilder, logger, configProvider, (DatabaseProvider?)null, writeCoordinator)
    {
        _lazyDbContextOptions = dbContextOptions;
    }

    private int CircuitOpenThreshold =>
        _configProvider?.Configuration is { } cfg && cfg.CircuitBreakerThreshold > 0
            ? cfg.CircuitBreakerThreshold
            : DefaultCircuitOpenThreshold;

    private TimeSpan CircuitOpenDuration =>
        _configProvider?.Configuration is { } cfg && cfg.CircuitBreakerDurationSeconds > 0
            ? TimeSpan.FromSeconds(cfg.CircuitBreakerDurationSeconds)
            : DefaultCircuitOpenDuration;

    private DbContextOptions<ViewingSessionContext>? GetDbContextOptions()
    {
        if (_databaseProvider == null && _lazyDbContextOptions == null)
        {
            return null;
        }

        if (_lazyDbContextOptions != null)
        {
            return _lazyDbContextOptions;
        }

        return _lazyDbContextOptions ??= _databaseProvider!.CreateContextOptions<ViewingSessionContext>();
    }

    /// <inheritdoc />
    public HealthSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return BuildSnapshot();
        }
    }

    /// <inheritdoc />
    public void RecordSuccess(int? responseTimeMs = null)
    {
        lock (_lock)
        {
            var previousStatus = _status;
            var wasDegraded = _circuitState != CircuitState.Closed
                || _consecutiveFailures >= CircuitOpenThreshold;

            _lastSuccessUtc = DateTimeOffset.UtcNow;
            _lastResponseTimeMs = responseTimeMs;
            _consecutiveFailures = 0;
            _circuitState = CircuitState.Closed;
            _status = HealthStatus.Healthy;

            if (previousStatus != HealthStatus.Healthy)
            {
                PersistTransition(previousStatus, _status, null, responseTimeMs, 0);
            }

            if (wasDegraded)
            {
                _logger.LogInformation(
                    "TVHeadend health recovered — status: {Status}, circuit: {Circuit}",
                    _status,
                    _circuitState);
            }
        }
    }

    /// <inheritdoc />
    public void RecordFailure(FailureReason reason)
    {
        lock (_lock)
        {
            var previousStatus = _status;
            _lastFailureUtc = DateTimeOffset.UtcNow;
            _lastFailureReason = reason;
            _consecutiveFailures++;

            _status = reason switch
            {
                FailureReason.AuthFailed => HealthStatus.AuthFailed,
                FailureReason.Timeout => HealthStatus.Timeout,
                FailureReason.CircuitOpen => HealthStatus.CircuitOpen,
                FailureReason.DnsFailure or FailureReason.ConnectionRefused or FailureReason.UpstreamUnavailable
                    => HealthStatus.Unreachable,
                _ => HealthStatus.Degraded,
            };

            if (_consecutiveFailures >= CircuitOpenThreshold)
            {
                if (_circuitState != CircuitState.Open)
                {
                    _circuitState = CircuitState.Open;
                    _circuitOpenUntilUtc = DateTimeOffset.UtcNow.Add(CircuitOpenDuration);
                    _status = HealthStatus.CircuitOpen;

                    _logger.LogWarning(
                        "TVHeadend circuit breaker opened — {ConsecutiveFailures} consecutive failures, last reason: {Reason}. Blocking requests until {OpenUntil:O}",
                        _consecutiveFailures,
                        reason,
                        _circuitOpenUntilUtc);
                }
            }

            if (previousStatus != _status)
            {
                PersistTransition(previousStatus, _status, reason.ToString(), null, _consecutiveFailures);
            }
        }
    }

    /// <inheritdoc />
    public bool ShouldBlockRequest()
    {
        lock (_lock)
        {
            if (_circuitState == CircuitState.Closed)
            {
                return false;
            }

            if (_circuitState == CircuitState.Open && DateTimeOffset.UtcNow >= _circuitOpenUntilUtc)
            {
                // Transition to half-open — allow a trial request
                _circuitState = CircuitState.HalfOpen;
                _status = HealthStatus.Recovering;
                _logger.LogInformation("TVHeadend circuit breaker entering half-open state — allowing trial request");
                return false;
            }

            // Open and not yet expired — block
            if (_circuitState == CircuitState.Open)
            {
                return true;
            }

            // HalfOpen — allow (already transitioned above or previously)
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<HealthSnapshot> CheckHealthAsync(CancellationToken cancellationToken)
    {
        var config = _apiClient.GetCurrentConfiguration();
        if (config == null)
        {
            RecordFailure(FailureReason.Unknown);
            return GetSnapshot();
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(OperationTimeouts.GetTimeout(OperationType.Health, config));

            using var httpClient = _apiClient.CreateApiHttpClient(config);
            var url = _urlBuilder.BuildApiUrl(config, "api/serverinfo");

            var sw = Stopwatch.StartNew();
            var response = await _apiClient.GetStringAsync(httpClient, url, cts.Token).ConfigureAwait(false);
            sw.Stop();

            if (!string.IsNullOrWhiteSpace(response))
            {
                RecordSuccess((int)sw.ElapsedMilliseconds);
            }
            else
            {
                RecordFailure(FailureReason.InvalidResponse);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RecordFailure(FailureReason.Timeout);
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled — don't record as TVH failure
        }
        catch (Exception ex)
        {
            var reason = FailureClassifier.Classify(ex);
            RecordFailure(reason);
            _logger.LogDebug(ex, "TVHeadend health check failed: {Reason}", reason);
        }

        return GetSnapshot();
    }

    /// <inheritdoc />
    public IReadOnlyList<HealthTransition> GetHealthHistory(int count = 100)
    {
        var dbOpts = GetDbContextOptions();
        if (dbOpts == null)
        {
            return Array.Empty<HealthTransition>();
        }

        try
        {
            using var db = new ViewingSessionContext(dbOpts);
            return db.HealthTransitions
                .AsNoTracking()
                .OrderByDescending(h => h.TimestampUtc)
                .Take(count)
                .ToList()
                .AsReadOnly();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read health transition history");
            return Array.Empty<HealthTransition>();
        }
    }

    private HealthSnapshot BuildSnapshot()
    {
        return new HealthSnapshot
        {
            Status = _status,
            CircuitState = _circuitState,
            IsDegradedModeActive = _circuitState != CircuitState.Closed
                || _status is HealthStatus.Degraded
                    or HealthStatus.Unreachable
                    or HealthStatus.AuthFailed
                    or HealthStatus.Timeout
                    or HealthStatus.CircuitOpen,
            LastSuccessUtc = _lastSuccessUtc,
            LastFailureUtc = _lastFailureUtc,
            LastFailureReason = _lastFailureReason,
            ConsecutiveFailures = _consecutiveFailures,
            LastResponseTimeMs = _lastResponseTimeMs,
            NextRetryUtc = _circuitState == CircuitState.Open ? _circuitOpenUntilUtc : null,
            SnapshotUtc = DateTimeOffset.UtcNow,
        };
    }

    private void PersistTransition(
        HealthStatus fromStatus,
        HealthStatus toStatus,
        string? failureReason,
        int? responseTimeMs,
        int consecutiveFailures)
    {
        var dbOpts = GetDbContextOptions();
        if (dbOpts == null || _writeCoordinator == null)
        {
            return;
        }

        // Fire-and-forget on the thread pool to avoid blocking health recording
        _ = Task.Run(() =>
        {
            try
            {
                using var writeLock = _writeCoordinator.AcquireWrite();
                using var db = new ViewingSessionContext(dbOpts);
                db.HealthTransitions.Add(new HealthTransition
                {
                    TimestampUtc = DateTime.UtcNow,
                    FromStatus = fromStatus.ToString(),
                    ToStatus = toStatus.ToString(),
                    FailureReason = failureReason,
                    ResponseTimeMs = responseTimeMs,
                    ConsecutiveFailures = consecutiveFailures,
                });
                db.SaveChanges();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to persist health transition");
            }
        });
    }
}
