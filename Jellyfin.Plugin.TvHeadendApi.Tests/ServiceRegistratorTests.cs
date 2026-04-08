using Jellyfin.Plugin.TvHeadendApi.Service;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostics;
using Jellyfin.Plugin.TvHeadendApi.Service.Dvr;
using Jellyfin.Plugin.TvHeadendApi.Service.Guide;
using Jellyfin.Plugin.TvHeadendApi.Service.Profiles;
using Jellyfin.Plugin.TvHeadendApi.Service.Streaming;
using Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;
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

        Assert.Contains(services, d => d.ServiceType == typeof(ITvheadendUrlBuilder) && d.ImplementationType == typeof(TvheadendUrlBuilder));
        Assert.Contains(services, d => d.ServiceType == typeof(ILiveTvGuideService) && d.ImplementationType == typeof(LiveTvGuideService));
        Assert.Contains(services, d => d.ServiceType == typeof(ITvheadendDvrService) && d.ImplementationType == typeof(TvheadendDvrService));
        Assert.Contains(services, d => d.ServiceType == typeof(ILiveStreamSourceService) && d.ImplementationType == typeof(LiveStreamSourceService));
        Assert.Contains(services, d => d.ServiceType == typeof(ILiveStreamLifecycleService) && d.ImplementationType == typeof(LiveStreamLifecycleService));
        Assert.Contains(services, d => d.ServiceType == typeof(IProfileProvisioningService) && d.ImplementationType == typeof(ProfileProvisioningService));
        Assert.Contains(services, d => d.ServiceType == typeof(ILiveTvService) && d.ImplementationType == typeof(LiveTvService));
        Assert.Contains(services, d => d.ServiceType == typeof(IDiagnoseService) && d.ImplementationType == typeof(DiagnoseService));
    }
}
