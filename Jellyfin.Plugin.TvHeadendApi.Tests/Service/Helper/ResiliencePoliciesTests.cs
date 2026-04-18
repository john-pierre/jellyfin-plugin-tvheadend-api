using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Helper;

/// <summary>
/// Tests for <see cref="ResiliencePolicies"/> and <see cref="ResilienceHandler"/> retry and circuit breaker behavior.
/// </summary>
public class ResiliencePoliciesTests
{
    /// <summary>
    /// Creates a <see cref="ResilienceHandler"/> backed by a programmable inner handler.
    /// </summary>
    private static HttpMessageInvoker CreateInvoker(FakeHandler inner)
    {
        var handler = new ResilienceHandler { InnerHandler = inner };
        return new HttpMessageInvoker(handler);
    }

    [Fact]
    public async Task RetryPolicy_RetriesOnTransientError_ThenSucceeds()
    {
        var callCount = 0;
        using var invoker = CreateInvoker(new FakeHandler(() =>
        {
            callCount++;
            return callCount <= 2
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var result = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task RetryPolicy_RetriesOnRequestTimeout()
    {
        var callCount = 0;
        using var invoker = CreateInvoker(new FakeHandler(() =>
        {
            callCount++;
            return callCount <= 1
                ? new HttpResponseMessage(HttpStatusCode.RequestTimeout)
                : new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var result = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task RetryPolicy_RetriesOn429TooManyRequests()
    {
        var callCount = 0;
        using var invoker = CreateInvoker(new FakeHandler(() =>
        {
            callCount++;
            return callCount <= 1
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                : new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var result = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task RetryPolicy_ReturnsLastFailure_AfterMaxRetries()
    {
        var callCount = 0;
        using var invoker = CreateInvoker(new FakeHandler(() =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }));

        var result = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
        Assert.Equal(1 + ResiliencePolicies.RetryCount, callCount);
    }

    [Fact]
    public async Task RetryPolicy_DoesNotRetryOnNonTransientError()
    {
        var callCount = 0;
        using var invoker = CreateInvoker(new FakeHandler(() =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var result = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task RetryPolicy_RetriesOnHttpRequestException()
    {
        var callCount = 0;
        using var invoker = CreateInvoker(new FakeHandler(() =>
        {
            callCount++;
            if (callCount <= 2)
            {
                throw new HttpRequestException("Connection refused");
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var result = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task CircuitBreakerPolicy_OpensAfterConsecutiveFailures()
    {
        // Use a single handler instance so the circuit breaker state accumulates.
        var inner = new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var handler = new ResilienceHandler { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        // Keep sending requests until the circuit opens. Each request records multiple failures
        // (1 initial + RetryCount retries). We catch the InvalidOperationException that signals
        // the circuit is open.
        bool circuitOpened = false;
        for (int i = 0; i < 10 && !circuitOpened; i++)
        {
            try
            {
                await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), CancellationToken.None);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Circuit breaker"))
            {
                circuitOpened = true;
            }
        }

        Assert.True(circuitOpened, "Circuit breaker should have opened after consecutive failures.");
    }

    [Fact]
    public async Task CircuitBreakerPolicy_AllowsRequests_WhenNotTripped()
    {
        using var invoker = CreateInvoker(new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.OK)));

        var result = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), CancellationToken.None);

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

    [Fact]
    public async Task CircuitBreaker_HalfOpen_SuccessClosesCircuit()
    {
        var callCount = 0;
        var shouldFail = true;
        var inner = new FakeHandler(() =>
        {
            callCount++;
            return shouldFail
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK);
        });

        var handler = new ResilienceHandler { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        // Trip the circuit: send enough failures to open it.
        for (int i = 0; i < 3 && handler.GetConsecutiveFailures() < ResiliencePolicies.CircuitBreakerThreshold; i++)
        {
            try
            {
                await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                break;
            }
        }

        // Simulate the break duration elapsing.
        handler.SetOpenUntil(DateTimeOffset.UtcNow.AddSeconds(-1));

        // Switch to success responses — the half-open trial should succeed and close the circuit.
        shouldFail = false;
        var result = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);

        // Circuit should now be closed (consecutive failures reset to 0).
        Assert.Equal(0, handler.GetConsecutiveFailures());
    }

    [Fact]
    public async Task CircuitBreaker_HalfOpen_FailureReopensCircuit()
    {
        var inner = new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var handler = new ResilienceHandler { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        // Trip the circuit.
        for (int i = 0; i < 3; i++)
        {
            try
            {
                await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                break;
            }
        }

        // Simulate break duration elapsed — half-open state.
        handler.SetOpenUntil(DateTimeOffset.UtcNow.AddSeconds(-1));

        // Send a trial request — it will fail and re-open the circuit.
        // The request may throw InvalidOperationException during retry if circuit re-opens mid-attempt,
        // or return a failure response if all retries complete.
        try
        {
            await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // Expected: circuit re-opened during retry
        }

        // The circuit should be open again — next request throws immediately.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), CancellationToken.None));
    }

    [Fact]
    public async Task ResilienceHandler_CancellationToken_PropagatesImmediately()
    {
        var callCount = 0;
        var inner = new FakeHandler(() =>
        {
            callCount++;
            throw new TaskCanceledException("Operation cancelled", null, new CancellationToken(true));
        });
        using var invoker = CreateInvoker(inner);

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), CancellationToken.None));

        // Should not retry on cancellation — only 1 call.
        Assert.Equal(1, callCount);
    }

    /// <summary>
    /// A fake <see cref="HttpMessageHandler"/> that delegates to a factory function.
    /// </summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _factory;

        public FakeHandler(Func<HttpResponseMessage> factory) => _factory = factory;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_factory());
    }
}
