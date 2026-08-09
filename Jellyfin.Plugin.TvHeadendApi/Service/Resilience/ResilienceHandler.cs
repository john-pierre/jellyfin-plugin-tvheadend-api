using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Resilience;

/// <summary>
/// A <see cref="DelegatingHandler"/> that retries idempotent requests on transient failures with
/// exponential back-off and breaks the circuit after consecutive failures. The half-open state
/// admits exactly one trial request. Failures are reported to an optional
/// <see cref="IHealthService"/> with their classified <see cref="FailureReason"/>.
/// </summary>
internal sealed class ResilienceHandler : DelegatingHandler
{
    private readonly object _lock = new();
    private int _consecutiveFailures;
    private DateTimeOffset _openUntil = DateTimeOffset.MinValue;
    private bool _trialInFlight;

    /// <summary>
    /// Gets or sets the optional health service callback for centralized health reporting.
    /// </summary>
    internal IHealthService? HealthService { get; set; }

    /// <summary>
    /// Gets or sets the retry delay strategy (1-based attempt → delay).
    /// Overridable in tests to avoid real back-off waits.
    /// </summary>
    internal Func<int, TimeSpan> RetryDelay { get; set; } = ResiliencePolicies.GetRetryDelay;

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Fast-fail if the central health service says the circuit is open
        if (HealthService?.ShouldBlockRequest() == true)
        {
            throw new InvalidOperationException(
                "TVHeadend circuit breaker is open (central). Requests are temporarily blocked.");
        }

        var mayRetry = ResiliencePolicies.IsIdempotent(request);
        var ownsTrial = false;

        try
        {
            HttpResponseMessage? response = null;

            for (int attempt = 0; attempt <= ResiliencePolicies.RetryCount; attempt++)
            {
                ownsTrial |= EnterGateOrThrow(ownsTrial);

                if (attempt > 0)
                {
                    await Task.Delay(RetryDelay(attempt), cancellationToken).ConfigureAwait(false);
                }

                // A failure only counts toward the circuit breaker when it is FINAL for this
                // logical request (retries exhausted or the request is not retryable). Counting
                // every attempt let a single retried request contribute up to 4 of the 5
                // threshold failures — one transient blip plus a second caller opened the
                // breaker before the retry ladder could prove recovery ("trips too fast").
                // Mid-retry failures stay visible as non-breaker health reports and metrics.
                var isFinalAttempt = !(mayRetry && attempt < ResiliencePolicies.RetryCount);

                try
                {
                    // Clone the request for each attempt — the original content stream may already be consumed.
                    using var clone = await HttpRequestCloner.CloneAsync(request, cancellationToken).ConfigureAwait(false);
                    response = await base.SendAsync(clone, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Genuine cancellation requested by the caller (or a linked timeout above this
                    // handler) — propagate immediately without recording a backend failure.
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // Internal cancellation the caller did not request (e.g. connect timeout):
                    // a hung backend must be visible to the circuit breaker.
                    RecordFailure(FailureReason.Timeout, isFinalAttempt);
                    if (!isFinalAttempt)
                    {
                        continue;
                    }

                    throw;
                }
                catch (HttpRequestException ex)
                {
                    var reason = FailureClassifier.Classify(ex);

                    // Deterministic connect-level failures (refused port, unknown host) will
                    // not heal within the back-off window — retrying them only stalls every
                    // degraded-mode caller (~7s of back-off per logical request, which made
                    // the dashboard time out while the backend was down). Fail immediately;
                    // the breaker still needs THRESHOLD such requests to open.
                    var isDeterministic = reason is FailureReason.ConnectionRefused or FailureReason.DnsFailure;
                    var isFinal = isDeterministic || isFinalAttempt;
                    RecordFailure(reason, isFinal);
                    if (!isFinal)
                    {
                        continue;
                    }

                    throw;
                }

                if (ResiliencePolicies.ShouldRetry(response))
                {
                    RecordFailure(FailureClassifier.ClassifyStatusCode(response.StatusCode), isFinalAttempt);
                    if (!isFinalAttempt)
                    {
                        response.Dispose();
                        continue;
                    }
                }
                else
                {
                    RecordSuccess();
                }

                return response;
            }

            // Should not be reached, but satisfy the compiler.
            return response!;
        }
        finally
        {
            if (ownsTrial)
            {
                ClearTrial();
            }
        }
    }

    /// <summary>
    /// Checks the circuit breaker gate. Throws when the circuit is open; when the open window
    /// has elapsed (half-open), admits exactly one trial request and rejects the rest.
    /// </summary>
    /// <param name="ownsTrial">
    /// Whether the calling flow already owns the half-open trial. The trial request re-enters
    /// this gate on every RETRY attempt — the owner must pass so its retry ladder can finish
    /// instead of aborting the trial against its own in-flight flag.
    /// </param>
    /// <returns><c>true</c> when this call was admitted as the half-open trial request.</returns>
    private bool EnterGateOrThrow(bool ownsTrial)
    {
        lock (_lock)
        {
            if (_consecutiveFailures < ResiliencePolicies.CircuitBreakerThreshold)
            {
                return false;
            }

            if (DateTimeOffset.UtcNow < _openUntil)
            {
                throw new InvalidOperationException(
                    $"Circuit breaker is open until {_openUntil:O}. Requests to TVHeadend are temporarily blocked.");
            }

            if (ownsTrial)
            {
                return false;
            }

            if (_trialInFlight)
            {
                throw new InvalidOperationException(
                    "Circuit breaker is half-open and a trial request is already in flight. Requests to TVHeadend are temporarily blocked.");
            }

            _trialInFlight = true;
            return true;
        }
    }

    private void ClearTrial()
    {
        lock (_lock)
        {
            _trialInFlight = false;
        }
    }

    /// <summary>
    /// Records a failed attempt. Only FINAL failures (retries exhausted / not retryable)
    /// advance the local and central circuit breaker counters; mid-retry failures are
    /// reported for health visibility and metrics only.
    /// </summary>
    /// <param name="reason">The classified failure reason.</param>
    /// <param name="isFinalAttempt">Whether this failure ends the logical request.</param>
    private void RecordFailure(FailureReason reason, bool isFinalAttempt)
    {
        if (isFinalAttempt)
        {
            lock (_lock)
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= ResiliencePolicies.CircuitBreakerThreshold)
                {
                    _openUntil = DateTimeOffset.UtcNow.AddSeconds(ResiliencePolicies.CircuitBreakerDurationSeconds);
                }
            }
        }

        HealthService?.RecordFailure(reason, affectsCircuit: isFinalAttempt);
    }

    private void RecordSuccess()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
        }

        HealthService?.RecordSuccess();
    }

    /// <summary>
    /// Gets the current consecutive failure count. Exposed for testing.
    /// </summary>
    /// <returns>The number of consecutive failures.</returns>
    internal int GetConsecutiveFailures()
    {
        lock (_lock)
        {
            return _consecutiveFailures;
        }
    }

    /// <summary>
    /// Sets the open-until time. Exposed for testing to simulate time advancement.
    /// </summary>
    /// <param name="openUntil">The new open-until timestamp.</param>
    internal void SetOpenUntil(DateTimeOffset openUntil)
    {
        lock (_lock)
        {
            _openUntil = openUntil;
        }
    }
}
