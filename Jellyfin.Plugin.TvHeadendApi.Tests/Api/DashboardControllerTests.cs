using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Api;
using Jellyfin.Plugin.TvHeadendApi.Model.Dashboard;
using Jellyfin.Plugin.TvHeadendApi.Service.Dashboard;
using Jellyfin.Plugin.TvHeadendApi.Service.Relay;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Api;

/// <summary>
/// Tests for <see cref="DashboardController"/>.
/// </summary>
public class DashboardControllerTests
{
    private static readonly Mock<IRelayMetricsService> MockMetrics = new();

    [Fact]
    public void Constructor_NullDashboardService_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new DashboardController(null!, MockMetrics.Object));
    }

    [Fact]
    public async Task GetDashboard_ReturnsOkWithDashboardStatus()
    {
        var expected = new DashboardStatus { IsReachable = true, PluginVersion = "1.0.0" };
        var mockService = new Mock<IDashboardService>();
        mockService.Setup(x => x.GetDashboardStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var sut = new DashboardController(mockService.Object, MockMetrics.Object);
        var result = await sut.GetDashboard(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<DashboardStatus>(ok.Value);
        Assert.True(payload.IsReachable);
        Assert.Equal("1.0.0", payload.PluginVersion);
    }

    [Fact]
    public async Task GetDashboard_CallsServiceOnce()
    {
        var mockService = new Mock<IDashboardService>();
        mockService.Setup(x => x.GetDashboardStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new DashboardStatus());

        var sut = new DashboardController(mockService.Object, MockMetrics.Object);
        await sut.GetDashboard(CancellationToken.None);

        mockService.Verify(x => x.GetDashboardStatusAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
