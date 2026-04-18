using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Helper;

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
}

/// <summary>
/// A <see cref="DelegatingHandler"/> that retries requests on transient failures with exponential back-off
/// and breaks the circuit after consecutive failures.
/// </summary>
internal sealed class ResilienceHandler : DelegatingHandler
{
    private readonly object _lock = new();
    private int _consecutiveFailures;
    private DateTimeOffset _openUntil = DateTimeOffset.MinValue;

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;

        for (int attempt = 0; attempt <= ResiliencePolicies.RetryCount; attempt++)
        {
            ThrowIfCircuitOpen();

            if (attempt > 0)
            {
                var delay = TimeSpan.FromSeconds(
                    Math.Pow(ResiliencePolicies.RetryBaseDelaySeconds * 2, attempt));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                // Clone the request for retries — the original content stream may already be consumed.
                using var clone = await CloneRequestAsync(request, cancellationToken).ConfigureAwait(false);
                response = await base.SendAsync(clone, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is not a transient failure — propagate immediately without recording failure.
                throw;
            }
            catch (HttpRequestException) when (attempt < ResiliencePolicies.RetryCount)
            {
                RecordFailure();
                continue;
            }
            catch (HttpRequestException)
            {
                RecordFailure();
                throw;
            }

            if (ResiliencePolicies.ShouldRetry(response) && attempt < ResiliencePolicies.RetryCount)
            {
                RecordFailure();
                response.Dispose();
                continue;
            }

            if (ResiliencePolicies.ShouldRetry(response))
            {
                RecordFailure();
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

    private void ThrowIfCircuitOpen()
    {
        lock (_lock)
        {
            if (_consecutiveFailures >= ResiliencePolicies.CircuitBreakerThreshold
                && DateTimeOffset.UtcNow < _openUntil)
            {
                throw new InvalidOperationException(
                    $"Circuit breaker is open until {_openUntil:O}. Requests to TVHeadend are temporarily blocked.");
            }

            // If the break duration has elapsed, allow a trial request (half-open).
            if (_consecutiveFailures >= ResiliencePolicies.CircuitBreakerThreshold)
            {
                // Reset so that a single success closes the circuit, or a failure re-opens it.
                _consecutiveFailures = ResiliencePolicies.CircuitBreakerThreshold - 1;
            }
        }
    }

    private void RecordFailure()
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

    private void RecordSuccess()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
        }
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

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
        };

        if (request.Content is not null)
        {
            var contentBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            clone.Content = new ByteArrayContent(contentBytes);
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in request.Options)
        {
            ((System.Collections.Generic.IDictionary<string, object?>)clone.Options).Add(option);
        }

        return clone;
    }
}
