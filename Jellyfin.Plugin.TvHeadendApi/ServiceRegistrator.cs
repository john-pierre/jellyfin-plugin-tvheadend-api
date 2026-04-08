using Jellyfin.Plugin.TvHeadendApi.Service;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostics;
using Jellyfin.Plugin.TvHeadendApi.Service.Dvr;
using Jellyfin.Plugin.TvHeadendApi.Service.Guide;
using Jellyfin.Plugin.TvHeadendApi.Service.Profiles;
using Jellyfin.Plugin.TvHeadendApi.Service.Streaming;
using Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;
using MediaBrowser.Controller;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.TvHeadendApi;

/// <summary>
/// Responsible for registering all necessary services required by the TVHeadEnd plugin.
/// This class ensures that the plugin integrates seamlessly with the Jellyfin service architecture
/// by utilizing dependency injection and hosted services.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>
    /// Registers all services required by the plugin in the application's dependency injection container.
    /// This method is called automatically during plugin initialization to configure services and dependencies.
    /// </summary>
    /// <param name="serviceCollection">
    /// The <see cref="IServiceCollection"/> instance where services should be registered.
    /// This collection is used to manage the lifecycle of services within the Jellyfin server.
    /// </param>
    /// <param name="applicationHost">
    /// The <see cref="IServerApplicationHost"/> instance that provides access to the server's application host.
    /// This can be used to resolve additional services or interact with the server lifecycle.
    /// </param>
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ITvheadendUrlBuilder, TvheadendUrlBuilder>();
        serviceCollection.AddSingleton<ITvheadendJsonReader, TvheadendJsonReader>();
        serviceCollection.AddSingleton<IJellyfinEncodingOptionsReader, JellyfinEncodingOptionsReader>();
        serviceCollection.AddSingleton<ITvheadendIdNodeService, TvheadendIdNodeService>();
        serviceCollection.AddSingleton<ITvheadendStreamProfileResolver, TvheadendStreamProfileResolver>();
        serviceCollection.AddSingleton<IDiagnoseService, DiagnoseService>();
        serviceCollection.AddSingleton<ILiveTvGuideService, LiveTvGuideService>();
        serviceCollection.AddSingleton<ITvheadendDvrService, TvheadendDvrService>();
        serviceCollection.AddSingleton<ILiveStreamSourceService, LiveStreamSourceService>();
        serviceCollection.AddSingleton<ILiveStreamLifecycleService, LiveStreamLifecycleService>();
        serviceCollection.AddSingleton<IProfileProvisioningService, ProfileProvisioningService>();
        serviceCollection.AddSingleton<ILiveStreamProfileContainerResolver, LiveStreamProfileContainerResolver>();
        serviceCollection.AddSingleton<ITvheadendApiClient, TvheadendApiClient>();

        // Register LiveTvService as the implementation of ILiveTvService
        serviceCollection.AddSingleton<ILiveTvService, LiveTvService>();
    }
}
