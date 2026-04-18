using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Polly;
using Polly.CircuitBreaker;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Helper;

/// <summary>
/// Tests for <see cref="ResiliencePolicies"/> retry and circuit breaker behavior.
/// </summary>
public class ResiliencePoliciesTests
{
    [Fact]
    public async Task RetryPolicy_RetriesOnTransientError_ThenSucceeds()
    {
        // Arrange
        var pipeline = ResiliencePolicies.GetRetryPolicy();
        var callCount = 0;

        // Act — fail twice with 500, succeed on third attempt
        var result = await pipeline.ExecuteAsync(async _ =>
        {
            callCount++;
            if (callCount <= 2)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        // Assert
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task RetryPolicy_RetriesOnRequestTimeout()
    {
        // Arrange
        var pipeline = ResiliencePolicies.GetRetryPolicy();
        var callCount = 0;

        // Act — fail once with 408 (Request Timeout), then succeed
        var result = await pipeline.ExecuteAsync(async _ =>
        {
            callCount++;
            if (callCount <= 1)
            {
                return new HttpResponseMessage(HttpStatusCode.RequestTimeout);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        // Assert
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task RetryPolicy_RetriesOn429TooManyRequests()
    {
        // Arrange
        var pipeline = ResiliencePolicies.GetRetryPolicy();
        var callCount = 0;

        // Act
        var result = await pipeline.ExecuteAsync(async _ =>
        {
            callCount++;
            if (callCount <= 1)
            {
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        // Assert
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task RetryPolicy_ReturnsLastFailure_AfterMaxRetries()
    {
        // Arrange
        var pipeline = ResiliencePolicies.GetRetryPolicy();
        var callCount = 0;

        // Act — always fail
        var result = await pipeline.ExecuteAsync(async _ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        // Assert — 1 initial + 3 retries = 4 total calls
        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
        Assert.Equal(1 + ResiliencePolicies.RetryCount, callCount);
    }

    [Fact]
    public async Task RetryPolicy_DoesNotRetryOnNonTransientError()
    {
        // Arrange
        var pipeline = ResiliencePolicies.GetRetryPolicy();
        var callCount = 0;

        // Act — 404 is not transient
        var result = await pipeline.ExecuteAsync(async _ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task RetryPolicy_RetriesOnHttpRequestException()
    {
        // Arrange
        var pipeline = ResiliencePolicies.GetRetryPolicy();
        var callCount = 0;

        // Act — throw HttpRequestException twice, then succeed
        var result = await pipeline.ExecuteAsync(async _ =>
        {
            callCount++;
            if (callCount <= 2)
            {
                throw new HttpRequestException("Connection refused");
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        // Assert
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task CircuitBreakerPolicy_OpensAfterConsecutiveFailures()
    {
        // Arrange
        var pipeline = ResiliencePolicies.GetCircuitBreakerPolicy();

        // Act — trigger enough failures to open the circuit
        for (int i = 0; i < ResiliencePolicies.CircuitBreakerThreshold; i++)
        {
            await pipeline.ExecuteAsync(async _ =>
                new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }

        // Assert — next call should throw BrokenCircuitException
        await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
            await pipeline.ExecuteAsync(async _ =>
                new HttpResponseMessage(HttpStatusCode.OK)));
    }

    [Fact]
    public async Task CircuitBreakerPolicy_AllowsRequests_WhenNotTripped()
    {
        // Arrange
        var pipeline = ResiliencePolicies.GetCircuitBreakerPolicy();

        // Act — successful call should pass through
        var result = await pipeline.ExecuteAsync(async _ =>
            new HttpResponseMessage(HttpStatusCode.OK));

        // Assert
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    [Fact]
    public void RetryPolicy_Constants_HaveExpectedValues()
    {
        Assert.Equal(3, ResiliencePolicies.RetryCount);
        Assert.Equal(0.5, ResiliencePolicies.RetryBaseDelaySeconds);
    }

    [Fact]
    public void CircuitBreakerPolicy_Constants_HaveExpectedValues()
    {
        Assert.Equal(5, ResiliencePolicies.CircuitBreakerThreshold);
        Assert.Equal(30, ResiliencePolicies.CircuitBreakerDurationSeconds);
    }
}

