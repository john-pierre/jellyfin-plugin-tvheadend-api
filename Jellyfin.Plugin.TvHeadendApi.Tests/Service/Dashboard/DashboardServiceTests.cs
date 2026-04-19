using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Model.Input;
using Jellyfin.Plugin.TvHeadendApi.Model.Status;
using Jellyfin.Plugin.TvHeadendApi.Model.Subscription;
using Jellyfin.Plugin.TvHeadendApi.Service.Dashboard;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Input;
using Jellyfin.Plugin.TvHeadendApi.Service.Status;
using Jellyfin.Plugin.TvHeadendApi.Service.Subscription;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Dashboard;

public class DashboardServiceTests
{
    private readonly Mock<IDiagnosticService> _diagMock = new();
    private readonly Mock<IStatusService> _statusMock = new();
    private readonly Mock<IInputMonitorService> _inputMock = new();
    private readonly Mock<ISubscriptionService> _subMock = new();

    private DashboardService CreateSut()
    {
        return new DashboardService(
            _diagMock.Object,
            _statusMock.Object,
            _inputMock.Object,
            _subMock.Object,
            NullLogger<DashboardService>.Instance);
    }

    [Fact]
    public void Constructor_NullDiagnostic_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DashboardService(
            null!, _statusMock.Object, _inputMock.Object, _subMock.Object, NullLogger<DashboardService>.Instance));
    }

    [Fact]
    public void Constructor_NullStatus_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DashboardService(
            _diagMock.Object, null!, _inputMock.Object, _subMock.Object, NullLogger<DashboardService>.Instance));
    }

    [Fact]
    public void Constructor_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DashboardService(
            _diagMock.Object, _statusMock.Object, null!, _subMock.Object, NullLogger<DashboardService>.Instance));
    }

    [Fact]
    public void Constructor_NullSubscription_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DashboardService(
            _diagMock.Object, _statusMock.Object, _inputMock.Object, null!, NullLogger<DashboardService>.Instance));
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DashboardService(
            _diagMock.Object, _statusMock.Object, _inputMock.Object, _subMock.Object, null!));
    }

    [Fact]
    public async Task GetDashboardStatusAsync_AllServicesSucceed_ReturnsFullData()
    {
        var diag = new DiagnoseResult
        {
            OverallStatus = "OK",
            CompatibilityScore = 95,
            ServerVersion = "4.3-2055",
            LatencyMs = 12,
            ChannelCount = 42,
            DvrEntryCount = 5,
            Connection = "http://192.168.1.10:9981",
        };
        _diagMock.Setup(x => x.DiagnoseAsync(It.IsAny<CancellationToken>())).ReturnsAsync(diag);

        var activity = new ActivityStatus();
        _statusMock.Setup(x => x.GetActivityStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(activity);

        var inputs = new List<InputStatusEntry> { new InputStatusEntry { Input = "DVB-T" } };
        _inputMock.Setup(x => x.GetInputStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(inputs);

        var subs = new List<SubscriptionEntry> { new SubscriptionEntry { Channel = "ARD" } };
        _subMock.Setup(x => x.GetActiveSubscriptionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(subs);

        var conns = new List<ConnectionEntry> { new ConnectionEntry { User = "admin" } };
        _statusMock.Setup(x => x.GetConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(conns);

        var sut = CreateSut();
        var result = await sut.GetDashboardStatusAsync(CancellationToken.None);

        Assert.True(result.IsReachable);
        Assert.True(result.IsAuthenticated);
        Assert.Equal("4.3-2055", result.ServerVersion);
        Assert.Equal(95, result.CompatibilityScore);
        Assert.Equal(42, result.ChannelCount);
        Assert.Single(result.Inputs);
        Assert.Single(result.Subscriptions);
        Assert.Single(result.Connections);
        Assert.Null(result.ConnectionError);
        Assert.Null(result.InputsError);
        Assert.Null(result.SubscriptionsError);
        Assert.Null(result.ConnectionsError);
    }

    [Fact]
    public async Task GetDashboardStatusAsync_DiagnosticFails_SetsConnectionError()
    {
        _diagMock.Setup(x => x.DiagnoseAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection refused"));
        _statusMock.Setup(x => x.GetActivityStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync((ActivityStatus?)null);
        _inputMock.Setup(x => x.GetInputStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<InputStatusEntry>());
        _subMock.Setup(x => x.GetActiveSubscriptionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SubscriptionEntry>());
        _statusMock.Setup(x => x.GetConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionEntry>());

        var sut = CreateSut();
        var result = await sut.GetDashboardStatusAsync(CancellationToken.None);

        Assert.False(result.IsReachable);
        Assert.Equal("Connection refused", result.ConnectionError);
    }

    [Fact]
    public async Task GetDashboardStatusAsync_InputsFail_SetsInputsError()
    {
        _diagMock.Setup(x => x.DiagnoseAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiagnoseResult { OverallStatus = "OK" });
        _statusMock.Setup(x => x.GetActivityStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync((ActivityStatus?)null);
        _inputMock.Setup(x => x.GetInputStatusAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Timeout"));
        _subMock.Setup(x => x.GetActiveSubscriptionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SubscriptionEntry>());
        _statusMock.Setup(x => x.GetConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionEntry>());

        var sut = CreateSut();
        var result = await sut.GetDashboardStatusAsync(CancellationToken.None);

        Assert.True(result.IsReachable);
        Assert.Equal("Timeout", result.InputsError);
        Assert.Empty(result.Inputs);
    }

    [Fact]
    public async Task GetDashboardStatusAsync_SubscriptionsFail_SetsSubscriptionsError()
    {
        _diagMock.Setup(x => x.DiagnoseAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiagnoseResult { OverallStatus = "OK" });
        _statusMock.Setup(x => x.GetActivityStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync((ActivityStatus?)null);
        _inputMock.Setup(x => x.GetInputStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<InputStatusEntry>());
        _subMock.Setup(x => x.GetActiveSubscriptionsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Auth error"));
        _statusMock.Setup(x => x.GetConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionEntry>());

        var sut = CreateSut();
        var result = await sut.GetDashboardStatusAsync(CancellationToken.None);

        Assert.Equal("Auth error", result.SubscriptionsError);
        Assert.Empty(result.Subscriptions);
    }

    [Fact]
    public async Task GetDashboardStatusAsync_DiagnosticError_SetsNotReachable()
    {
        _diagMock.Setup(x => x.DiagnoseAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiagnoseResult { OverallStatus = "ERROR", Connection = "Connection refused" });
        _statusMock.Setup(x => x.GetActivityStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync((ActivityStatus?)null);
        _inputMock.Setup(x => x.GetInputStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<InputStatusEntry>());
        _subMock.Setup(x => x.GetActiveSubscriptionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SubscriptionEntry>());
        _statusMock.Setup(x => x.GetConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionEntry>());

        var sut = CreateSut();
        var result = await sut.GetDashboardStatusAsync(CancellationToken.None);

        Assert.False(result.IsReachable);
        Assert.False(result.IsAuthenticated);
        Assert.Equal("Connection refused", result.ConnectionError);
    }

    [Fact]
    public async Task GetDashboardStatusAsync_ConnectionsFail_SetsConnectionsError()
    {
        _diagMock.Setup(x => x.DiagnoseAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiagnoseResult { OverallStatus = "OK" });
        _statusMock.Setup(x => x.GetActivityStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync((ActivityStatus?)null);
        _inputMock.Setup(x => x.GetInputStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<InputStatusEntry>());
        _subMock.Setup(x => x.GetActiveSubscriptionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SubscriptionEntry>());
        _statusMock.Setup(x => x.GetConnectionsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Network error"));

        var sut = CreateSut();
        var result = await sut.GetDashboardStatusAsync(CancellationToken.None);

        Assert.Equal("Network error", result.ConnectionsError);
        Assert.Empty(result.Connections);
    }

    [Fact]
    public async Task GetDashboardStatusAsync_AlwaysHasTimestampAndPluginVersion()
    {
        _diagMock.Setup(x => x.DiagnoseAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiagnoseResult { OverallStatus = "OK" });
        _statusMock.Setup(x => x.GetActivityStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync((ActivityStatus?)null);
        _inputMock.Setup(x => x.GetInputStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<InputStatusEntry>());
        _subMock.Setup(x => x.GetActiveSubscriptionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SubscriptionEntry>());
        _statusMock.Setup(x => x.GetConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionEntry>());

        var sut = CreateSut();
        var result = await sut.GetDashboardStatusAsync(CancellationToken.None);

        Assert.NotEqual(default, result.Timestamp);
        Assert.NotEmpty(result.PluginVersion);
    }
}

