using Jellyfin.Plugin.TvHeadendApi.Service;
using Jellyfin.Plugin.TvHeadendApi.Service.Auth;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Dvr;
using Jellyfin.Plugin.TvHeadendApi.Service.Guide;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Stream;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using MediaBrowser.Controller;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.DependencyInjection;
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
    }
}
