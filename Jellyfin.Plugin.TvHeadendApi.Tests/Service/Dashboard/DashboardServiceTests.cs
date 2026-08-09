using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Model.Input;
using Jellyfin.Plugin.TvHeadendApi.Model.Status;
using Jellyfin.Plugin.TvHeadendApi.Model.Subscription;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Dashboard;
using Jellyfin.Plugin.TvHeadendApi.Service.Database;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Input;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Jellyfin.Plugin.TvHeadendApi.Service.Status;
using Jellyfin.Plugin.TvHeadendApi.Service.Storage;
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
    private readonly Mock<IUrlBuilder> _urlBuilderMock = new();
    private readonly Mock<IApiClient> _apiClientMock = new();

    private static DatabaseHealthService CreateTestDbHealth()
    {
        var dir = Path.Combine(Path.GetTempPath(), "test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var pathProvider = new DataFolderPathProvider(() => dir);
        var provider = new DatabaseProvider(pathProvider);
        var factory = new DatabaseConnectionFactory(provider);
        var migration = new DatabaseMigrationService(factory, NullLogger<DatabaseMigrationService>.Instance);
        var recovery = new DatabaseRecoveryService(provider, migration, factory, NullLogger<DatabaseRecoveryService>.Instance);
        var health = new DatabaseHealthService(provider, factory, migration, recovery, NullLogger<DatabaseHealthService>.Instance);
        health.Initialize();
        return health;
    }

    private DashboardService CreateSut(PluginConfiguration? config = null)
    {
        var effectiveConfig = config ?? new PluginConfiguration { Username = "admin", Password = "secret" };
        var configProvider = new ConfigurationProvider(() => effectiveConfig);
        return new DashboardService(
            _diagMock.Object,
            _statusMock.Object,
            _inputMock.Object,
            _subMock.Object,
            _urlBuilderMock.Object,
            _apiClientMock.Object,
            NullHealthService.Instance,
            CreateTestDbHealth(),
            configProvider,
            NullLogger<DashboardService>.Instance);
    }

    private static ConfigurationProvider CreateConfiguredProvider()
    {
        return new ConfigurationProvider(() => new PluginConfiguration { Username = "admin", Password = "secret" });
    }

    [Fact]
    public void Constructor_NullDiagnostic_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DashboardService(
            null!, _statusMock.Object, _inputMock.Object, _subMock.Object, _urlBuilderMock.Object, _apiClientMock.Object, NullHealthService.Instance, CreateTestDbHealth(), CreateConfiguredProvider(), NullLogger<DashboardService>.Instance));
    }

    [Fact]
    public void Constructor_NullStatus_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DashboardService(
            _diagMock.Object, null!, _inputMock.Object, _subMock.Object, _urlBuilderMock.Object, _apiClientMock.Object, NullHealthService.Instance, CreateTestDbHealth(), CreateConfiguredProvider(), NullLogger<DashboardService>.Instance));
    }

    [Fact]
    public void Constructor_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DashboardService(
            _diagMock.Object, _statusMock.Object, null!, _subMock.Object, _urlBuilderMock.Object, _apiClientMock.Object, NullHealthService.Instance, CreateTestDbHealth(), CreateConfiguredProvider(), NullLogger<DashboardService>.Instance));
    }

    [Fact]
    public void Constructor_NullSubscription_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DashboardService(
            _diagMock.Object, _statusMock.Object, _inputMock.Object, null!, _urlBuilderMock.Object, _apiClientMock.Object, NullHealthService.Instance, CreateTestDbHealth(), CreateConfiguredProvider(), NullLogger<DashboardService>.Instance));
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DashboardService(
            _diagMock.Object, _statusMock.Object, _inputMock.Object, _subMock.Object, _urlBuilderMock.Object, _apiClientMock.Object, NullHealthService.Instance, CreateTestDbHealth(), CreateConfiguredProvider(), null!));
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

    [Fact]
    public async Task GetDashboardStatusAsync_WhenActivityEndpointUnavailable_SynthesizesCountsFromSections()
    {
        _diagMock.Setup(x => x.DiagnoseAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiagnoseResult { OverallStatus = "OK" });
        _statusMock.Setup(x => x.GetActivityStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync((ActivityStatus?)null);

        _inputMock.Setup(x => x.GetInputStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<InputStatusEntry>());
        _subMock.Setup(x => x.GetActiveSubscriptionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SubscriptionEntry> { new SubscriptionEntry(), new SubscriptionEntry() });
        _statusMock.Setup(x => x.GetConnectionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConnectionEntry> { new ConnectionEntry() });

        var sut = CreateSut();
        var result = await sut.GetDashboardStatusAsync(CancellationToken.None);

        Assert.NotNull(result.Activity);
        Assert.Equal(2, result.Activity!.SubscriptionCount);
        Assert.Equal(1, result.Activity.ConnectionCount);
    }

    [Fact]
    public async Task GetDashboardStatusAsync_WhenConfigurationAvailable_UsesUrlBuilderForBaseUrl()
    {
        var pluginConfiguration = new PluginConfiguration { Host = "tvh.local", Port = 9981 };
        _apiClientMock.Setup(x => x.GetCurrentConfiguration()).Returns(pluginConfiguration);
        _urlBuilderMock.Setup(x => x.GetBaseUrl(pluginConfiguration)).Returns("http://tvh.local:9981");

        _diagMock.Setup(x => x.DiagnoseAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiagnoseResult { OverallStatus = "OK", Connection = "fallback-value" });
        _statusMock.Setup(x => x.GetActivityStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ActivityStatus());
        _inputMock.Setup(x => x.GetInputStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<InputStatusEntry>());
        _subMock.Setup(x => x.GetActiveSubscriptionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SubscriptionEntry>());
        _statusMock.Setup(x => x.GetConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionEntry>());

        var sut = CreateSut();
        var result = await sut.GetDashboardStatusAsync(CancellationToken.None);

        Assert.Equal("http://tvh.local:9981", result.BaseUrl);
        _urlBuilderMock.Verify(x => x.GetBaseUrl(pluginConfiguration), Times.Once);
    }

    [Fact]
    public async Task GetDashboardStatusAsync_WhenNoCredentials_ReturnsRequiresSetup()
    {
        var config = new PluginConfiguration(); // default: empty username/password, anonymous disabled
        var sut = CreateSut(config);

        var result = await sut.GetDashboardStatusAsync(CancellationToken.None);

        Assert.True(result.RequiresSetup);
        Assert.NotEmpty(result.PluginVersion);
        // No backend calls should have been made
        _diagMock.Verify(x => x.DiagnoseAsync(It.IsAny<CancellationToken>()), Times.Never);
        _statusMock.Verify(x => x.GetActivityStatusAsync(It.IsAny<CancellationToken>()), Times.Never);
        _inputMock.Verify(x => x.GetInputStatusAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetDashboardStatusAsync_WhenAnonymousAccessEnabled_DoesNotRequireSetup()
    {
        var config = new PluginConfiguration { AllowAnonymousAccess = true };
        _diagMock.Setup(x => x.DiagnoseAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiagnoseResult { OverallStatus = "OK" });
        _statusMock.Setup(x => x.GetActivityStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync((ActivityStatus?)null);
        _inputMock.Setup(x => x.GetInputStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<InputStatusEntry>());
        _subMock.Setup(x => x.GetActiveSubscriptionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SubscriptionEntry>());
        _statusMock.Setup(x => x.GetConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionEntry>());

        var sut = CreateSut(config);
        var result = await sut.GetDashboardStatusAsync(CancellationToken.None);

        Assert.False(result.RequiresSetup);
        _diagMock.Verify(x => x.DiagnoseAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetDashboardStatusAsync_WhenCredentialsProvided_DoesNotRequireSetup()
    {
        var config = new PluginConfiguration { Username = "admin", Password = "secret" };
        _diagMock.Setup(x => x.DiagnoseAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiagnoseResult { OverallStatus = "OK" });
        _statusMock.Setup(x => x.GetActivityStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync((ActivityStatus?)null);
        _inputMock.Setup(x => x.GetInputStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<InputStatusEntry>());
        _subMock.Setup(x => x.GetActiveSubscriptionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SubscriptionEntry>());
        _statusMock.Setup(x => x.GetConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConnectionEntry>());

        var sut = CreateSut(config);
        var result = await sut.GetDashboardStatusAsync(CancellationToken.None);

        Assert.False(result.RequiresSetup);
        _diagMock.Verify(x => x.DiagnoseAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
