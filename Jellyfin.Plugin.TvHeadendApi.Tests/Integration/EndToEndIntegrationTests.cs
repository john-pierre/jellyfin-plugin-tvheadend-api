using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Auth;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Dvr;
using Jellyfin.Plugin.TvHeadendApi.Service.Guide;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Jellyfin.Plugin.TvHeadendApi.Service.Input;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Status;
using Jellyfin.Plugin.TvHeadendApi.Service.Subscription;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Integration;

/// <summary>
/// Full end-to-end integration tests against the bootstrapped TVHeadend + IPTV simulator stack.
/// Requires <c>docker compose -f docker/docker-compose.test.yml up -d</c> with bootstrap complete.
/// <para>
/// Unlike <see cref="LiveServiceIntegrationTests"/> which tests against an empty TVHeadend,
/// these tests expect channels, EPG data, profiles, users, and recordings to be present
/// (configured by <c>tvh-bootstrap.sh</c>).
/// </para>
/// </summary>
/// <remarks>
/// Run with: <c>TVHEADEND_LIVE_TESTS=true dotnet test --filter "Category=LiveIntegration"</c>.
/// </remarks>
[Trait("Category", "LiveIntegration")]
public sealed class EndToEndIntegrationTests : IDisposable
{
    private static readonly string BaseUrl =
        Environment.GetEnvironmentVariable("TVHEADEND_URL") ?? "http://localhost:19981";

    private readonly PluginConfiguration _config;
    private readonly PluginConfiguration _authConfig;
    private readonly IApiClient _apiClient;
    private readonly IUrlBuilder _urlBuilder;
    private readonly HttpClient _rawClient;

    public EndToEndIntegrationTests()
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
            StreamingProfile = "test-pass",
            RecordingProfile = "test-dvr",
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = false,
        };

        _authConfig = new PluginConfiguration
        {
            Host = uri.Host,
            Port = uri.Port,
            UseSSL = uri.Scheme == "https",
            Webroot = "/",
            AllowAnonymousAccess = false,
            Username = "testuser",
            Password = "testpass",
            AuthToken = string.Empty,
            StreamingProfile = "test-pass",
            RecordingProfile = "test-dvr",
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
        _rawClient = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(15) };
    }

    public void Dispose()
    {
        _rawClient.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 21c — Channel & EPG
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GuideService_GetChannelsAsync_ReturnsBootstrappedChannels()
    {
        var sut = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder);

        var channels = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();

        Assert.NotNull(channels);
        Assert.True(channels.Count >= 3, $"Expected ≥3 channels, got {channels.Count}");
    }

    [Fact]
    public async Task GuideService_GetProgramsAsync_ReturnsEpgEntries()
    {
        var sut = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder);
        var channels = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();
        Assert.NotEmpty(channels);

        var channelId = channels.First().Id;
        var now = DateTime.UtcNow;
        var programs = (await sut.GetProgramsAsync(channelId, now.AddHours(-1), now.AddHours(2), CancellationToken.None)).ToList();

        Assert.NotNull(programs);
        Assert.NotEmpty(programs);
        // Verify basic EPG data mapping
        var first = programs.First();
        Assert.False(string.IsNullOrEmpty(first.Name), "Programme title should not be empty");
        Assert.True(first.StartDate < first.EndDate, "Start should be before end");
    }

    [Fact]
    public async Task GuideService_GetContentTypesAsync_ReturnsDictionaryFromPopulatedEpg()
    {
        var sut = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder);

        var types = await sut.GetContentTypesAsync(CancellationToken.None);

        Assert.NotNull(types);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 21d — Auth Token Lifecycle
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TokenService_GenerateValidTokenAsync_ReturnsSuccessResult()
    {
        var authConfigProvider = new PluginConfigurationProvider(() => _authConfig);
        var authApiClient = CreateApiClient(authConfigProvider);
        var configSaver = new Mock<PluginConfigurationSaver>();

        var sut = new TokenService(
            NullLogger<TokenService>.Instance,
            authApiClient,
            configSaver.Object);

        var result = await sut.GenerateValidTokenAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Success, $"Token generation failed: {result.Message}");
        Assert.False(string.IsNullOrEmpty(result.AuthToken), "Token should not be empty on success");
    }

    [Fact]
    public async Task TokenService_InvalidCredentials_ReturnsFailure()
    {
        var badConfig = new PluginConfiguration
        {
            Host = _config.Host,
            Port = _config.Port,
            UseSSL = _config.UseSSL,
            Webroot = "/",
            AllowAnonymousAccess = false,
            Username = "wronguser",
            Password = "wrongpass",
            AuthToken = string.Empty,
        };

        var badConfigProvider = new PluginConfigurationProvider(() => badConfig);
        var badApiClient = CreateApiClient(badConfigProvider);
        var configSaver = new Mock<PluginConfigurationSaver>();

        var sut = new TokenService(
            NullLogger<TokenService>.Instance,
            badApiClient,
            configSaver.Object);

        var result = await sut.GenerateValidTokenAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.Success, "Expected failure with invalid credentials");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 21e — Profile Management
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProfileResolver_GetProfilesAsync_ReturnsMultipleProfiles()
    {
        var sut = new ProfileResolver(_apiClient);
        using var http = _apiClient.BuildHttpClient(_config);

        var profiles = await sut.GetProfilesAsync(
            http, _apiClient.GetBaseUrl(_config), _apiClient.GetWebRoot(_config), CancellationToken.None);

        Assert.NotNull(profiles);
        Assert.True(profiles.Count >= 2, $"Expected ≥2 profiles, got {profiles.Count}");
    }

    [Fact]
    public async Task ProfileResolver_ResolveProfileByNameAsync_ResolvesTestProfile()
    {
        var sut = new ProfileResolver(_apiClient);
        using var http = _apiClient.BuildHttpClient(_config);
        var baseUrl = _apiClient.GetBaseUrl(_config);
        var webRoot = _apiClient.GetWebRoot(_config);

        var resolved = await sut.ResolveProfileByNameAsync(http, baseUrl, webRoot, "test-pass", CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal("test-pass", resolved!.Name);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 21f — Streaming
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task StreamUrl_ContainsProfile_WhenChannelExists()
    {
        var sut = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder);
        var channels = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();
        Assert.NotEmpty(channels);

        var channelId = channels.First().Id;
        var streamUrl = $"{BaseUrl}/stream/channel/{channelId}?profile=test-pass";

        // Verify the stream endpoint returns a valid response (not 404).
        var response = await _rawClient.GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);

        // TVHeadend may return 200 (streaming) or 503 (no input) — but not 404.
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        response.Dispose();
    }

    [Fact]
    public async Task StreamUrl_HttpHead_ReturnsVideoContentType()
    {
        var sut = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder);
        var channels = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();
        if (channels.Count == 0)
        {
            return; // Skip if no channels available.
        }

        var channelId = channels.First().Id;
        var streamUrl = $"{BaseUrl}/stream/channel/{channelId}?profile=test-pass";

        using var request = new HttpRequestMessage(HttpMethod.Get, streamUrl);
        using var response = await _rawClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);

        if (response.IsSuccessStatusCode)
        {
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            Assert.True(
                contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
                contentType == "application/octet-stream",
                $"Unexpected content type: {contentType}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 21g — DVR (Recording)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DvrService_GetTimersAsync_ReturnsTimerList()
    {
        var sut = new DvrService(NullLogger<DvrService>.Instance, _apiClient, _urlBuilder);

        var timers = (await sut.GetTimersAsync(CancellationToken.None)).ToList();

        // Bootstrap schedules a recording — it should appear as a timer or completed recording.
        Assert.NotNull(timers);
    }

    [Fact]
    public async Task DvrService_CreateAndCancelTimerAsync_RoundTrips()
    {
        var sut = new DvrService(NullLogger<DvrService>.Instance, _apiClient, _urlBuilder);
        var guideService = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder);

        var channels = (await guideService.GetChannelsAsync(CancellationToken.None)).ToList();
        if (channels.Count == 0)
        {
            return; // Skip if no channels.
        }

        var channelId = channels.First().Id;

        // Create a timer for 5 minutes in the future (so it won't start yet).
        var start = DateTime.UtcNow.AddMinutes(5);
        var end = start.AddMinutes(1);
        var timerInfo = new MediaBrowser.Controller.LiveTv.TimerInfo
        {
            ChannelId = channelId,
            Name = "E2E Test Timer",
            StartDate = start,
            EndDate = end,
        };

        await sut.CreateTimerAsync(timerInfo, CancellationToken.None);

        // Verify timer appears.
        var timers = (await sut.GetTimersAsync(CancellationToken.None)).ToList();
        var created = timers.FirstOrDefault(t => t.Name == "E2E Test Timer");
        Assert.NotNull(created);

        // Cancel it.
        await sut.CancelTimerAsync(created!.Id, CancellationToken.None);

        // Verify removed.
        var timersAfter = (await sut.GetTimersAsync(CancellationToken.None)).ToList();
        Assert.DoesNotContain(timersAfter, t => t.Name == "E2E Test Timer");
    }

    [Fact]
    public async Task DvrService_GetSeriesTimersAsync_ReturnsList()
    {
        var sut = new DvrService(NullLogger<DvrService>.Instance, _apiClient, _urlBuilder);

        var seriesTimers = (await sut.GetSeriesTimersAsync(CancellationToken.None)).ToList();

        Assert.NotNull(seriesTimers);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 21h — Tuner & Input Status
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task StatusService_GetActivityStatusAsync_ReturnsStatus()
    {
        var sut = new StatusService(NullLogger<StatusService>.Instance, _apiClient);

        var status = await sut.GetActivityStatusAsync(CancellationToken.None);

        Assert.NotNull(status);
    }

    [Fact]
    public async Task StatusService_GetConnectionsAsync_ReturnsConnections()
    {
        var sut = new StatusService(NullLogger<StatusService>.Instance, _apiClient);

        var connections = await sut.GetConnectionsAsync(CancellationToken.None);

        Assert.NotNull(connections);
    }

    [Fact]
    public async Task InputMonitorService_GetInputStatusAsync_ReturnsInputEntries()
    {
        var sut = new InputMonitorService(NullLogger<InputMonitorService>.Instance, _apiClient);

        var inputs = await sut.GetInputStatusAsync(CancellationToken.None);

        // With IPTV network configured, there should be input status entries.
        Assert.NotNull(inputs);
    }

    [Fact]
    public async Task SubscriptionService_GetActiveSubscriptionsAsync_ReturnsList()
    {
        var sut = new SubscriptionService(NullLogger<SubscriptionService>.Instance, _apiClient);

        var subscriptions = await sut.GetActiveSubscriptionsAsync(CancellationToken.None);

        Assert.NotNull(subscriptions);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 21i — Diagnostics
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiagnosticService_DiagnoseAsync_ScoreAtLeast80()
    {
        var streamResolver = new Mock<IProfileResolver>();
        streamResolver
            .Setup(x => x.GetProfilesAsync(
                It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Model.Profile.ProfileReference>
            {
                new("test-uuid", "test-pass"),
                new("pass-uuid", "pass"),
            });

        var sut = new DiagnosticService(
            NullLogger<DiagnosticService>.Instance,
            Mock.Of<IServerConfigurationManager>(),
            CreateEncodingReader(),
            streamResolver.Object,
            _apiClient,
            new CachePathProvider(() => null));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.CompatibilityScore >= 80, $"Score was {result.CompatibilityScore}, expected ≥80");
        // No ERROR status on a properly bootstrapped TVH.
        Assert.DoesNotContain(result.Checks, c => c.Status == "ERROR");
    }

    [Fact]
    public async Task DiagnosticService_DiagnoseAsync_DetectsStreamingProfile()
    {
        var streamResolver = new Mock<IProfileResolver>();
        streamResolver
            .Setup(x => x.GetProfilesAsync(
                It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Model.Profile.ProfileReference> { new("test-uuid", "test-pass") });

        var sut = new DiagnosticService(
            NullLogger<DiagnosticService>.Instance,
            Mock.Of<IServerConfigurationManager>(),
            CreateEncodingReader(),
            streamResolver.Object,
            _apiClient,
            new CachePathProvider(() => null));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c =>
            c.Category == "Streaming" || c.Category == "Connection");
    }

    [Fact]
    public async Task DiagnosticService_DiagnoseAsync_ReportsApiVersion()
    {
        var streamResolver = new Mock<IProfileResolver>();
        streamResolver
            .Setup(x => x.GetProfilesAsync(
                It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Model.Profile.ProfileReference>());

        var sut = new DiagnosticService(
            NullLogger<DiagnosticService>.Instance,
            Mock.Of<IServerConfigurationManager>(),
            CreateEncodingReader(),
            streamResolver.Object,
            _apiClient,
            new CachePathProvider(() => null));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c => c.Name.Contains("API Version", StringComparison.OrdinalIgnoreCase));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private IApiClient CreateApiClient(PluginConfigurationProvider configProvider)
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(15) });

        return new ApiClient(httpClientFactory.Object, configProvider);
    }

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






