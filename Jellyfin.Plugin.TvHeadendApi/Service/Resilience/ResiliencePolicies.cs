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
