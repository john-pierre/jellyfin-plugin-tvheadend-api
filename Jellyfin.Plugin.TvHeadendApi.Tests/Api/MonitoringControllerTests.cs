using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Api.Endpoint;
using Jellyfin.Plugin.TvHeadendApi.Model.Input;
using Jellyfin.Plugin.TvHeadendApi.Model.Status;
using Jellyfin.Plugin.TvHeadendApi.Model.Subscription;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Input;
using Jellyfin.Plugin.TvHeadendApi.Service.Status;
using Jellyfin.Plugin.TvHeadendApi.Service.Subscription;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

/// <summary>
/// Tests for the <see cref="MonitoringController"/> endpoints and constructor null guards.
/// </summary>
public class MonitoringControllerTests
{
    private static MonitoringController CreateController(
        IStatusService? status = null,
        IInputMonitorService? input = null,
        ISubscriptionService? sub = null,
        IHealthService? health = null)
    {
        return new MonitoringController(
            status ?? new Mock<IStatusService>().Object,
            input ?? new Mock<IInputMonitorService>().Object,
            sub ?? new Mock<ISubscriptionService>().Object,
            health ?? NullHealthService.Instance);
    }

    // --- GetStatus ---

    [Fact]
    public async Task GetStatus_WithResult_ReturnsOk()
    {
        var expected = new ActivityStatus();
        var mockStatus = new Mock<IStatusService>();
        mockStatus.Setup(x => x.GetActivityStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var sut = CreateController(status: mockStatus.Object);

        var result = await sut.GetStatus(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(expected, ok.Value);
    }

    [Fact]
    public async Task GetStatus_WhenNull_ReturnsBadRequest()
    {
        var mockStatus = new Mock<IStatusService>();
        mockStatus.Setup(x => x.GetActivityStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync((ActivityStatus?)null);
        var sut = CreateController(status: mockStatus.Object);

        var result = await sut.GetStatus(CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // --- GetConnections ---

    [Fact]
    public async Task GetConnections_ReturnsOk()
    {
        var expected = new List<ConnectionEntry> { new() };
        var mockStatus = new Mock<IStatusService>();
        mockStatus.Setup(x => x.GetConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var sut = CreateController(status: mockStatus.Object);

        var result = await sut.GetConnections(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(expected, ok.Value);
    }

    // --- GetInputs ---

    [Fact]
    public async Task GetInputs_ReturnsOk()
    {
        var expected = new List<InputStatusEntry> { new() };
        var mockInput = new Mock<IInputMonitorService>();
        mockInput.Setup(x => x.GetInputStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var sut = CreateController(input: mockInput.Object);

        var result = await sut.GetInputs(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(expected, ok.Value);
    }

    // --- GetSubscriptions ---

    [Fact]
    public async Task GetSubscriptions_ReturnsOk()
    {
        var expected = new List<SubscriptionEntry> { new() };
        var mockSub = new Mock<ISubscriptionService>();
        mockSub.Setup(x => x.GetActiveSubscriptionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var sut = CreateController(sub: mockSub.Object);

        var result = await sut.GetSubscriptions(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(expected, ok.Value);
    }

    // --- GetHealth ---

    [Fact]
    public void GetHealth_ReturnsOk()
    {
        var sut = CreateController();

        var result = sut.GetHealth();

        Assert.IsType<OkObjectResult>(result.Result);
    }

    // --- CheckHealth ---

    [Fact]
    public async Task CheckHealth_ReturnsOk()
    {
        var sut = CreateController();

        var result = await sut.CheckHealth(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    // --- Constructor null guards ---

    [Fact]
    public void Constructor_NullStatusService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MonitoringController(
            null!,
            new Mock<IInputMonitorService>().Object,
            new Mock<ISubscriptionService>().Object,
            NullHealthService.Instance));
    }

    [Fact]
    public void Constructor_NullInputMonitorService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MonitoringController(
            new Mock<IStatusService>().Object,
            null!,
            new Mock<ISubscriptionService>().Object,
            NullHealthService.Instance));
    }

    [Fact]
    public void Constructor_NullSubscriptionService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MonitoringController(
            new Mock<IStatusService>().Object,
            new Mock<IInputMonitorService>().Object,
            null!,
            NullHealthService.Instance));
    }

    [Fact]
    public void Constructor_NullHealthService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MonitoringController(
            new Mock<IStatusService>().Object,
            new Mock<IInputMonitorService>().Object,
            new Mock<ISubscriptionService>().Object,
            null!));
    }
}
