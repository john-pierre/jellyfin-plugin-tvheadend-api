// Tests for the centralized TVHeadend health service, failure classifier, and circuit breaker behavior.

using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Helper;

/// <summary>
/// Tests for <see cref="FailureClassifier"/>.
/// </summary>
public class FailureClassifierTests
{
    [Fact]
    public void Classify_OperationCancelled_ReturnsCancelled()
    {
        var result = FailureClassifier.Classify(new OperationCanceledException());
        Assert.Equal(FailureReason.Cancelled, result);
    }

    [Fact]
    public void Classify_TaskCancelled_ReturnsCancelled()
    {
        var result = FailureClassifier.Classify(new TaskCanceledException());
        Assert.Equal(FailureReason.Cancelled, result);
    }

    [Fact]
    public void Classify_HttpClientTimeout_TaskCancelledWithTimeoutInner_ReturnsTimeout()
    {
        // HttpClient.Timeout surfaces as TaskCanceledException wrapping TimeoutException (.NET 5+).
        var timeout = new TaskCanceledException("timed out", new TimeoutException());
        Assert.Equal(FailureReason.Timeout, FailureClassifier.Classify(timeout));
    }

    [Fact]
    public void Classify_CircuitBreakerException_ReturnsCircuitOpen()
    {
        var result = FailureClassifier.Classify(new InvalidOperationException("Circuit breaker is open"));
        Assert.Equal(FailureReason.CircuitOpen, result);
    }

    [Fact]
    public void Classify_GenericException_ReturnsUnexpected()
    {
        var result = FailureClassifier.Classify(new Exception("something"));
        Assert.Equal(FailureReason.UnexpectedException, result);
    }

    [Fact]
    public void Classify_ConnectionRefused_ReturnsConnectionRefused()
    {
        var socketEx = new SocketException((int)SocketError.ConnectionRefused);
        var httpEx = new HttpRequestException("Connection refused", socketEx);
        Assert.Equal(FailureReason.ConnectionRefused, FailureClassifier.Classify(httpEx));
    }

    [Fact]
    public void Classify_DnsFailure_ReturnsDnsFailure()
    {
        var socketEx = new SocketException((int)SocketError.HostNotFound);
        var httpEx = new HttpRequestException("Host not found", socketEx);
        Assert.Equal(FailureReason.DnsFailure, FailureClassifier.Classify(httpEx));
    }

    [Fact]
    public void Classify_HttpRequestException_NoInner_ReturnsUpstreamUnavailable()
    {
        var httpEx = new HttpRequestException("Some error");
        Assert.Equal(FailureReason.UpstreamUnavailable, FailureClassifier.Classify(httpEx));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, FailureReason.AuthFailed)]
    [InlineData(HttpStatusCode.Forbidden, FailureReason.AuthFailed)]
    [InlineData(HttpStatusCode.RequestTimeout, FailureReason.Timeout)]
    [InlineData(HttpStatusCode.GatewayTimeout, FailureReason.Timeout)]
    [InlineData(HttpStatusCode.InternalServerError, FailureReason.Upstream5xx)]
    [InlineData(HttpStatusCode.BadGateway, FailureReason.Upstream5xx)]
    [InlineData(HttpStatusCode.NotFound, FailureReason.Upstream4xx)]
    [InlineData(HttpStatusCode.OK, FailureReason.None)]
    public void ClassifyStatusCode_ReturnsExpected(HttpStatusCode code, FailureReason expected)
    {
        Assert.Equal(expected, FailureClassifier.ClassifyStatusCode(code));
    }
}

/// <summary>
/// Tests for <see cref="HealthService"/> state transitions.
/// </summary>
public class TvHeadendHealthServiceTests
{
    private static HealthService CreateSut()
    {
        var apiClient = new Moq.Mock<IApiClient>().Object;
        var urlBuilder = new Moq.Mock<IUrlBuilder>().Object;
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<HealthService>.Instance;
        return new HealthService(apiClient, urlBuilder, logger);
    }

    [Fact]
    public void InitialState_IsUnknown()
    {
        var sut = CreateSut();
        var snapshot = sut.GetSnapshot();
        Assert.Equal(HealthStatus.Unknown, snapshot.Status);
        Assert.Equal(CircuitState.Closed, snapshot.CircuitState);
        Assert.False(snapshot.IsDegradedModeActive);
    }

    [Fact]
    public void RecordSuccess_SetsHealthy()
    {
        var sut = CreateSut();
        sut.RecordSuccess(42);
        var snapshot = sut.GetSnapshot();
        Assert.Equal(HealthStatus.Healthy, snapshot.Status);
        Assert.Equal(42, snapshot.LastResponseTimeMs);
        Assert.Equal(0, snapshot.ConsecutiveFailures);
    }

    [Fact]
    public void RecordFailure_SetsCorrectStatus()
    {
        var sut = CreateSut();
        sut.RecordFailure(FailureReason.Timeout);
        var snapshot = sut.GetSnapshot();
        Assert.Equal(HealthStatus.Timeout, snapshot.Status);
        Assert.Equal(1, snapshot.ConsecutiveFailures);
        Assert.True(snapshot.IsDegradedModeActive);
    }

    [Fact]
    public void RecordFailure_AuthFailed_SetsAuthFailedStatus()
    {
        var sut = CreateSut();
        sut.RecordFailure(FailureReason.AuthFailed);
        Assert.Equal(HealthStatus.AuthFailed, sut.GetSnapshot().Status);
    }

    [Fact]
    public void RecordFailure_ConnectionRefused_SetsUnreachable()
    {
        var sut = CreateSut();
        sut.RecordFailure(FailureReason.ConnectionRefused);
        Assert.Equal(HealthStatus.Unreachable, sut.GetSnapshot().Status);
    }

    [Fact]
    public void CircuitOpens_AfterThresholdFailures()
    {
        var sut = CreateSut();
        for (int i = 0; i < 5; i++)
        {
            sut.RecordFailure(FailureReason.Timeout);
        }

        var snapshot = sut.GetSnapshot();
        Assert.Equal(CircuitState.Open, snapshot.CircuitState);
        Assert.Equal(HealthStatus.CircuitOpen, snapshot.Status);
        Assert.True(sut.ShouldBlockRequest());
    }

    [Fact]
    public void RecordSuccess_ClosesCircuit()
    {
        var sut = CreateSut();
        for (int i = 0; i < 5; i++)
        {
            sut.RecordFailure(FailureReason.Timeout);
        }

        Assert.Equal(CircuitState.Open, sut.GetSnapshot().CircuitState);

        sut.RecordSuccess(10);
        var snapshot = sut.GetSnapshot();
        Assert.Equal(CircuitState.Closed, snapshot.CircuitState);
        Assert.Equal(HealthStatus.Healthy, snapshot.Status);
        Assert.False(sut.ShouldBlockRequest());
    }

    [Fact]
    public void ShouldBlockRequest_ReturnsFalse_WhenClosed()
    {
        var sut = CreateSut();
        Assert.False(sut.ShouldBlockRequest());
    }

    [Fact]
    public void ConsecutiveFailures_ResetOnSuccess()
    {
        var sut = CreateSut();
        sut.RecordFailure(FailureReason.Timeout);
        sut.RecordFailure(FailureReason.Timeout);
        Assert.Equal(2, sut.GetSnapshot().ConsecutiveFailures);

        sut.RecordSuccess();
        Assert.Equal(0, sut.GetSnapshot().ConsecutiveFailures);
    }

    [Fact]
    public void LastFailureReason_Tracks()
    {
        var sut = CreateSut();
        sut.RecordFailure(FailureReason.DnsFailure);
        Assert.Equal(FailureReason.DnsFailure, sut.GetSnapshot().LastFailureReason);

        sut.RecordFailure(FailureReason.Upstream5xx);
        Assert.Equal(FailureReason.Upstream5xx, sut.GetSnapshot().LastFailureReason);
    }

    [Fact]
    public void HalfOpen_AdmitsExactlyOneTrialRequest()
    {
        var sut = CreateSut();
        for (int i = 0; i < 5; i++)
        {
            sut.RecordFailure(FailureReason.Timeout);
        }

        Assert.True(ShouldBlockFromIndependentCaller(sut));

        // Simulate the open window elapsing
        sut.SetCircuitOpenUntil(DateTimeOffset.UtcNow.AddSeconds(-1));

        // First caller is admitted as the single trial request
        Assert.False(ShouldBlockFromIndependentCaller(sut));
        Assert.Equal(CircuitState.HalfOpen, sut.GetSnapshot().CircuitState);

        // Concurrent callers are rejected until the trial resolves
        Assert.True(ShouldBlockFromIndependentCaller(sut));
        Assert.True(ShouldBlockFromIndependentCaller(sut));
    }

    [Fact]
    public void HalfOpen_TrialOwnerFlow_PassesRepeatedChecks()
    {
        var sut = CreateSut();
        for (int i = 0; i < 5; i++)
        {
            sut.RecordFailure(FailureReason.Timeout);
        }

        sut.SetCircuitOpenUntil(DateTimeOffset.UtcNow.AddSeconds(-1));

        // This flow is admitted as the trial; its own re-check (the ResilienceHandler
        // fast-fail on the same request) must pass while other callers stay blocked.
        Assert.False(sut.ShouldBlockRequest());
        Assert.False(sut.ShouldBlockRequest());
        Assert.True(ShouldBlockFromIndependentCaller(sut));
    }

    [Fact]
    public void HalfOpen_TrialSuccess_ClosesCircuitAndUnblocks()
    {
        var sut = CreateSut();
        for (int i = 0; i < 5; i++)
        {
            sut.RecordFailure(FailureReason.Timeout);
        }

        sut.SetCircuitOpenUntil(DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.False(sut.ShouldBlockRequest());

        sut.RecordSuccess(10);

        Assert.Equal(CircuitState.Closed, sut.GetSnapshot().CircuitState);
        Assert.False(ShouldBlockFromIndependentCaller(sut));
        Assert.False(sut.ShouldBlockRequest());
    }

    [Fact]
    public void HalfOpen_TrialFailure_ReopensCircuit()
    {
        var sut = CreateSut();
        for (int i = 0; i < 5; i++)
        {
            sut.RecordFailure(FailureReason.Timeout);
        }

        sut.SetCircuitOpenUntil(DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.False(sut.ShouldBlockRequest());

        sut.RecordFailure(FailureReason.Timeout);

        var snapshot = sut.GetSnapshot();
        Assert.Equal(CircuitState.Open, snapshot.CircuitState);
        Assert.True(sut.ShouldBlockRequest());
        Assert.True(ShouldBlockFromIndependentCaller(sut));
    }

    [Fact]
    public void HalfOpen_AbandonedTrial_AdmitsNewTrialAfterOpenDuration()
    {
        var sut = CreateSut();
        for (int i = 0; i < 5; i++)
        {
            sut.RecordFailure(FailureReason.Timeout);
        }

        sut.SetCircuitOpenUntil(DateTimeOffset.UtcNow.AddSeconds(-1));

        // Trial admitted by a caller that never resolves it
        Assert.False(ShouldBlockFromIndependentCaller(sut));
        Assert.True(ShouldBlockFromIndependentCaller(sut));

        // Simulate the admitted trial never resolving within the open duration
        sut.SetHalfOpenTrialStarted(DateTimeOffset.UtcNow.AddMinutes(-1));

        // A new trial is admitted, and subsequent callers are rejected again
        Assert.False(ShouldBlockFromIndependentCaller(sut));
        Assert.True(ShouldBlockFromIndependentCaller(sut));
    }

    /// <summary>
    /// Invokes <see cref="HealthService.ShouldBlockRequest"/> without flowing the current
    /// <see cref="ExecutionContext"/>, simulating an unrelated concurrent caller that does
    /// not own any half-open trial admitted by this test's flow. A dedicated short-lived
    /// thread is used (not <c>Task.Run</c> with suppressed flow — AsyncLocal mutations made
    /// on an unflowed thread-pool thread leak into later work items on the same thread).
    /// </summary>
    private static bool ShouldBlockFromIndependentCaller(HealthService sut)
    {
        var result = false;
        Thread thread;
        using (ExecutionContext.SuppressFlow())
        {
            thread = new Thread(() => result = sut.ShouldBlockRequest());
            thread.Start();
        }

        thread.Join();
        return result;
    }

    [Fact]
    public void Timestamps_ArePopulated()
    {
        var sut = CreateSut();
        var before = DateTimeOffset.UtcNow;

        sut.RecordSuccess(5);
        var snapshot = sut.GetSnapshot();
        Assert.NotNull(snapshot.LastSuccessUtc);
        Assert.True(snapshot.LastSuccessUtc >= before);

        sut.RecordFailure(FailureReason.Timeout);
        snapshot = sut.GetSnapshot();
        Assert.NotNull(snapshot.LastFailureUtc);
        Assert.True(snapshot.LastFailureUtc >= before);
    }
}

/// <summary>
/// Tests for <see cref="OperationTimeouts"/>.
/// </summary>
public class OperationTimeoutsTests
{
    [Theory]
    [InlineData(OperationType.Health, 3)]
    [InlineData(OperationType.Image, 5)]
    [InlineData(OperationType.Metadata, 10)]
    [InlineData(OperationType.StreamStartup, 8)]
    [InlineData(OperationType.BackgroundRefresh, 15)]
    public void GetTimeout_ReturnsExpectedSeconds(OperationType type, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), OperationTimeouts.GetTimeout(type));
    }
}
