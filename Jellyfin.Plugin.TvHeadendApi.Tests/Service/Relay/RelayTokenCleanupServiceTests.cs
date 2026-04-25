// Tests for RelayTokenCleanupService — hosted service lifecycle.

using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Relay;

public class RelayTokenCleanupServiceTests
{
    private static RelayTokenOptions CreateOptions()
    {
        var config = new PluginConfiguration { CleanupExpiredTokensIntervalMinutes = 60 };
        return new RelayTokenOptions(new ConfigurationProvider(() => config));
    }

    [Fact]
    public async Task StartAsync_DoesNotThrow()
    {
        var tokenService = new Mock<IRelayTokenService>();
        using var sut = new RelayTokenCleanupService(
            NullLogger<RelayTokenCleanupService>.Instance,
            tokenService.Object,
            CreateOptions());

        await sut.StartAsync(CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_WithoutStart_DoesNotThrow()
    {
        var tokenService = new Mock<IRelayTokenService>();
        using var sut = new RelayTokenCleanupService(
            NullLogger<RelayTokenCleanupService>.Instance,
            tokenService.Object,
            CreateOptions());

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Dispose_WithoutStart_DoesNotThrow()
    {
        var tokenService = new Mock<IRelayTokenService>();
        var sut = new RelayTokenCleanupService(
            NullLogger<RelayTokenCleanupService>.Instance,
            tokenService.Object,
            CreateOptions());

        sut.Dispose();
    }

    [Fact]
    public void Constructor_ThrowsOnNullLogger()
    {
        Assert.Throws<ArgumentNullException>(() => new RelayTokenCleanupService(
            null!, Mock.Of<IRelayTokenService>(), CreateOptions()));
    }

    [Fact]
    public void Constructor_ThrowsOnNullTokenService()
    {
        Assert.Throws<ArgumentNullException>(() => new RelayTokenCleanupService(
            NullLogger<RelayTokenCleanupService>.Instance, null!, CreateOptions()));
    }

    [Fact]
    public void Constructor_ThrowsOnNullOptions()
    {
        Assert.Throws<ArgumentNullException>(() => new RelayTokenCleanupService(
            NullLogger<RelayTokenCleanupService>.Instance, Mock.Of<IRelayTokenService>(), null!));
    }
}
