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
using Jellyfin.Plugin.TvHeadendApi.Service.Metrics;
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
        // Ensure the HTTP request context is resolvable (idempotent — Jellyfin registers it too).
        // Used to derive the requesting client identity for streaming-profile rules and to build
        // client-reachable relay URLs from the incoming request.
        serviceCollection.AddHttpContextAccessor();

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
        // Note: Initialize() is NOT called here because Plugin.Instance (and DataFolderPath)
        // is not yet available during service registration. Initialization is deferred until
        // the first service that needs the database resolves DatabaseHealthService.
        serviceCollection.AddSingleton(sp =>
        {
            var svc = new DatabaseHealthService(
                sp.GetRequiredService<DatabaseProvider>(),
                sp.GetRequiredService<DatabaseConnectionFactory>(),
                sp.GetRequiredService<DatabaseMigrationService>(),
                sp.GetRequiredService<DatabaseRecoveryService>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DatabaseHealthService>>());
            return svc;
        });

        // DatabaseCleanupService — central retention cleanup for all tables.
        serviceCollection.AddSingleton(sp => new DatabaseCleanupService(
            sp.GetRequiredService<DatabaseHealthService>(),
            sp.GetRequiredService<DatabaseConnectionFactory>(),
            sp.GetRequiredService<DatabaseWriteCoordinator>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DatabaseCleanupService>>()));

        // DatabaseCleanupHostedService — periodic background cleanup.
        serviceCollection.AddSingleton<DatabaseCleanupHostedService>(sp => new DatabaseCleanupHostedService(
            sp.GetRequiredService<DatabaseCleanupService>(),
            sp.GetRequiredService<ConfigurationProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DatabaseCleanupHostedService>>()));
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<DatabaseCleanupHostedService>());

        // ── PluginLogService — unified log persistence + query ──
        serviceCollection.AddSingleton<PluginLogService>(sp =>
            new PluginLogService(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PluginLogService>>(),
                sp.GetRequiredService<ConfigurationProvider>(),
                sp.GetRequiredService<DatabaseHealthService>(),
                sp.GetRequiredService<DatabaseProvider>()));
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<PluginLogService>());
        serviceCollection.AddSingleton<IPluginLogQueryService>(sp => sp.GetRequiredService<PluginLogService>());

        // ── PluginLogPersistenceProvider — auto-captures plugin log entries to SQLite,
        //    applying the configured plugin-specific log-level override ──
        serviceCollection.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(sp =>
            new PluginLogPersistenceProvider(
                sp.GetRequiredService<PluginLogService>(),
                sp.GetRequiredService<ConfigurationProvider>()));

        serviceCollection.AddSingleton<StatisticsService>(sp =>
            new StatisticsService(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<StatisticsService>>(),
                sp.GetRequiredService<MediaBrowser.Controller.Session.ISessionManager>(),
                sp.GetRequiredService<ConfigurationProvider>(),
                sp.GetRequiredService<DatabaseHealthService>(),
                sp.GetRequiredService<DatabaseWriteCoordinator>(),
                sp.GetRequiredService<DatabaseProvider>()));
        serviceCollection.AddSingleton<IStatisticsService>(sp => sp.GetRequiredService<StatisticsService>());

        serviceCollection.AddSingleton<IUrlBuilder, UrlBuilder>();
        serviceCollection.AddSingleton<IEncodingOptionsReader, EncodingOptionsReader>();
        serviceCollection.AddSingleton<ITokenService, TokenService>();
        serviceCollection.AddSingleton<IProfileResolver, ProfileResolver>();
        serviceCollection.AddSingleton<IDiagnosticService, DiagnosticService>();
        serviceCollection.AddSingleton<IGuideService, GuideService>();
        serviceCollection.AddSingleton<IDvrService, DvrService>();
        serviceCollection.AddSingleton<IMediaInfoCacheService, MediaInfoCacheService>();
        serviceCollection.AddSingleton<IMediaSourceService, MediaSourceService>();
        serviceCollection.AddSingleton<ILifecycleService, LifecycleService>();
        serviceCollection.AddSingleton<IDefaultProfileService, DefaultProfileService>();
        serviceCollection.AddSingleton<IProfileContainerResolver, ProfileContainerResolver>();
        serviceCollection.AddSingleton<IStreamingProfileResolver, StreamingProfileResolver>();
        serviceCollection.AddSingleton<IPlaybackContextAccessor, PlaybackContextAccessor>();
        serviceCollection.AddSingleton<IProfileDiscoveryService, ProfileDiscoveryService>();
        serviceCollection.AddSingleton<IApiClient, ApiClient>();
        serviceCollection.AddSingleton<IHealthService>(sp =>
            new HealthService(
                sp.GetRequiredService<IApiClient>(),
                sp.GetRequiredService<IUrlBuilder>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<HealthService>>(),
                sp.GetRequiredService<ConfigurationProvider>(),
                sp.GetRequiredService<DatabaseProvider>(),
                sp.GetRequiredService<DatabaseWriteCoordinator>()));
        serviceCollection.AddSingleton<IStatusService, StatusService>();
        serviceCollection.AddSingleton<IInputMonitorService, InputMonitorService>();
        serviceCollection.AddSingleton<ISubscriptionService, SubscriptionService>();
        serviceCollection.AddSingleton<IDashboardService, DashboardService>();
        serviceCollection.AddSingleton<RelayImageCache>();
        serviceCollection.AddSingleton<RelayImageCacheCleanupService>();
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<RelayImageCacheCleanupService>());
        serviceCollection.AddSingleton<IRelayService, RelayService>();
        serviceCollection.AddSingleton<IRelayUrlBuilder>(sp =>
            new RelayUrlBuilder(
                sp.GetRequiredService<IServerApplicationHost>(),
                sp.GetRequiredService<ConfigurationProvider>(),
                sp.GetRequiredService<IRelayTokenService>(),
                sp.GetRequiredService<RelayTokenOptions>(),
                sp.GetService<Microsoft.AspNetCore.Http.IHttpContextAccessor>()));

        // Relay metrics — shared activity tracker, EF Core context, and hosted service.
        serviceCollection.AddSingleton<RelayActivityTracker>();
        serviceCollection.AddSingleton<RelayMetricsService>(sp =>
            new RelayMetricsService(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RelayMetricsService>>(),
                sp.GetRequiredService<ConfigurationProvider>(),
                sp.GetRequiredService<DatabaseHealthService>(),
                sp.GetRequiredService<DatabaseWriteCoordinator>(),
                sp.GetRequiredService<RelayActivityTracker>(),
                sp.GetRequiredService<DatabaseProvider>()));
        serviceCollection.AddSingleton<IRelayMetricsService>(sp => sp.GetRequiredService<RelayMetricsService>());
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<RelayMetricsService>());

        // ── Streaming Telemetry — real-time session tracking and dashboard ──
        serviceCollection.AddSingleton<ActiveSessionStore>();
        serviceCollection.AddSingleton<MetricsWriter>(sp =>
            new MetricsWriter(
                sp.GetRequiredService<DatabaseHealthService>(),
                sp.GetRequiredService<DatabaseConnectionFactory>(),
                sp.GetRequiredService<DatabaseWriteCoordinator>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MetricsWriter>>()));
        serviceCollection.AddSingleton<SessionTracker>(sp =>
            new SessionTracker(
                sp.GetRequiredService<ActiveSessionStore>(),
                sp.GetRequiredService<MetricsWriter>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SessionTracker>>()));
        serviceCollection.AddSingleton<MetricsAggregator>(sp =>
            new MetricsAggregator(
                sp.GetRequiredService<DatabaseHealthService>(),
                sp.GetRequiredService<DatabaseConnectionFactory>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MetricsAggregator>>()));
        serviceCollection.AddSingleton<IStreamingDashboardService>(sp =>
            new StreamingDashboardService(
                sp.GetRequiredService<ActiveSessionStore>(),
                sp.GetRequiredService<MetricsAggregator>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<StreamingDashboardService>>()));

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
            new RelayTokenRepository(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RelayTokenRepository>>(),
                sp.GetRequiredService<DatabaseHealthService>(),
                sp.GetRequiredService<DatabaseWriteCoordinator>(),
                sp.GetRequiredService<DatabaseProvider>()));
        serviceCollection.AddSingleton<IRelayTokenRepository>(sp => sp.GetRequiredService<RelayTokenRepository>());

        // Relay token security — service, validator, cleanup.
        serviceCollection.AddSingleton<IRelayTokenService, RelayTokenService>();
        serviceCollection.AddSingleton<IRelayTokenValidator, RelayTokenValidatorService>();
        serviceCollection.AddSingleton<RelayTokenCleanupService>();
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<RelayTokenCleanupService>());

        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<StatisticsService>());
        serviceCollection.AddSingleton<CometService>(sp =>
            new CometService(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CometService>>(),
                sp.GetRequiredService<IApiClient>(),
                sp.GetRequiredService<IUrlBuilder>(),
                sp.GetRequiredService<ConfigurationProvider>(),
                sp.GetRequiredService<DatabaseProvider>(),
                sp.GetRequiredService<PluginLogService>()));
        serviceCollection.AddSingleton<ICometSnapshotReader>(sp => sp.GetRequiredService<CometService>());
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<CometService>());

        // Register OrchestratorService as the implementation of ILiveTvService
        serviceCollection.AddSingleton<ILiveTvService, OrchestratorService>();
    }
}
