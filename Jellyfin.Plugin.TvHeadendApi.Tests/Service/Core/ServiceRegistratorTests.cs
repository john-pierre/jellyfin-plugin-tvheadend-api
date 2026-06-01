using Jellyfin.Plugin.TvHeadendApi.Service;
using Jellyfin.Plugin.TvHeadendApi.Service.Auth;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Comet;
using Jellyfin.Plugin.TvHeadendApi.Service.Dashboard;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Dvr;
using Jellyfin.Plugin.TvHeadendApi.Service.Guide;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Input;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Jellyfin.Plugin.TvHeadendApi.Service.Statistic;
using Jellyfin.Plugin.TvHeadendApi.Service.Status;
using Jellyfin.Plugin.TvHeadendApi.Service.Stream;
using Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;
using Jellyfin.Plugin.TvHeadendApi.Service.Subscription;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class ServiceRegistratorTests
{
    [Fact]
    public void RegisterServices_RegistersExpectedServiceDescriptors()
    {
        var services = new ServiceCollection();
        var host = new Mock<IServerApplicationHost>();
        var sut = new ServiceRegistrator();

        sut.RegisterServices(services, host.Object);

        Assert.Contains(services, d => d.ServiceType == typeof(IUrlBuilder) && d.ImplementationType == typeof(UrlBuilder));
        Assert.Contains(services, d => d.ServiceType == typeof(IDvrService) && d.ImplementationType == typeof(DvrService));
        Assert.Contains(services, d => d.ServiceType == typeof(IMediaSourceService) && d.ImplementationType == typeof(MediaSourceService));
        Assert.Contains(services, d => d.ServiceType == typeof(ILifecycleService) && d.ImplementationType == typeof(LifecycleService));
        Assert.Contains(services, d => d.ServiceType == typeof(IDefaultProfileService) && d.ImplementationType == typeof(DefaultProfileService));
        Assert.Contains(services, d => d.ServiceType == typeof(ITokenService) && d.ImplementationType == typeof(TokenService));
        Assert.Contains(services, d => d.ServiceType == typeof(ILiveTvService) && d.ImplementationType == typeof(OrchestratorService));
        Assert.Contains(services, d => d.ServiceType == typeof(IDiagnosticService) && d.ImplementationType == typeof(DiagnosticService));
        Assert.Contains(services, d => d.ServiceType == typeof(IDashboardService) && d.ImplementationType == typeof(DashboardService));
        Assert.Contains(services, d => d.ServiceType == typeof(ICometSnapshotReader));
    }

    [Fact]
    public void RegisterServices_AllServicesResolvable_WithMockedFrameworkDependencies()
    {
        var services = new ServiceCollection();
        var host = new Mock<IServerApplicationHost>();
        var sut = new ServiceRegistrator();

        sut.RegisterServices(services, host.Object);

        // Register framework dependencies that Jellyfin normally provides
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Mock.Of<IServerConfigurationManager>());
        services.AddSingleton(Mock.Of<ILibraryManager>());
        services.AddSingleton(Mock.Of<ISessionManager>());
        services.AddSingleton(Mock.Of<IMediaEncoder>());
        services.AddSingleton(Mock.Of<IApplicationPaths>());
        services.AddSingleton(Mock.Of<IAuthorizationContext>());
        services.AddSingleton(host.Object);

        using var provider = services.BuildServiceProvider();

        // Verify all plugin-registered interfaces can resolve
        Assert.NotNull(provider.GetRequiredService<IUrlBuilder>());
        Assert.NotNull(provider.GetRequiredService<IApiClient>());
        Assert.NotNull(provider.GetRequiredService<ITokenService>());
        Assert.NotNull(provider.GetRequiredService<IProfileResolver>());
        Assert.NotNull(provider.GetRequiredService<IDiagnosticService>());
        Assert.NotNull(provider.GetRequiredService<IGuideService>());
        Assert.NotNull(provider.GetRequiredService<IDvrService>());
        Assert.NotNull(provider.GetRequiredService<IMediaSourceService>());
        Assert.NotNull(provider.GetRequiredService<ILifecycleService>());
        Assert.NotNull(provider.GetRequiredService<IDefaultProfileService>());
        Assert.NotNull(provider.GetRequiredService<IProfileContainerResolver>());
        Assert.NotNull(provider.GetRequiredService<IPlaybackContextAccessor>());
        Assert.NotNull(provider.GetRequiredService<IStatusService>());
        Assert.NotNull(provider.GetRequiredService<IInputMonitorService>());
        Assert.NotNull(provider.GetRequiredService<ISubscriptionService>());
        Assert.NotNull(provider.GetRequiredService<IStatisticsService>());
        Assert.NotNull(provider.GetRequiredService<IDashboardService>());
        Assert.NotNull(provider.GetRequiredService<ICometSnapshotReader>());
        Assert.NotNull(provider.GetRequiredService<ILiveTvService>());
        Assert.NotNull(provider.GetRequiredService<IEncodingOptionsReader>());
        Assert.NotNull(provider.GetRequiredService<IRelayMetricsService>());
        Assert.NotNull(provider.GetRequiredService<RelayActivityTracker>());

        // StatisticsService is also registered as IHostedService
        var hostedServices = provider.GetServices<IHostedService>();
        Assert.Contains(hostedServices, s => s is StatisticsService);
        Assert.Contains(hostedServices, s => s is CometService);
        Assert.Contains(hostedServices, s => s is RelayMetricsService);
    }
}
