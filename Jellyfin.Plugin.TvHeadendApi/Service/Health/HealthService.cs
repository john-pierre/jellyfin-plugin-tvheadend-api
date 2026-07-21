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

    // Per-reason failure counters (all reported failures) — guarded by _lock.
    private readonly Dictionary<FailureReason, long> _failureCountsByReason = new();

    // Marks the async flow that owns the current half-open trial request. The same logical
    // request consults ShouldBlockRequest() twice — once from the domain service's pre-check
    // and once from the ResilienceHandler fast-fail — so the owning flow must pass both.
    private readonly AsyncLocal<bool> _ownsHalfOpenTrial = new();

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
    private bool _halfOpenTrialInFlight;
    private DateTimeOffset _halfOpenTrialStartedUtc = DateTimeOffset.MinValue;

    // Breaker observability counters — guarded by _lock, accumulated since plugin start.
    private long _timesOpened;
    private DateTimeOffset? _lastOpenedAtUtc;
    private FailureReason _lastOpenReason = FailureReason.None;
    private DateTimeOffset? _currentOpenEpisodeStartedUtc;
    private long _lastOpenDurationMs;
    private long _totalOpenDurationMs;
    private long _rejectedWhileOpen;
    private long _halfOpenTrials;
    private long _halfOpenTrialSuccesses;
    private long _halfOpenTrialFailures;
    private long _circuitFailures;
    private long _reportedFailures;

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

            if (_circuitState == CircuitState.HalfOpen)
            {
                _halfOpenTrialSuccesses++;
            }

            CloseOpenEpisodeIfAny();

            _lastSuccessUtc = DateTimeOffset.UtcNow;
            _lastResponseTimeMs = responseTimeMs;
            _consecutiveFailures = 0;
            _circuitState = CircuitState.Closed;
            _halfOpenTrialInFlight = false;
            _ownsHalfOpenTrial.Value = false;
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
    public void RecordFailure(FailureReason reason, bool affectsCircuit = true)
    {
        lock (_lock)
        {
            var previousStatus = _status;
            _lastFailureUtc = DateTimeOffset.UtcNow;
            _lastFailureReason = reason;
            _reportedFailures++;
            _failureCountsByReason[reason] = _failureCountsByReason.TryGetValue(reason, out var count) ? count + 1 : 1;

            if (!affectsCircuit)
            {
                // Non-breaker failures (server-responded per-channel errors, mid-retry attempts)
                // stay VISIBLE — LastFailureReason and the metrics counters above record them —
                // but they must NOT downgrade the GLOBAL health status. A single dead channel or
                // exhausted tuner used to flip the dashboard banner to "Degraded" while Diagnose
                // still read "Connected, 100" (backlog #4). The global status reflects whether we
                // can reach TVHeadend AT ALL, which a server-answered error does not threaten.
                return;
            }

            // A failure resolves any half-open trial; the threshold check below re-opens the
            // circuit (consecutive failures never reset between Open and HalfOpen).
            if (_circuitState == CircuitState.HalfOpen && _halfOpenTrialInFlight)
            {
                _halfOpenTrialFailures++;
            }

            _halfOpenTrialInFlight = false;
            _ownsHalfOpenTrial.Value = false;

            _status = reason switch
            {
                FailureReason.AuthFailed => HealthStatus.AuthFailed,
                FailureReason.Timeout => HealthStatus.Timeout,
                FailureReason.CircuitOpen => HealthStatus.CircuitOpen,
                FailureReason.DnsFailure or FailureReason.ConnectionRefused or FailureReason.UpstreamUnavailable
                    => HealthStatus.Unreachable,
                _ => HealthStatus.Degraded,
            };

            _consecutiveFailures++;
            _circuitFailures++;

            if (_consecutiveFailures >= CircuitOpenThreshold && _circuitState != CircuitState.Open)
            {
                var reopenedFromHalfOpen = _circuitState == CircuitState.HalfOpen;
                _circuitState = CircuitState.Open;
                _circuitOpenUntilUtc = DateTimeOffset.UtcNow.Add(CircuitOpenDuration);
                _status = HealthStatus.CircuitOpen;

                // Metrics: a Closed->Open transition starts a NEW open episode; a failed
                // half-open trial merely extends the current one.
                if (!reopenedFromHalfOpen || _currentOpenEpisodeStartedUtc is null)
                {
                    _timesOpened++;
                    _lastOpenedAtUtc = DateTimeOffset.UtcNow;
                    _lastOpenReason = reason;
                    _currentOpenEpisodeStartedUtc ??= DateTimeOffset.UtcNow;
                }

                _logger.LogWarning(
                    "TVHeadend circuit breaker opened — {ConsecutiveFailures} consecutive failures, last reason: {Reason}. Blocking requests until {OpenUntil:O}",
                    _consecutiveFailures,
                    reason,
                    _circuitOpenUntilUtc);
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

            if (_circuitState == CircuitState.Open)
            {
                // Open and not yet expired — block
                if (DateTimeOffset.UtcNow < _circuitOpenUntilUtc)
                {
                    _rejectedWhileOpen++;
                    return true;
                }

                // Open window elapsed — transition to half-open and admit this caller
                // as the single trial request.
                _circuitState = CircuitState.HalfOpen;
                _status = HealthStatus.Recovering;
                BeginHalfOpenTrial();
                _logger.LogInformation("TVHeadend circuit breaker entering half-open state — allowing a single trial request");
                return false;
            }

            // Half-open: exactly one trial request may be in flight; concurrent callers are
            // blocked until the trial resolves via RecordSuccess/RecordFailure. The async flow
            // that was admitted as the trial passes repeated checks (its own request re-checks
            // this gate in the ResilienceHandler). If the admitted caller never issues a request
            // (e.g. it bailed on a missing configuration), the trial is considered abandoned
            // after the open duration and a new one is admitted so the breaker cannot stay
            // half-open forever.
            if (_ownsHalfOpenTrial.Value)
            {
                return false;
            }

            if (_halfOpenTrialInFlight
                && DateTimeOffset.UtcNow - _halfOpenTrialStartedUtc < CircuitOpenDuration)
            {
                _rejectedWhileOpen++;
                return true;
            }

            BeginHalfOpenTrial();
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

    /// <summary>
    /// Sets the circuit open-until time. Exposed for testing to simulate time advancement.
    /// </summary>
    /// <param name="openUntil">The new open-until timestamp.</param>
    internal void SetCircuitOpenUntil(DateTimeOffset openUntil)
    {
        lock (_lock)
        {
            _circuitOpenUntilUtc = openUntil;
        }
    }

    /// <summary>
    /// Sets the half-open trial start time. Exposed for testing to simulate an abandoned trial.
    /// </summary>
    /// <param name="startedUtc">The new trial start timestamp.</param>
    internal void SetHalfOpenTrialStarted(DateTimeOffset startedUtc)
    {
        lock (_lock)
        {
            _halfOpenTrialStartedUtc = startedUtc;
        }
    }

    /// <summary>
    /// Marks a half-open trial request as in flight and the current async flow as its owner.
    /// Caller must hold <see cref="_lock"/>.
    /// </summary>
    private void BeginHalfOpenTrial()
    {
        _halfOpenTrialInFlight = true;
        _halfOpenTrialStartedUtc = DateTimeOffset.UtcNow;
        _ownsHalfOpenTrial.Value = true;
        _halfOpenTrials++;
    }

    /// <summary>
    /// Finalizes the current open episode's duration metrics on recovery.
    /// Caller must hold <see cref="_lock"/>.
    /// </summary>
    private void CloseOpenEpisodeIfAny()
    {
        if (_currentOpenEpisodeStartedUtc is { } startedUtc)
        {
            var episodeMs = (long)(DateTimeOffset.UtcNow - startedUtc).TotalMilliseconds;
            _lastOpenDurationMs = episodeMs;
            _totalOpenDurationMs += episodeMs;
            _currentOpenEpisodeStartedUtc = null;
        }
    }

    private HealthSnapshot BuildSnapshot()
    {
        // Cumulative open time includes the still-running episode while the circuit is open.
        var totalOpenMs = _totalOpenDurationMs;
        if (_currentOpenEpisodeStartedUtc is { } startedUtc)
        {
            totalOpenMs += (long)(DateTimeOffset.UtcNow - startedUtc).TotalMilliseconds;
        }

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
            Breaker = new CircuitBreakerMetrics
            {
                Threshold = CircuitOpenThreshold,
                OpenDurationSeconds = (int)CircuitOpenDuration.TotalSeconds,
                TimesOpened = _timesOpened,
                LastOpenedAtUtc = _lastOpenedAtUtc,
                LastOpenReason = _lastOpenReason,
                LastOpenDurationMs = _lastOpenDurationMs,
                TotalOpenDurationMs = totalOpenMs,
                RejectedWhileOpen = _rejectedWhileOpen,
                HalfOpenTrials = _halfOpenTrials,
                HalfOpenTrialSuccesses = _halfOpenTrialSuccesses,
                HalfOpenTrialFailures = _halfOpenTrialFailures,
                CircuitFailures = _circuitFailures,
                ReportedFailures = _reportedFailures,
                FailureCountsByReason = _failureCountsByReason.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            },
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
