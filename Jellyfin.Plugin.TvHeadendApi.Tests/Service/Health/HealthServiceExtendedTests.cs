// Extended tests for HealthService — configurable thresholds and CheckHealthAsync.

using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Helper;

public class TvHeadendHealthServiceConfigurableTests
{
    private static HealthService CreateSut(
        IApiClient? apiClient = null,
        IUrlBuilder? urlBuilder = null,
        ConfigurationProvider? configProvider = null)
    {
        return new HealthService(
            apiClient ?? Mock.Of<IApiClient>(),
            urlBuilder ?? new UrlBuilder(),
            NullLogger<HealthService>.Instance,
            configProvider);
    }

    [Fact]
    public void CircuitBreaker_OpensAfterConfigurableThreshold()
    {
        var config = new PluginConfiguration
        {
            CircuitBreakerThreshold = 3,
            CircuitBreakerDurationSeconds = 30,
        };
        var configProvider = new ConfigurationProvider(() => config);
        var sut = CreateSut(configProvider: configProvider);

        for (int i = 0; i < 3; i++)
        {
            sut.RecordFailure(FailureReason.UpstreamUnavailable);
        }

        var snapshot = sut.GetSnapshot();
        Assert.Equal(HealthStatus.CircuitOpen, snapshot.Status);
        Assert.Equal(CircuitState.Open, snapshot.CircuitState);
        Assert.NotNull(snapshot.NextRetryUtc);
    }

    [Fact]
    public void ShouldBlockRequest_WhenCircuitOpenWithConfiguredDuration_ReturnsTrue()
    {
        var config = new PluginConfiguration
        {
            CircuitBreakerThreshold = 2,
            CircuitBreakerDurationSeconds = 300,
        };
        var configProvider = new ConfigurationProvider(() => config);
        var sut = CreateSut(configProvider: configProvider);

        sut.RecordFailure(FailureReason.UpstreamUnavailable);
        sut.RecordFailure(FailureReason.UpstreamUnavailable);

        Assert.True(sut.ShouldBlockRequest());
    }

    [Fact]
    public void RecordSuccess_WithoutResponseTime_StillSetsHealthy()
    {
        var sut = CreateSut();
        sut.RecordSuccess();

        var snapshot = sut.GetSnapshot();
        Assert.Equal(HealthStatus.Healthy, snapshot.Status);
        Assert.Null(snapshot.LastResponseTimeMs);
    }

    [Fact]
    public void RecordFailure_DnsFailure_SetsUnreachableStatus()
    {
        var sut = CreateSut();
        sut.RecordFailure(FailureReason.DnsFailure);

        Assert.Equal(HealthStatus.Unreachable, sut.GetSnapshot().Status);
    }

    [Fact]
    public void RecordFailure_Unknown_SetsDegraded()
    {
        var sut = CreateSut();
        sut.RecordFailure(FailureReason.Unknown);

        Assert.Equal(HealthStatus.Degraded, sut.GetSnapshot().Status);
    }

    [Fact]
    public void GetHealthHistory_WithoutDb_ReturnsEmpty()
    {
        var sut = CreateSut();
        var history = sut.GetHealthHistory();

        Assert.Empty(history);
    }

    [Fact]
    public async Task CheckHealthAsync_WithNullConfig_RecordsFailure()
    {
        var apiClient = new Mock<IApiClient>();
        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns((PluginConfiguration?)null);

        var sut = CreateSut(apiClient: apiClient.Object);
        var snapshot = await sut.CheckHealthAsync(CancellationToken.None);

        Assert.NotEqual(HealthStatus.Healthy, snapshot.Status);
        Assert.Equal(1, snapshot.ConsecutiveFailures);
    }

    [Fact]
    public void ConsecutiveFailures_IncrementCorrectly()
    {
        var sut = CreateSut();
        sut.RecordFailure(FailureReason.Timeout);
        sut.RecordFailure(FailureReason.Timeout);
        sut.RecordFailure(FailureReason.DnsFailure);

        Assert.Equal(3, sut.GetSnapshot().ConsecutiveFailures);
    }

    [Fact]
    public void RecordFailure_CircuitOpen_SetsDegradedModeActive()
    {
        var sut = CreateSut();
        sut.RecordFailure(FailureReason.CircuitOpen);

        var snapshot = sut.GetSnapshot();
        Assert.True(snapshot.IsDegradedModeActive);
    }

    [Fact]
    public void SnapshotUtc_IsPopulated()
    {
        var sut = CreateSut();
        var snapshot = sut.GetSnapshot();

        Assert.True(snapshot.SnapshotUtc > DateTimeOffset.MinValue);
    }
}
