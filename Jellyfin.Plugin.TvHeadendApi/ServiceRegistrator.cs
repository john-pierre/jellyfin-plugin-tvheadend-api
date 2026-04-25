using System.Net.Http;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service;
using Jellyfin.Plugin.TvHeadendApi.Service.Auth;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Comet;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Dashboard;
using Jellyfin.Plugin.TvHeadendApi.Service.Database;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Dvr;
using Jellyfin.Plugin.TvHeadendApi.Service.Guide;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Input;
using Jellyfin.Plugin.TvHeadendApi.Service.Logging;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Jellyfin.Plugin.TvHeadendApi.Service.Statistic;
using Jellyfin.Plugin.TvHeadendApi.Service.Status;
using Jellyfin.Plugin.TvHeadendApi.Service.Storage;
using Jellyfin.Plugin.TvHeadendApi.Service.Stream;
using Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;
using Jellyfin.Plugin.TvHeadendApi.Service.Subscription;
using MediaBrowser.Controller;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.TvHeadendApi;

/// <summary>
/// Registers the plugin's infrastructure, domain services, and hosted background services.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>
    /// Registers all services required by the plugin in the application's dependency injection container.
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
            .AddHttpMessageHandler(sp => new ResilienceHandler
            {
                HealthService = sp.GetService<IHealthService>(),
            });

        serviceCollection.AddHttpClient(ApiClient.HttpClientUnsafeName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                CheckCertificateRevocationList = false,
                ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
            })
            .AddHttpMessageHandler(sp => new ResilienceHandler
            {
                HealthService = sp.GetService<IHealthService>(),
            });

        // Plugin path/config providers — decouple services from Plugin.Instance singleton.
        serviceCollection.AddSingleton(new ConfigurationProvider(() => Plugin.Instance?.Configuration as Configuration.PluginConfiguration));
        serviceCollection.AddSingleton(new CachePathProvider(() => Plugin.Instance?.CachePath));
        serviceCollection.AddSingleton(new DataFolderPathProvider(() => Plugin.Instance?.DataFolderPath));
        serviceCollection.AddSingleton(new ConfigurationSaver(mutate =>
        {
            var plugin = Plugin.Instance;
            if (plugin?.Configuration is Configuration.PluginConfiguration cfg)
            {
                mutate(cfg);
                plugin.SaveConfiguration();
            }
        }));

        // ── Central Database Infrastructure ──
        // DatabaseProvider owns the file path and connection string.
        serviceCollection.AddSingleton(sp => new DatabaseProvider(sp.GetRequiredService<DataFolderPathProvider>()));

        // DatabaseConnectionFactory creates connections with consistent PRAGMA settings.
        serviceCollection.AddSingleton(sp => new DatabaseConnectionFactory(sp.GetRequiredService<DatabaseProvider>()));

        // DatabaseWriteCoordinator serializes write operations across services.
        serviceCollection.AddSingleton<DatabaseWriteCoordinator>();

        // DatabaseMigrationService owns all schema creation and versioning.
        serviceCollection.AddSingleton(sp => new DatabaseMigrationService(
            sp.GetRequiredService<DatabaseConnectionFactory>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DatabaseMigrationService>>()));

        // DatabaseRecoveryService handles corruption detection and recovery.
        serviceCollection.AddSingleton(sp => new DatabaseRecoveryService(
            sp.GetRequiredService<DatabaseProvider>(),
            sp.GetRequiredService<DatabaseMigrationService>(),
            sp.GetRequiredService<DatabaseConnectionFactory>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DatabaseRecoveryService>>()));

        // DatabaseHealthService — central health monitoring, initialization, and migration.
        serviceCollection.AddSingleton(sp =>
        {
            var svc = new DatabaseHealthService(
                sp.GetRequiredService<DatabaseProvider>(),
                sp.GetRequiredService<DatabaseConnectionFactory>(),
                sp.GetRequiredService<DatabaseMigrationService>(),
                sp.GetRequiredService<DatabaseRecoveryService>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DatabaseHealthService>>());

            // Initialize eagerly so migrations run before any service touches the DB.
            svc.Initialize();
            return svc;
        });

        // ── PluginLogService — unified log persistence + query ──
        serviceCollection.AddSingleton<PluginLogService>(sp =>
        {
            var db = sp.GetRequiredService<DatabaseProvider>();
            return new PluginLogService(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PluginLogService>>(),
                sp.GetRequiredService<ConfigurationProvider>(),
                db.CreateContextOptions<Service.Statistic.ViewingSessionContext>());
        });
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<PluginLogService>());
        serviceCollection.AddSingleton<IPluginLogQueryService>(sp => sp.GetRequiredService<PluginLogService>());

        // ── PluginLoggerFactory — plugin-specific log level override ──
        serviceCollection.AddSingleton<IPluginLoggerFactory>(sp =>
            new PluginLoggerFactory(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(),
                sp.GetRequiredService<ConfigurationProvider>(),
                sp.GetRequiredService<PluginLogService>()));

        serviceCollection.AddSingleton<StatisticsService>(sp =>
        {
            var db = sp.GetRequiredService<DatabaseProvider>();
            return new StatisticsService(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<StatisticsService>>(),
                sp.GetRequiredService<MediaBrowser.Controller.Session.ISessionManager>(),
                sp.GetRequiredService<ConfigurationProvider>(),
                sp.GetRequiredService<DatabaseHealthService>(),
                db.CreateContextOptions<ViewingSessionContext>(),
                db.DatabasePath);
        });
        serviceCollection.AddSingleton<IStatisticsService>(sp => sp.GetRequiredService<StatisticsService>());

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
        serviceCollection.AddSingleton<IStreamingProfileResolver, StreamingProfileResolver>();
        serviceCollection.AddSingleton<IProfileDiscoveryService, ProfileDiscoveryService>();
        serviceCollection.AddSingleton<IApiClient, ApiClient>();
        serviceCollection.AddSingleton<IHealthService>(sp =>
        {
            var db = sp.GetRequiredService<DatabaseProvider>();
            return new HealthService(
                sp.GetRequiredService<IApiClient>(),
                sp.GetRequiredService<IUrlBuilder>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<HealthService>>(),
                sp.GetRequiredService<ConfigurationProvider>(),
                db.CreateContextOptions<Service.Statistic.ViewingSessionContext>());
        });
        serviceCollection.AddSingleton<IStatusService, StatusService>();
        serviceCollection.AddSingleton<IInputMonitorService, InputMonitorService>();
        serviceCollection.AddSingleton<ISubscriptionService, SubscriptionService>();
        serviceCollection.AddSingleton<IDashboardService, DashboardService>();
        serviceCollection.AddSingleton<IRelayService, RelayService>();
        serviceCollection.AddSingleton<IRelayUrlBuilder>(sp =>
            new RelayUrlBuilder(
                sp.GetRequiredService<IServerApplicationHost>(),
                sp.GetRequiredService<ConfigurationProvider>(),
                sp.GetRequiredService<IRelayTokenService>(),
                sp.GetRequiredService<RelayTokenOptions>()));

        // Relay metrics — shared activity tracker, EF Core context, and hosted service.
        serviceCollection.AddSingleton<RelayActivityTracker>();
        serviceCollection.AddSingleton<RelayMetricsService>(sp =>
        {
            var db = sp.GetRequiredService<DatabaseProvider>();
            return new RelayMetricsService(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RelayMetricsService>>(),
                sp.GetRequiredService<ConfigurationProvider>(),
                sp.GetRequiredService<DatabaseHealthService>(),
                sp.GetRequiredService<RelayActivityTracker>(),
                db.CreateContextOptions<RelayMetricsContext>(),
                db.DatabasePath);
        });
        serviceCollection.AddSingleton<IRelayMetricsService>(sp => sp.GetRequiredService<RelayMetricsService>());
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<RelayMetricsService>());

        // Relay token security — hasher, options, repository.
        serviceCollection.AddSingleton<RelayTokenOptions>();
        serviceCollection.AddSingleton<RelayTokenHasher>(sp =>
        {
            var pathProvider = sp.GetRequiredService<DataFolderPathProvider>();
            var folder = pathProvider.Path ?? "jellyfin-tvheadend-default";
            var secretBytes = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("relay-token-pepper:" + folder));
            return new RelayTokenHasher(secretBytes);
        });
        serviceCollection.AddSingleton<RelayTokenRepository>(sp =>
        {
            var db = sp.GetRequiredService<DatabaseProvider>();
            return new RelayTokenRepository(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RelayTokenRepository>>(),
                db.CreateContextOptions<RelayTokenDbContext>(),
                db.DatabasePath);
        });
        serviceCollection.AddSingleton<IRelayTokenRepository>(sp => sp.GetRequiredService<RelayTokenRepository>());

        // Relay token security — service, validator, cleanup.
        serviceCollection.AddSingleton<IRelayTokenService, RelayTokenService>();
        serviceCollection.AddSingleton<IRelayTokenValidator, RelayTokenValidatorService>();
        serviceCollection.AddSingleton<RelayTokenCleanupService>();
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<RelayTokenCleanupService>());

        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<StatisticsService>());
        serviceCollection.AddSingleton<CometService>(sp =>
        {
            var db = sp.GetRequiredService<DatabaseProvider>();
            return new CometService(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CometService>>(),
                sp.GetRequiredService<IApiClient>(),
                sp.GetRequiredService<IUrlBuilder>(),
                sp.GetRequiredService<ConfigurationProvider>(),
                db.CreateContextOptions<Service.Statistic.ViewingSessionContext>(),
                sp.GetRequiredService<PluginLogService>());
        });
        serviceCollection.AddSingleton<ICometSnapshotReader>(sp => sp.GetRequiredService<CometService>());
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<CometService>());

        // Register OrchestratorService as the implementation of ILiveTvService
        serviceCollection.AddSingleton<ILiveTvService, OrchestratorService>();
    }
}
