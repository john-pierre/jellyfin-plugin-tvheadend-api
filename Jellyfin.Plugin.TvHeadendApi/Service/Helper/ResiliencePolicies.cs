using System;
using System.Net;
using System.Net.Http;
using Polly;
using Polly.CircuitBreaker;
using Polly.Extensions.Http;
using Polly.Retry;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Helper;

/// <summary>
/// Defines resilience policies (retry + circuit breaker) for TVHeadend API HTTP calls.
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
    /// Creates the retry policy: retries up to <see cref="RetryCount"/> times on transient HTTP errors
    /// (5xx, 408, <see cref="HttpRequestException"/>) with exponential back-off.
    /// </summary>
    /// <returns>An async retry policy for <see cref="HttpResponseMessage"/>.</returns>
    public static AsyncRetryPolicy<HttpResponseMessage> GetRetryPolicy()
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(msg => msg.StatusCode == HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(
                RetryCount,
                retryAttempt => TimeSpan.FromSeconds(Math.Pow(RetryBaseDelaySeconds * 2, retryAttempt)));
    }

    /// <summary>
    /// Creates the circuit breaker policy: breaks after <see cref="CircuitBreakerThreshold"/> consecutive
    /// transient failures and stays open for <see cref="CircuitBreakerDurationSeconds"/> seconds.
    /// </summary>
    /// <returns>An async circuit breaker policy for <see cref="HttpResponseMessage"/>.</returns>
    public static AsyncCircuitBreakerPolicy<HttpResponseMessage> GetCircuitBreakerPolicy()
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .CircuitBreakerAsync(
                CircuitBreakerThreshold,
                TimeSpan.FromSeconds(CircuitBreakerDurationSeconds));
    }
}
