using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Resilience;

/// <summary>
/// Defines resilience policies (retry + circuit breaker) for TVHeadend API HTTP calls.
/// Implemented without external dependencies to avoid assembly-loading issues in Jellyfin's plugin host.
/// </summary>
internal static class ResiliencePolicies
{
    /// <summary>
    /// Maximum number of retry attempts for transient failures.
    /// </summary>
    internal const int RetryCount = 3;

    /// <summary>
    /// Base delay in seconds for exponential back-off between retries.
    /// </summary>
    internal const double RetryBaseDelaySeconds = 0.5;

    /// <summary>
    /// Number of consecutive failures before the circuit breaker opens.
    /// </summary>
    internal const int CircuitBreakerThreshold = 5;

    /// <summary>
    /// Duration in seconds the circuit stays open before allowing a trial request.
    /// </summary>
    internal const int CircuitBreakerDurationSeconds = 30;

    /// <summary>
    /// Returns <c>true</c> when the status code represents a transient HTTP error (5xx or 408).
    /// </summary>
    /// <param name="statusCode">The HTTP status code to evaluate.</param>
    /// <returns><c>true</c> if the status code is transient; otherwise <c>false</c>.</returns>
    internal static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code >= 500 || statusCode == HttpStatusCode.RequestTimeout;
    }

    /// <summary>
    /// Returns <c>true</c> when the response should trigger a retry (transient error or 429).
    /// </summary>
    /// <param name="response">The HTTP response to evaluate, or <c>null</c> if no response was received.</param>
    /// <returns><c>true</c> if the request should be retried; otherwise <c>false</c>.</returns>
    internal static bool ShouldRetry(HttpResponseMessage? response)
    {
        if (response is null)
        {
            return true;
        }

        return IsTransientStatusCode(response.StatusCode)
               || response.StatusCode == HttpStatusCode.TooManyRequests;
    }

    /// <summary>
    /// Computes the exponential back-off delay for a retry attempt: 1s, 2s, 4s for attempts 1..3.
    /// </summary>
    /// <param name="attempt">The 1-based retry attempt number.</param>
    /// <returns>The delay to wait before the attempt.</returns>
    internal static TimeSpan GetRetryDelay(int attempt)
    {
        return TimeSpan.FromSeconds(RetryBaseDelaySeconds * Math.Pow(2, attempt));
    }

    /// <summary>
    /// Returns <c>true</c> when the request may safely be sent again (idempotent method).
    /// All TVHeadend mutations in this plugin (DVR timer/autorec, passwd entry, profile and
    /// codec creation) are form POSTs — resending one that the backend already processed
    /// would duplicate the mutation, so only GET/HEAD requests are retried.
    /// </summary>
    /// <param name="request">The request to evaluate.</param>
    /// <returns><c>true</c> if the request is idempotent; otherwise <c>false</c>.</returns>
    internal static bool IsIdempotent(HttpRequestMessage request)
    {
        return request.Method == HttpMethod.Get || request.Method == HttpMethod.Head;
    }
}

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
                ownsTrial |= EnterGateOrThrow();

                if (attempt > 0)
                {
                    await Task.Delay(RetryDelay(attempt), cancellationToken).ConfigureAwait(false);
                }

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
                    RecordFailure(FailureReason.Timeout);
                    if (mayRetry && attempt < ResiliencePolicies.RetryCount)
                    {
                        continue;
                    }

                    throw;
                }
                catch (HttpRequestException ex)
                {
                    RecordFailure(FailureClassifier.Classify(ex));
                    if (mayRetry && attempt < ResiliencePolicies.RetryCount)
                    {
                        continue;
                    }

                    throw;
                }

                if (ResiliencePolicies.ShouldRetry(response))
                {
                    RecordFailure(FailureClassifier.ClassifyStatusCode(response.StatusCode));
                    if (mayRetry && attempt < ResiliencePolicies.RetryCount)
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
    /// <returns><c>true</c> when this call was admitted as the half-open trial request.</returns>
    private bool EnterGateOrThrow()
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

    private void RecordFailure(FailureReason reason)
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= ResiliencePolicies.CircuitBreakerThreshold)
            {
                _openUntil = DateTimeOffset.UtcNow.AddSeconds(ResiliencePolicies.CircuitBreakerDurationSeconds);
            }
        }

        HealthService?.RecordFailure(reason);
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
