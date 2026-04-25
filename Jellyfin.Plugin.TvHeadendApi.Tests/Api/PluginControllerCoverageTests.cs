using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Api;
using Jellyfin.Plugin.TvHeadendApi.Model.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Model.Input;
using Jellyfin.Plugin.TvHeadendApi.Model.Statistic;
using Jellyfin.Plugin.TvHeadendApi.Model.Status;
using Jellyfin.Plugin.TvHeadendApi.Model.Subscription;
using Jellyfin.Plugin.TvHeadendApi.Service.Auth;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Input;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Statistic;
using Jellyfin.Plugin.TvHeadendApi.Service.Status;
using Jellyfin.Plugin.TvHeadendApi.Service.Subscription;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

/// <summary>
/// Additional PluginController tests for uncovered endpoints and constructor null guards.
/// </summary>
public class PluginControllerCoverageTests
{
    private static PluginController CreateController(
        IDiagnosticService? diag = null,
        IDefaultProfileService? profile = null,
        ITokenService? token = null,
        IStatisticsService? stats = null,
        IStatusService? status = null,
        IInputMonitorService? input = null,
        ISubscriptionService? sub = null)
    {
        return new PluginController(
            diag ?? new Mock<IDiagnosticService>().Object,
            profile ?? new Mock<IDefaultProfileService>().Object,
            token ?? new Mock<ITokenService>().Object,
            stats ?? new Mock<IStatisticsService>().Object,
            status ?? new Mock<IStatusService>().Object,
            input ?? new Mock<IInputMonitorService>().Object,
            sub ?? new Mock<ISubscriptionService>().Object,
            NullHealthService.Instance);
    }

    // --- GetPluginInfo ---

    [Fact]
    public void GetPluginInfo_ReturnsOk()
    {
        var sut = CreateController();
        var result = sut.GetPluginInfo();
        Assert.IsType<OkObjectResult>(result.Result);
    }

    // --- GetStatistics ---

    [Fact]
    public void GetStatistics_ReturnsOkWithPayload()
    {
        var mockStats = new Mock<IStatisticsService>();
        mockStats.Setup(x => x.GetStatistics(30)).Returns(new ViewingStatisticsResult());
        var sut = CreateController(stats: mockStats.Object);

        var result = sut.GetStatistics(30);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<ViewingStatisticsResult>(ok.Value);
    }

    // --- ClearStatistics ---

    [Fact]
    public void ClearStatistics_ReturnsOk()
    {
        var mockStats = new Mock<IStatisticsService>();
        var sut = CreateController(stats: mockStats.Object);

        var result = sut.ClearStatistics();

        Assert.IsType<OkObjectResult>(result);
        mockStats.Verify(x => x.ClearStatistics(), Times.Once);
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

    // --- Constructor null guards ---

    [Fact]
    public void Constructor_NullDiagnose_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PluginController(
            null!,
            new Mock<IDefaultProfileService>().Object,
            new Mock<ITokenService>().Object,
            new Mock<IStatisticsService>().Object,
            new Mock<IStatusService>().Object,
            new Mock<IInputMonitorService>().Object,
            new Mock<ISubscriptionService>().Object,
            NullHealthService.Instance));
    }

    [Fact]
    public void Constructor_NullProfileService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PluginController(
            new Mock<IDiagnosticService>().Object,
            null!,
            new Mock<ITokenService>().Object,
            new Mock<IStatisticsService>().Object,
            new Mock<IStatusService>().Object,
            new Mock<IInputMonitorService>().Object,
            new Mock<ISubscriptionService>().Object,
            NullHealthService.Instance));
    }

    [Fact]
    public void Constructor_NullTokenService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PluginController(
            new Mock<IDiagnosticService>().Object,
            new Mock<IDefaultProfileService>().Object,
            null!,
            new Mock<IStatisticsService>().Object,
            new Mock<IStatusService>().Object,
            new Mock<IInputMonitorService>().Object,
            new Mock<ISubscriptionService>().Object,
            NullHealthService.Instance));
    }

    [Fact]
    public void Constructor_NullStatisticsService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PluginController(
            new Mock<IDiagnosticService>().Object,
            new Mock<IDefaultProfileService>().Object,
            new Mock<ITokenService>().Object,
            null!,
            new Mock<IStatusService>().Object,
            new Mock<IInputMonitorService>().Object,
            new Mock<ISubscriptionService>().Object,
            NullHealthService.Instance));
    }

    [Fact]
    public void Constructor_NullStatusService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PluginController(
            new Mock<IDiagnosticService>().Object,
            new Mock<IDefaultProfileService>().Object,
            new Mock<ITokenService>().Object,
            new Mock<IStatisticsService>().Object,
            null!,
            new Mock<IInputMonitorService>().Object,
            new Mock<ISubscriptionService>().Object,
            NullHealthService.Instance));
    }

    [Fact]
    public void Constructor_NullInputMonitorService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PluginController(
            new Mock<IDiagnosticService>().Object,
            new Mock<IDefaultProfileService>().Object,
            new Mock<ITokenService>().Object,
            new Mock<IStatisticsService>().Object,
            new Mock<IStatusService>().Object,
            null!,
            new Mock<ISubscriptionService>().Object,
            NullHealthService.Instance));
    }

    [Fact]
    public void Constructor_NullSubscriptionService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PluginController(
            new Mock<IDiagnosticService>().Object,
            new Mock<IDefaultProfileService>().Object,
            new Mock<ITokenService>().Object,
            new Mock<IStatisticsService>().Object,
            new Mock<IStatusService>().Object,
            new Mock<IInputMonitorService>().Object,
            null!,
            NullHealthService.Instance));
    }
}
