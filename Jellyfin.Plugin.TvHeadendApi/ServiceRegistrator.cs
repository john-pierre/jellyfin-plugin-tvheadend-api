using System.Net.Http;
using Jellyfin.Plugin.TvHeadendApi.Service;
using Jellyfin.Plugin.TvHeadendApi.Service.Auth;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Dvr;
using Jellyfin.Plugin.TvHeadendApi.Service.Guide;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Statistics;
using Jellyfin.Plugin.TvHeadendApi.Service.Stream;
using MediaBrowser.Controller;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        // Register named HttpClients for TVHeadend API communication.
        // "TvHeadend" — standard client with certificate revocation checks.
        // "TvHeadendUnsafe" — skips certificate validation (self-signed certs).
        serviceCollection.AddHttpClient(ApiClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                CheckCertificateRevocationList = true,
            })
            .AddPolicyHandler(ResiliencePolicies.GetRetryPolicy())
            .AddPolicyHandler(ResiliencePolicies.GetCircuitBreakerPolicy());

        serviceCollection.AddHttpClient(ApiClient.HttpClientUnsafeName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                CheckCertificateRevocationList = false,
                ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
            })
            .AddPolicyHandler(ResiliencePolicies.GetRetryPolicy())
            .AddPolicyHandler(ResiliencePolicies.GetCircuitBreakerPolicy());

        serviceCollection.AddSingleton<IUrlBuilder, UrlBuilder>();
        serviceCollection.AddSingleton<IEncodingOptionsReader, EncodingOptionsReader>();
        serviceCollection.AddSingleton<ITokenService, TokenService>();
        serviceCollection.AddSingleton<IProfileResolver, ProfileResolver>();
        serviceCollection.AddSingleton<IDiagnosticService, DiagnosticService>();
        serviceCollection.AddSingleton<IGuideService, GuideService>();
        serviceCollection.AddSingleton<IDvrService, DvrService>();
        serviceCollection.AddSingleton<IMediaSourceService, MediaSourceService>();
        serviceCollection.AddSingleton<ILifecycleService, LifecycleService>();
        serviceCollection.AddSingleton<IDefaultProfileService, DefaultProfileService>();
        serviceCollection.AddSingleton<IProfileContainerResolver, ProfileContainerResolver>();
        serviceCollection.AddSingleton<IApiClient, ApiClient>();
        serviceCollection.AddSingleton<StatisticsService>();
        serviceCollection.AddSingleton<IStatisticsService>(sp => sp.GetRequiredService<StatisticsService>());
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<StatisticsService>());

        // Register OrchestratorService as the implementation of ILiveTvService
        serviceCollection.AddSingleton<ILiveTvService, OrchestratorService>();
    }
}
