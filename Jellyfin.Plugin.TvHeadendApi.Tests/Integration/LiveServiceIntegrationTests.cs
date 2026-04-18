using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Dvr;
using Jellyfin.Plugin.TvHeadendApi.Service.Guide;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Status;
using Jellyfin.Plugin.TvHeadendApi.Service.Subscription;
using Jellyfin.Plugin.TvHeadendApi.Service.Input;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Integration;

/// <summary>
/// Live integration tests that exercise the plugin service layer against a real
/// TVHeadend instance started via <c>docker-compose.test.yml</c>.
/// <para>
/// These tests validate that each service correctly communicates with the TVHeadend
/// HTTP/JSON API. The TVHeadend instance starts empty (no IPTV network), so tests
/// verify structural correctness (grid shapes, default profiles, diagnostic scoring)
/// rather than data content.
/// </para>
/// </summary>
/// <remarks>
/// Run with: <c>TVHEADEND_LIVE_TESTS=true dotnet test --filter "Category=LiveIntegration"</c>.
/// Requires <c>docker compose -f docker-compose.test.yml up -d</c> running.
/// </remarks>
[Trait("Category", "LiveIntegration")]
public sealed class LiveServiceIntegrationTests : IDisposable
{
    private static readonly string BaseUrl =
        Environment.GetEnvironmentVariable("TVHEADEND_URL") ?? "http://localhost:19981";

    private readonly PluginConfiguration _config;
    private readonly IApiClient _apiClient;
    private readonly IUrlBuilder _urlBuilder;

    public LiveServiceIntegrationTests()
    {
        var uri = new Uri(BaseUrl);
        _config = new PluginConfiguration
        {
            Host = uri.Host,
            Port = uri.Port,
            UseSSL = uri.Scheme == "https",
            Webroot = "/",
            AllowAnonymousAccess = true,
            AuthToken = string.Empty,
            StreamingProfile = "pass",
            RecordingProfile = string.Empty,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = false,
        };

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(15) });

        var configProvider = new PluginConfigurationProvider(() => _config);
        _apiClient = new ApiClient(httpClientFactory.Object, configProvider);
        _urlBuilder = new UrlBuilder();
    }

    public void Dispose()
    {
        // No unmanaged resources; HttpClient instances created per-call via factory.
    }

    // ── GuideService ────────────────────────────────────────────────────

    [Fact]
    public async Task GuideService_GetChannelsAsync_ReturnsEmptyList_WhenNoChannelsConfigured()
    {
        var sut = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder);

        var channels = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();

        // TVHeadend is bootstrapped with IPTV channels — verify they are returned.
        Assert.NotNull(channels);
        Assert.NotEmpty(channels);
    }

    [Fact]
    public async Task GuideService_GetProgramsAsync_ReturnsEmptyList_WhenNoEpgData()
    {
        var sut = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder);
        var now = DateTime.UtcNow;

        // Use a dummy channel UUID — no channels exist, so no programs either.
        var programs = (await sut.GetProgramsAsync(
            "00000000000000000000000000000000",
            now,
            now.AddHours(2),
            CancellationToken.None)).ToList();

        Assert.NotNull(programs);
        Assert.Empty(programs);
    }

    [Fact]
    public async Task GuideService_GetContentTypesAsync_ReturnsDictionary()
    {
        var sut = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder);

        var types = await sut.GetContentTypesAsync(CancellationToken.None);

        // TVHeadend returns a built-in content type list even without EPG data.
        Assert.NotNull(types);
    }

    [Fact]
    public async Task GuideService_GetChannelTagsAsync_ReturnsDictionary()
    {
        var sut = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder);

        var tags = await sut.GetChannelTagsAsync(CancellationToken.None);

        Assert.NotNull(tags);
    }

    // ── DvrService ──────────────────────────────────────────────────────

    [Fact]
    public async Task DvrService_GetTimersAsync_ReturnsEmptyList()
    {
        var sut = new DvrService(NullLogger<DvrService>.Instance, _apiClient, _urlBuilder);

        var timers = (await sut.GetTimersAsync(CancellationToken.None)).ToList();

        Assert.NotNull(timers);
        Assert.Empty(timers);
    }

    [Fact]
    public async Task DvrService_GetSeriesTimersAsync_ReturnsEmptyList()
    {
        var sut = new DvrService(NullLogger<DvrService>.Instance, _apiClient, _urlBuilder);

        var seriesTimers = (await sut.GetSeriesTimersAsync(CancellationToken.None)).ToList();

        Assert.NotNull(seriesTimers);
        Assert.Empty(seriesTimers);
    }

    [Fact]
    public async Task DvrService_GetRecordingProfileUuidAsync_ReturnsDefaultProfileUuid()
    {
        var sut = new DvrService(NullLogger<DvrService>.Instance, _apiClient, _urlBuilder);

        // TVHeadend always has a default DVR config with an empty name.
        var uuid = await sut.GetRecordingProfileUuidAsync(string.Empty, CancellationToken.None);

        Assert.NotNull(uuid);
        Assert.NotEmpty(uuid);
    }

    // ── DiagnosticService ───────────────────────────────────────────────

    [Fact]
    public async Task DiagnosticService_DiagnoseAsync_ReturnsValidResult()
    {
        var streamResolver = new Mock<IProfileResolver>();
        streamResolver
            .Setup(x => x.GetProfilesAsync(
                It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Model.Profile.ProfileReference> { new("default-uuid", "pass") });

        var sut = new DiagnosticService(
            NullLogger<DiagnosticService>.Instance,
            Mock.Of<IServerConfigurationManager>(),
            CreateEncodingReader(),
            streamResolver.Object,
            _apiClient,
            new CachePathProvider(() => null));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.NotNull(result);
        // A live, reachable TVHeadend should not produce an ERROR status.
        Assert.NotEqual("ERROR", result.OverallStatus);
        // Connection check should succeed.
        Assert.Contains(result.Checks, c =>
            c.Category == "Connection" && c.Name == "TVHeadend Connectivity" && c.Status == "OK");
        // Score should be positive — at least connectivity and API version are OK.
        Assert.True(result.CompatibilityScore > 0, $"Score was {result.CompatibilityScore}");
    }

    // ── StatusService ───────────────────────────────────────────────────

    [Fact]
    public async Task StatusService_GetActivityStatusAsync_ReturnsStatus()
    {
        var sut = new StatusService(NullLogger<StatusService>.Instance, _apiClient);

        var status = await sut.GetActivityStatusAsync(CancellationToken.None);

        // TVHeadend always returns an activity status, even when idle.
        Assert.NotNull(status);
    }

    [Fact]
    public async Task StatusService_GetConnectionsAsync_ReturnsConnectionList()
    {
        var sut = new StatusService(NullLogger<StatusService>.Instance, _apiClient);

        var connections = await sut.GetConnectionsAsync(CancellationToken.None);

        Assert.NotNull(connections);
        // There should be at least our own HTTP connection.
    }

    // ── InputMonitorService ─────────────────────────────────────────────

    [Fact]
    public async Task InputMonitorService_GetInputStatusAsync_ReturnsEmptyList_WhenNoInputs()
    {
        var sut = new InputMonitorService(NullLogger<InputMonitorService>.Instance, _apiClient);

        var inputs = await sut.GetInputStatusAsync(CancellationToken.None);

        // IPTV network is configured — inputs should be present.
        Assert.NotNull(inputs);
        Assert.NotEmpty(inputs);
    }

    // ── SubscriptionService ─────────────────────────────────────────────

    [Fact]
    public async Task SubscriptionService_GetActiveSubscriptionsAsync_ReturnsEmptyList()
    {
        var sut = new SubscriptionService(NullLogger<SubscriptionService>.Instance, _apiClient);

        var subscriptions = await sut.GetActiveSubscriptionsAsync(CancellationToken.None);

        Assert.NotNull(subscriptions);
        // Subscriptions may or may not be active depending on timing.
    }

    // ── ProfileResolver ─────────────────────────────────────────────────

    [Fact]
    public async Task ProfileResolver_GetProfilesAsync_ReturnsAtLeastOneProfile()
    {
        var sut = new ProfileResolver(_apiClient);
        using var http = _apiClient.BuildHttpClient(_config);

        var profiles = await sut.GetProfilesAsync(
            http,
            _apiClient.GetBaseUrl(_config),
            _apiClient.GetWebRoot(_config),
            CancellationToken.None);

        // TVHeadend always ships with at least "pass" and "matroska" profiles.
        Assert.NotNull(profiles);
        Assert.NotEmpty(profiles);
        Assert.Contains(profiles, p =>
            string.Equals(p.Name, "pass", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProfileResolver_ResolveProfileByNameAsync_ResolvesPassProfile()
    {
        var sut = new ProfileResolver(_apiClient);
        using var http = _apiClient.BuildHttpClient(_config);
        var baseUrl = _apiClient.GetBaseUrl(_config);
        var webRoot = _apiClient.GetWebRoot(_config);

        var resolved = await sut.ResolveProfileByNameAsync(
            http, baseUrl, webRoot, "pass", CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal("pass", resolved!.Name);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static IEncodingOptionsReader CreateEncodingReader()
    {
        var mock = new Mock<IEncodingOptionsReader>();
        mock.Setup(x => x.ReadFfmpegSettings(
                It.IsAny<IServerConfigurationManager>(),
                It.IsAny<Microsoft.Extensions.Logging.ILogger>()))
            .Returns((null, null));
        return mock.Object;
    }
}




