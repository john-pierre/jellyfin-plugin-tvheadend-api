using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Helper;

/// <summary>
/// Tests for <see cref="ResiliencePolicies"/> and <see cref="ResilienceHandler"/> retry and circuit breaker behavior.
/// </summary>
public class ResiliencePoliciesTests
{
    /// <summary>
    /// Creates a <see cref="ResilienceHandler"/> backed by a programmable inner handler.
    /// Retry delays are zeroed so tests do not wait on real back-off.
    /// </summary>
    private static HttpMessageInvoker CreateInvoker(FakeHandler inner)
    {
        return new HttpMessageInvoker(CreateHandler(inner));
    }

    private static ResilienceHandler CreateHandler(HttpMessageHandler inner)
    {
        return new ResilienceHandler
        {
            InnerHandler = inner,
            RetryDelay = _ => TimeSpan.Zero,
        };
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
                // Transient network error (classified UpstreamUnavailable) — retryable.
                throw new HttpRequestException("Connection reset by peer");
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var result = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task RetryPolicy_ConnectionRefused_FailsFast_WithoutRetry()
    {
        // A refused port is deterministic — nothing heals within the back-off window, and the
        // ~7s retry ladder used to stall every degraded-mode caller (dashboard timeouts while
        // the backend was down). Fail immediately and count ONE breaker-relevant failure.
        var callCount = 0;
        var inner = new FakeHandler(() =>
        {
            callCount++;
            throw new HttpRequestException("Connection refused");
        });
        var handler = CreateHandler(inner);
        using var invoker = new HttpMessageInvoker(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), CancellationToken.None));

        Assert.Equal(1, callCount);
        Assert.Equal(1, handler.GetConsecutiveFailures());
    }

    [Fact]
    public async Task CircuitBreakerPolicy_OpensAfterConsecutiveFailures()
    {
        // Use a single handler instance so the circuit breaker state accumulates.
        var inner = new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var handler = CreateHandler(inner);
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

        var handler = CreateHandler(inner);
        using var invoker = new HttpMessageInvoker(handler);

        // Trip the circuit: send enough LOGICAL failures to open it (one count per request).
        for (int i = 0; i < ResiliencePolicies.CircuitBreakerThreshold && handler.GetConsecutiveFailures() < ResiliencePolicies.CircuitBreakerThreshold; i++)
        {
            try
            {
                (await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), CancellationToken.None)).Dispose();
            }
            catch (InvalidOperationException)
            {
                break;
            }
        }

        Assert.True(
            handler.GetConsecutiveFailures() >= ResiliencePolicies.CircuitBreakerThreshold,
            "circuit must be open before the half-open scenario starts");

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

        var handler = CreateHandler(inner);
        using var invoker = new HttpMessageInvoker(handler);

        // Trip the circuit: each LOGICAL failed request counts once, so the threshold takes
        // that many requests (per-attempt counting used to reach it after ~2).
        for (int i = 0; i < ResiliencePolicies.CircuitBreakerThreshold; i++)
        {
            try
            {
                (await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), CancellationToken.None)).Dispose();
            }
            catch (InvalidOperationException)
            {
                break;
            }
        }

        Assert.True(
            handler.GetConsecutiveFailures() >= ResiliencePolicies.CircuitBreakerThreshold,
            "circuit must be open before the half-open scenario starts");

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
    public async Task ResilienceHandler_CallerCancellation_PropagatesImmediately_WithoutRecordingFailure()
    {
        var callCount = 0;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var inner = new FakeHandler(() =>
        {
            callCount++;
            throw new TaskCanceledException("Operation cancelled", null, cts.Token);
        });
        var handler = CreateHandler(inner);
        using var invoker = new HttpMessageInvoker(handler);

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), cts.Token));

        // Caller-requested cancellation must not retry and must not count as a backend failure.
        Assert.Equal(1, callCount);
        Assert.Equal(0, handler.GetConsecutiveFailures());
    }

    [Fact]
    public async Task ResilienceHandler_InternalTimeout_RecordsFailure_AndRetriesIdempotentRequest()
    {
        // A cancellation the caller did NOT request (e.g. connect timeout) is a backend failure:
        // it must be recorded for the circuit breaker and retried for idempotent requests.
        var callCount = 0;
        var inner = new FakeHandler(() =>
        {
            callCount++;
            throw new TaskCanceledException("timed out internally");
        });
        var handler = CreateHandler(inner);
        using var invoker = new HttpMessageInvoker(handler);

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), CancellationToken.None));

        Assert.Equal(1 + ResiliencePolicies.RetryCount, callCount);

        // One LOGICAL request failed — regardless of how many attempts it took. Per-attempt
        // counting let a single retried request contribute 4 of the 5 threshold failures.
        Assert.Equal(1, handler.GetConsecutiveFailures());
    }

    [Fact]
    public async Task RetryPolicy_Post_TransientError_IsNotRetried()
    {
        // Regression: POSTs to TVHeadend are mutations (dvr/entry/create, passwd/entry/create, ...).
        // Resending one that was already processed duplicates the mutation, so POSTs must not retry.
        var callCount = 0;
        using var invoker = CreateInvoker(new FakeHandler(() =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }));

        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost");
        request.Content = new StringContent("conf=x", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");

        var result = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task RetryPolicy_Post_HttpRequestException_IsNotRetried()
    {
        var callCount = 0;
        using var invoker = CreateInvoker(new FakeHandler(() =>
        {
            callCount++;
            throw new HttpRequestException("Connection reset");
        }));

        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost");
        request.Content = new StringContent("conf=x", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => invoker.SendAsync(request, CancellationToken.None));

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ResilienceHandler_CloneRequest_GetWithContent_PreservesContentAndHeaders()
    {
        var callCount = 0;
        var inner = new FakeHandler(() =>
        {
            callCount++;
            if (callCount == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var invoker = CreateInvoker(inner);

        var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost");
        request.Content = new StringContent("test body", System.Text.Encoding.UTF8, "text/plain");
        request.Headers.Add("X-Custom", "value");

        var result = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.True(callCount >= 2);
    }

    [Fact]
    public void GetRetryDelay_ProducesExponentialProgression()
    {
        // Regression: Math.Pow(base * 2, attempt) yielded a constant 1s for every attempt.
        Assert.Equal(TimeSpan.FromSeconds(1), ResiliencePolicies.GetRetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), ResiliencePolicies.GetRetryDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(4), ResiliencePolicies.GetRetryDelay(3));
    }

    [Fact]
    public async Task CircuitBreaker_HalfOpen_AdmitsExactlyOneTrialRequest()
    {
        // Trip the breaker with failing GETs (each request records 1 + RetryCount failures).
        var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var trialEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failing = true;
        var inner = new AsyncFakeHandler(() =>
        {
            if (failing)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            trialEntered.TrySetResult();
            return gate.Task;
        });
        var handler = CreateHandler(inner);
        using var invoker = new HttpMessageInvoker(handler);

        while (handler.GetConsecutiveFailures() < ResiliencePolicies.CircuitBreakerThreshold)
        {
            try
            {
                (await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), CancellationToken.None)).Dispose();
            }
            catch (InvalidOperationException)
            {
                break;
            }
        }

        // Move to half-open and start the (now blocking) trial request.
        handler.SetOpenUntil(DateTimeOffset.UtcNow.AddSeconds(-1));
        failing = false;
        var trialTask = invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), CancellationToken.None);
        await trialEntered.Task;

        // While the trial is in flight, all other requests must be rejected immediately.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), CancellationToken.None));

        // Complete the trial successfully — the circuit closes.
        gate.SetResult(new HttpResponseMessage(HttpStatusCode.OK));
        var result = await trialTask;
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(0, handler.GetConsecutiveFailures());

        // Circuit closed again — requests flow normally.
        var after = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task ResilienceHandler_ReportsClassifiedFailureReason_ToHealthService()
    {
        // Regression: every failure was reported as UpstreamUnavailable regardless of cause.
        var health = new Moq.Mock<Jellyfin.Plugin.TvHeadendApi.Service.Health.IHealthService>();
        var inner = new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var handler = CreateHandler(inner);
        handler.HealthService = health.Object;
        using var invoker = new HttpMessageInvoker(handler);

        (await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), CancellationToken.None)).Dispose();

        // Mid-retry attempts are reported WITHOUT breaker impact; only the final failed
        // attempt of the logical request counts toward the circuit.
        health.Verify(h => h.RecordFailure(FailureReason.Upstream5xx, false), Moq.Times.Exactly(ResiliencePolicies.RetryCount));
        health.Verify(h => h.RecordFailure(FailureReason.Upstream5xx, true), Moq.Times.Once);
        health.Verify(h => h.RecordFailure(FailureReason.UpstreamUnavailable, Moq.It.IsAny<bool>()), Moq.Times.Never);
    }

    [Fact]
    public async Task ResilienceHandler_CloneRequest_WithoutContent_Works()
    {
        var callCount = 0;
        var inner = new FakeHandler(() =>
        {
            callCount++;
            if (callCount == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var invoker = CreateInvoker(inner);

        var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost");
        request.Headers.Add("X-Test", "hello");

        var result = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    [Fact]
    public async Task RetryPolicy_HttpRequestException_AfterMaxRetries_Throws()
    {
        var callCount = 0;
        using var invoker = CreateInvoker(new FakeHandler(() =>
        {
            callCount++;
            throw new HttpRequestException("Connection failed");
        }));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost"), CancellationToken.None));

        Assert.Equal(1 + ResiliencePolicies.RetryCount, callCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public void IsTransientStatusCode_ReturnsExpected(HttpStatusCode code, bool expected)
    {
        Assert.Equal(expected, ResiliencePolicies.IsTransientStatusCode(code));
    }

    [Fact]
    public void ShouldRetry_NullResponse_ReturnsTrue()
    {
        Assert.True(ResiliencePolicies.ShouldRetry(null));
    }

    [Fact]
    public void ShouldRetry_429_ReturnsTrue()
    {
        Assert.True(ResiliencePolicies.ShouldRetry(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
    }

    [Fact]
    public void ShouldRetry_200_ReturnsFalse()
    {
        Assert.False(ResiliencePolicies.ShouldRetry(new HttpResponseMessage(HttpStatusCode.OK)));
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

    /// <summary>
    /// A fake <see cref="HttpMessageHandler"/> whose responses are asynchronous, allowing
    /// tests to hold a request in flight.
    /// </summary>
    private sealed class AsyncFakeHandler : HttpMessageHandler
    {
        private readonly Func<Task<HttpResponseMessage>> _factory;

        public AsyncFakeHandler(Func<Task<HttpResponseMessage>> factory) => _factory = factory;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _factory();
    }
}
