using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Polly;
using Polly.CircuitBreaker;
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
    /// Determines whether an HTTP outcome represents a transient error that should be retried.
    /// </summary>
    private static bool IsTransientError(Outcome<HttpResponseMessage> outcome)
    {
        if (outcome.Exception is HttpRequestException)
        {
            return true;
        }

        if (outcome.Result is null)
        {
            return false;
        }

        var status = (int)outcome.Result.StatusCode;
        return status >= 500 || outcome.Result.StatusCode == HttpStatusCode.RequestTimeout;
    }

    /// <summary>
    /// Gets retry strategy options for use with <c>AddResilienceHandler</c>.
    /// </summary>
    /// <returns>Retry strategy options for <see cref="HttpResponseMessage"/>.</returns>
    public static RetryStrategyOptions<HttpResponseMessage> GetRetryOptions()
    {
        return new RetryStrategyOptions<HttpResponseMessage>
        {
            MaxRetryAttempts = RetryCount,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromSeconds(RetryBaseDelaySeconds * 2),
            ShouldHandle = args =>
            {
                var outcome = args.Outcome;
                if (IsTransientError(outcome))
                {
                    return ValueTask.FromResult(true);
                }

                if (outcome.Result?.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return ValueTask.FromResult(true);
                }

                return ValueTask.FromResult(false);
            },
        };
    }

    /// <summary>
    /// Gets circuit breaker strategy options for use with <c>AddResilienceHandler</c>.
    /// </summary>
    /// <returns>Circuit breaker strategy options for <see cref="HttpResponseMessage"/>.</returns>
    public static CircuitBreakerStrategyOptions<HttpResponseMessage> GetCircuitBreakerOptions()
    {
        return new CircuitBreakerStrategyOptions<HttpResponseMessage>
        {
            FailureRatio = 1.0,
            MinimumThroughput = CircuitBreakerThreshold,
            SamplingDuration = TimeSpan.FromSeconds(CircuitBreakerDurationSeconds),
            BreakDuration = TimeSpan.FromSeconds(CircuitBreakerDurationSeconds),
            ShouldHandle = args => ValueTask.FromResult(IsTransientError(args.Outcome)),
        };
    }

    /// <summary>
    /// Creates a retry pipeline for direct use in tests.
    /// </summary>
    /// <returns>A resilience pipeline for <see cref="HttpResponseMessage"/>.</returns>
    public static ResiliencePipeline<HttpResponseMessage> GetRetryPolicy()
    {
        return new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(GetRetryOptions())
            .Build();
    }

    /// <summary>
    /// Creates a circuit breaker pipeline for direct use in tests.
    /// </summary>
    /// <returns>A resilience pipeline for <see cref="HttpResponseMessage"/>.</returns>
    public static ResiliencePipeline<HttpResponseMessage> GetCircuitBreakerPolicy()
    {
        return new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddCircuitBreaker(GetCircuitBreakerOptions())
            .Build();
    }
}
