// Circuit breaker observability and trigger-happiness tests: non-breaker failures stay
// visible without opening the circuit, and the Breaker metrics block answers how often the
// circuit opened, why, for how long, and how many requests it rejected.

using System;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Health;

public class CircuitBreakerMetricsTests
{
    private static HealthService CreateSut(int threshold = 5, int durationSeconds = 30)
    {
        var config = new PluginConfiguration
        {
            CircuitBreakerThreshold = threshold,
            CircuitBreakerDurationSeconds = durationSeconds,
        };
        return new HealthService(
            Mock.Of<IApiClient>(),
            new UrlBuilder(),
            NullLogger<HealthService>.Instance,
            new ConfigurationProvider(() => config));
    }

    [Fact]
    public void NonBreakerFailures_StayVisible_ButNeverOpenTheCircuit_NorDowngradeGlobalStatus()
    {
        // Per-channel stream errors (server responded) and mid-retry attempt failures report
        // with affectsCircuit=false — arbitrarily many must neither open the breaker NOR flip
        // the global status (backlog #4: a dead channel used to show "Degraded" next to a
        // "Connected, 100" Diagnose). They stay visible via LastFailureReason + metrics.
        var sut = CreateSut(threshold: 3);
        sut.RecordSuccess(); // establish a Healthy global status first

        for (int i = 0; i < 20; i++)
        {
            sut.RecordFailure(FailureReason.Upstream5xx, affectsCircuit: false);
        }

        var snapshot = sut.GetSnapshot();
        Assert.Equal(CircuitState.Closed, snapshot.CircuitState);
        Assert.Equal(0, snapshot.ConsecutiveFailures);
        Assert.Equal(HealthStatus.Healthy, snapshot.Status); // global status NOT downgraded
        Assert.Equal(FailureReason.Upstream5xx, snapshot.LastFailureReason); // but visible
        Assert.Equal(20, snapshot.Breaker.ReportedFailures);
        Assert.Equal(0, snapshot.Breaker.CircuitFailures);
        Assert.Equal(0, snapshot.Breaker.TimesOpened);
        Assert.Equal(20, snapshot.Breaker.FailureCountsByReason[nameof(FailureReason.Upstream5xx)]);
        Assert.False(sut.ShouldBlockRequest());
    }

    [Fact]
    public void BreakerFailures_OpenTheCircuit_AndRecordOpenMetrics()
    {
        var sut = CreateSut(threshold: 3, durationSeconds: 300);

        sut.RecordFailure(FailureReason.ConnectionRefused);
        sut.RecordFailure(FailureReason.ConnectionRefused);
        var before = DateTimeOffset.UtcNow;
        sut.RecordFailure(FailureReason.ConnectionRefused);

        var snapshot = sut.GetSnapshot();
        Assert.Equal(CircuitState.Open, snapshot.CircuitState);
        Assert.Equal(1, snapshot.Breaker.TimesOpened);
        Assert.Equal(FailureReason.ConnectionRefused, snapshot.Breaker.LastOpenReason);
        Assert.NotNull(snapshot.Breaker.LastOpenedAtUtc);
        Assert.True(snapshot.Breaker.LastOpenedAtUtc >= before.AddSeconds(-1));
        Assert.Equal(3, snapshot.Breaker.CircuitFailures);
        Assert.Equal(3, snapshot.Breaker.ReportedFailures);
        Assert.Equal(3, snapshot.Breaker.Threshold);
        Assert.Equal(300, snapshot.Breaker.OpenDurationSeconds);
        Assert.True(snapshot.Breaker.TotalOpenDurationMs >= 0);
    }

    [Fact]
    public void RejectionsWhileOpen_AreCounted()
    {
        var sut = CreateSut(threshold: 2, durationSeconds: 300);
        sut.RecordFailure(FailureReason.Timeout);
        sut.RecordFailure(FailureReason.Timeout);

        Assert.True(sut.ShouldBlockRequest());
        Assert.True(sut.ShouldBlockRequest());
        Assert.True(sut.ShouldBlockRequest());

        Assert.Equal(3, sut.GetSnapshot().Breaker.RejectedWhileOpen);
    }

    [Fact]
    public void HalfOpenTrialSuccess_ClosesEpisode_AndRecordsDurations()
    {
        var sut = CreateSut(threshold: 2, durationSeconds: 300);
        sut.RecordFailure(FailureReason.ConnectionRefused);
        sut.RecordFailure(FailureReason.ConnectionRefused);
        Assert.Equal(CircuitState.Open, sut.GetSnapshot().CircuitState);

        // Open window elapses -> the next check admits a half-open trial.
        sut.SetCircuitOpenUntil(DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.False(sut.ShouldBlockRequest());

        var halfOpen = sut.GetSnapshot();
        Assert.Equal(CircuitState.HalfOpen, halfOpen.CircuitState);
        Assert.Equal(1, halfOpen.Breaker.HalfOpenTrials);

        // The trial succeeds — episode ends, durations recorded, circuit closes.
        sut.RecordSuccess(42);

        var closed = sut.GetSnapshot();
        Assert.Equal(CircuitState.Closed, closed.CircuitState);
        Assert.Equal(1, closed.Breaker.HalfOpenTrialSuccesses);
        Assert.Equal(0, closed.Breaker.HalfOpenTrialFailures);
        Assert.True(closed.Breaker.LastOpenDurationMs >= 0);
        Assert.True(closed.Breaker.TotalOpenDurationMs >= closed.Breaker.LastOpenDurationMs);
        Assert.Equal(1, closed.Breaker.TimesOpened);
    }

    [Fact]
    public void HalfOpenTrialFailure_ReopensSameEpisode_WithoutCountingANewOpen()
    {
        var sut = CreateSut(threshold: 2, durationSeconds: 300);
        sut.RecordFailure(FailureReason.ConnectionRefused);
        sut.RecordFailure(FailureReason.ConnectionRefused);
        sut.SetCircuitOpenUntil(DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.False(sut.ShouldBlockRequest()); // half-open trial admitted

        // Trial fails -> circuit re-opens, but this is the SAME outage episode.
        sut.RecordFailure(FailureReason.ConnectionRefused);

        var snapshot = sut.GetSnapshot();
        Assert.Equal(CircuitState.Open, snapshot.CircuitState);
        Assert.Equal(1, snapshot.Breaker.TimesOpened);
        Assert.Equal(1, snapshot.Breaker.HalfOpenTrialFailures);
        Assert.True(sut.ShouldBlockRequest());

        // Eventually a trial succeeds — one episode, one recovery.
        sut.SetCircuitOpenUntil(DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.False(sut.ShouldBlockRequest());
        sut.RecordSuccess();

        var recovered = sut.GetSnapshot();
        Assert.Equal(1, recovered.Breaker.TimesOpened);
        Assert.Equal(2, recovered.Breaker.HalfOpenTrials);
        Assert.Equal(1, recovered.Breaker.HalfOpenTrialSuccesses);
    }

    [Fact]
    public void SecondOutage_CountsASecondOpen()
    {
        var sut = CreateSut(threshold: 2, durationSeconds: 300);

        // Outage 1 + recovery.
        sut.RecordFailure(FailureReason.ConnectionRefused);
        sut.RecordFailure(FailureReason.ConnectionRefused);
        sut.SetCircuitOpenUntil(DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.False(sut.ShouldBlockRequest());
        sut.RecordSuccess();

        // Outage 2.
        sut.RecordFailure(FailureReason.Timeout);
        sut.RecordFailure(FailureReason.Timeout);

        var snapshot = sut.GetSnapshot();
        Assert.Equal(2, snapshot.Breaker.TimesOpened);
        Assert.Equal(FailureReason.Timeout, snapshot.Breaker.LastOpenReason);
    }
}
