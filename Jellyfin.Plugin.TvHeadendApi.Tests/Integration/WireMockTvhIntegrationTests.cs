using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Guide;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Stream;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Integration;

/// <summary>
/// Full-stack WireMock integration tests that drive the service layer
/// against a simulated TVHeadend HTTP server.
/// These tests are tagged <c>JellyfinIntegration</c> and excluded from
/// default CI unit-test runs.
/// </summary>
[Trait("Category", "JellyfinIntegration")]
public sealed class WireMockTvhIntegrationTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly PluginConfiguration _config;
    private readonly IApiClient _apiClient;
    private readonly IUrlBuilder _urlBuilder;

    public WireMockTvhIntegrationTests()
    {
        _server = WireMockServer.Start();

        _config = new PluginConfiguration
        {
            Host = "localhost",
            Port = _server.Port,
            UseSSL = false,
            Webroot = "/",
            AllowAnonymousAccess = true,
            AuthToken = "testtoken123",
            StreamingProfile = "pass",
            RecordingProfile = "default",
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = false,
            AnalyzeDurationMs = 200,
            BufferMs = 0,
        };

        var apiClientMock = new Mock<IApiClient>();
        apiClientMock.Setup(x => x.GetCurrentConfiguration()).Returns(_config);
        apiClientMock
            .Setup(x => x.CreateApiHttpClient(It.IsAny<PluginConfiguration>()))
            .Returns(() => new HttpClient { BaseAddress = new Uri($"http://localhost:{_server.Port}") });
        apiClientMock
            .Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>(async (client, url, ct) =>
            {
                using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            });
        apiClientMock
            .Setup(x => x.PostFormAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, IEnumerable<KeyValuePair<string, string>>, CancellationToken>(async (client, url, values, ct) =>
            {
                using var content = new FormUrlEncodedContent(values);
                return await client.PostAsync(url, content, ct).ConfigureAwait(false);
            });

        _apiClient = apiClientMock.Object;

        var urlBuilderMock = new Mock<IUrlBuilder>();
        urlBuilderMock
            .Setup(x => x.GetBaseUrl(It.IsAny<PluginConfiguration>()))
            .Returns($"http://localhost:{_server.Port}");
        urlBuilderMock
            .Setup(x => x.GetWebRoot(It.IsAny<PluginConfiguration>()))
            .Returns("/");
        urlBuilderMock
            .Setup(x => x.BuildApiUrl(It.IsAny<PluginConfiguration>(), It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((cfg, ep) =>
                $"http://localhost:{_server.Port}/{ep.TrimStart('/')}");
        urlBuilderMock
            .Setup(x => x.BuildResourceUrl(It.IsAny<PluginConfiguration>(), It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((cfg, ep) =>
                $"http://localhost:{_server.Port}/{ep.TrimStart('/')}");
        urlBuilderMock
            .Setup(x => x.BuildApiUrl(It.IsAny<PluginConfiguration>(), It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((cfg, ep) =>
                $"http://localhost:{_server.Port}/{ep.TrimStart('/')}");
        urlBuilderMock
            .Setup(x => x.MaskSensitiveData(It.IsAny<string>(), It.IsAny<PluginConfiguration>()))
            .Returns<string, PluginConfiguration>((v, _) => v);
        _urlBuilder = urlBuilderMock.Object;
    }

    public void Dispose() => _server.Dispose();

    // ── GuideService ─────────────────────────────────────────────────

    [Fact]
    public async Task GuideService_GetChannelsAsync_WireMock_ReturnsMappedChannels()
    {
        _server
            .Given(Request.Create().WithPath("/api/channel/grid").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                    {
                      "entries": [
                        { "uuid": "ch-001", "name": "Das Erste HD", "number": 1000000, "enabled": true, "icon_public_url": "imagecache/14", "tags": [] },
                        { "uuid": "ch-002", "name": "ZDF HD", "number": 2000000, "enabled": false, "icon_public_url": "", "tags": [] }
                      ],
                      "total": 2
                    }
                """));
        _server
            .Given(Request.Create().WithPath("/api/channeltag/list").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{ "entries": [] }"""));

        var sut = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder);
        var channels = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();

        Assert.Single(channels); // only enabled channels
        Assert.Equal("ch-001", channels[0].Id);
        Assert.Equal("Das Erste HD", channels[0].Name);
        Assert.Equal("1", channels[0].Number);
        Assert.NotNull(channels[0].ImageUrl);
        Assert.True(channels[0].HasImage);
    }

    [Fact]
    public async Task GuideService_GetChannelsAsync_WireMock_WithTagResolution_ResolvesTagName()
    {
        _server
            .Given(Request.Create().WithPath("/api/channel/grid").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                    {
                      "entries": [
                        { "uuid": "ch-010", "name": "Sport 1", "number": 3000000, "enabled": true, "tags": ["tag-sports"] }
                      ],
                      "total": 1
                    }
                """));
        _server
            .Given(Request.Create().WithPath("/api/channeltag/list").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{ "entries": [{ "key": "tag-sports", "val": "Sports" }] }"""));

        var sut = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder);
        var channels = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();

        Assert.Single(channels);
        Assert.Contains("Sports", channels[0].Tags!);
        Assert.Equal("Sports", channels[0].ChannelGroup);
    }

    [Fact]
    public async Task GuideService_GetProgramsAsync_WireMock_ReturnsMappedPrograms()
    {
        var startUtc = new DateTime(2026, 4, 17, 20, 0, 0, DateTimeKind.Utc);
        var endUtc = startUtc.AddHours(2);
        var evStart = new DateTimeOffset(startUtc.AddMinutes(10)).ToUnixTimeSeconds();
        var evStop = new DateTimeOffset(startUtc.AddMinutes(50)).ToUnixTimeSeconds();

        _server
            .Given(Request.Create().WithPath("/api/epg/events/grid").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""
                    {
                      "entries": [
                        {
                          "eventId": 1001,
                          "channelUuid": "ch-001",
                          "title": "Tagesschau",
                          "description": "Die aktuellen Nachrichten.",
                          "start": {{evStart}},
                          "stop": {{evStop}},
                          "genre": [32],
                          "hd": 1
                        }
                      ],
                      "totalCount": 1
                    }
                """));

        var sut = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder);
        var programs = (await sut.GetProgramsAsync("ch-001", startUtc, endUtc, CancellationToken.None)).ToList();

        Assert.Single(programs);
        Assert.Equal("1001", programs[0].Id);
        Assert.Equal("ch-001", programs[0].ChannelId);
        Assert.Equal("Tagesschau", programs[0].Name);
        Assert.Equal("Die aktuellen Nachrichten.", programs[0].Overview);
        Assert.True(programs[0].IsNews);
        Assert.True(programs[0].IsHD);
    }

    [Fact]
    public async Task GuideService_GetContentTypesAsync_WireMock_ReturnsDictionary()
    {
        _server
            .Given(Request.Create().WithPath("/api/epg/content_type/list").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                    { "entries": [{ "key": 64, "val": "Sports" }, { "key": 32, "val": "News" }] }
                """));

        var sut = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder);
        var types = await sut.GetContentTypesAsync(CancellationToken.None);

        Assert.Equal(2, types.Count);
        Assert.Equal("Sports", types[64]);
        Assert.Equal("News", types[32]);
    }

    // ── DiagnosticService ─────────────────────────────────────────────

    [Fact]
    public async Task DiagnosticService_DiagnoseAsync_WireMock_HappyPath_ReturnsOkScore()
    {
        SetupDiagnosticHappyPathMocks();

        var streamResolver = new Mock<IProfileResolver>();
        streamResolver
            .Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Jellyfin.Plugin.TvHeadendApi.Model.Profile.ProfileReference>
            {
                new("profile-key-1", "pass")
            });
        streamResolver
            .Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Jellyfin.Plugin.TvHeadendApi.Model.Profile.ProfileDetails(
                "profile-key-1", "pass", "profile", "mpegts", "mpegts",
                string.Empty, string.Empty, new List<string>(), new List<string>(), null));

        var sut = new DiagnosticService(
            NullLogger<DiagnosticService>.Instance,
            Mock.Of<IServerConfigurationManager>(),
            CreateEncodingReader(),
            streamResolver.Object,
            _apiClient, _urlBuilder, new CachePathProvider(() => null));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.True(result.CompatibilityScore >= 80, $"Score was {result.CompatibilityScore}");
        Assert.Contains(result.Checks, c => c.Category == "Connection" && c.Name == "TVHeadend Connectivity" && c.Status == "OK");
        Assert.Contains(result.AvailableStreamingProfiles, p => p == "pass");
    }

    [Fact]
    public async Task DiagnosticService_DiagnoseAsync_WireMock_WhenServerUnreachable_ReturnsError()
    {
        // Use a port where nothing is running
        var badConfig = new PluginConfiguration
        {
            Host = "localhost",
            Port = 19998,
            UseSSL = false,
            Webroot = "/",
            AllowAnonymousAccess = true,
            AuthToken = "abc123",
            StreamingProfile = "pass",
            RecordingProfile = "default",
        };

        var apiClientMock = new Mock<IApiClient>();
        apiClientMock.Setup(x => x.GetCurrentConfiguration()).Returns(badConfig);
        var urlBuilderMock2 = new Mock<IUrlBuilder>();
        urlBuilderMock2.Setup(x => x.GetBaseUrl(It.IsAny<PluginConfiguration>())).Returns("http://localhost:19998");
        urlBuilderMock2.Setup(x => x.GetWebRoot(It.IsAny<PluginConfiguration>())).Returns("/");
        apiClientMock.Setup(x => x.CreateApiHttpClient(It.IsAny<PluginConfiguration>())).Returns(new HttpClient());
        apiClientMock
            .Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

        var sut = new DiagnosticService(
            NullLogger<DiagnosticService>.Instance,
            Mock.Of<IServerConfigurationManager>(),
            CreateEncodingReader(),
            Mock.Of<IProfileResolver>(),
            apiClientMock.Object, urlBuilderMock2.Object, new CachePathProvider(() => null));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Equal("ERROR", result.OverallStatus);
        Assert.Equal(0, result.CompatibilityScore);
        Assert.Contains(result.Checks, c => c.Category == "Connection" && c.Status == "ERROR");
    }

    [Fact]
    public async Task DiagnosticService_DiagnoseAsync_WireMock_WithOldApiVersion_AddsWarning()
    {
        _server
            .Given(Request.Create().WithPath("/api/serverinfo").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{ "sw_version": "4.1", "api_version": 12, "name": "tvh-old" }"""));
        _server
            .Given(Request.Create().WithPath("/api/channel/grid").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{ "entries": [], "total": 0 }"""));
        _server
            .Given(Request.Create().WithPath("/api/dvr/entry/grid").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{ "entries": [], "total": 0 }"""));
        _server
            .Given(Request.Create().WithPath("/api/idnode/load").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{ "entries": [] }"""));

        var streamResolver = new Mock<IProfileResolver>();
        streamResolver
            .Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Jellyfin.Plugin.TvHeadendApi.Model.Profile.ProfileReference>());

        var sut = new DiagnosticService(
            NullLogger<DiagnosticService>.Instance,
            Mock.Of<IServerConfigurationManager>(),
            CreateEncodingReader(),
            streamResolver.Object,
            _apiClient, _urlBuilder, new CachePathProvider(() => null));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c => c.Category == "Connection" && c.Name == "API Version" && c.Status == "WARNING");
    }

    // ── ProfileResolver ───────────────────────────────────────────────

    [Fact]
    public async Task ProfileResolver_GetProfilesAsync_WireMock_ReturnsProfileList()
    {
        _server
            .Given(Request.Create().WithPath("/api/profile/list").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                    { "entries": [
                        { "key": "uuid-pass", "val": "pass" },
                        { "key": "uuid-jf", "val": "jellyfin" }
                    ]}
                """));

        var sut = new ProfileResolver(_apiClient);
        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{_server.Port}") };
        var profiles = await sut.GetProfilesAsync(http, $"http://localhost:{_server.Port}", "/", CancellationToken.None);

        Assert.Equal(2, profiles.Count);
        Assert.Equal("pass", profiles[0].Name);
        Assert.Equal("jellyfin", profiles[1].Name);
    }

    [Fact]
    public async Task ProfileResolver_ResolveProfileByNameAsync_WireMock_ReturnsResolvedProfile()
    {
        _server
            .Given(Request.Create().WithPath("/api/profile/list").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{ "entries": [{ "key": "uuid-pass", "val": "pass" }] }"""));
        _server
            .Given(Request.Create().WithPath("/api/codec_profile/list").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{ "entries": [] }"""));
        _server
            .Given(Request.Create().WithPath("/api/idnode/load").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                    { "entries": [{
                        "class": "profile-pass",
                        "container": "mpegts",
                        "pro_vcodec": "",
                        "pro_acodec": ""
                    }]}
                """));

        var sut = new ProfileResolver(_apiClient);
        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{_server.Port}") };
        var resolved = await sut.ResolveProfileByNameAsync(http, $"http://localhost:{_server.Port}", "/", "pass", CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal("pass", resolved.Name);
        Assert.Equal("mpegts", resolved.Container);
    }

    // ── StatisticsService Persistence ────────────────────────────────

    [Fact]
    public async Task StatisticsService_Persistence_WireMock_SaveAndReloadSessions()
    {
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
        System.IO.Directory.CreateDirectory(tempDir);
        try
        {
            var sm = new Mock<MediaBrowser.Controller.Session.ISessionManager>();
            var sut1 = new Jellyfin.Plugin.TvHeadendApi.Service.Statistics.StatisticsService(
                NullLogger<Jellyfin.Plugin.TvHeadendApi.Service.Statistics.StatisticsService>.Instance,
                sm.Object,
                new PluginConfigurationProvider(() => null),
                new DataFolderPathProvider(() => tempDir));

            await sut1.StartAsync(CancellationToken.None);

            // Simulate a completed session via playback events
            var channel = new MediaBrowser.Controller.LiveTv.LiveTvChannel { Name = "ARD" };
            sm.Raise(m => m.PlaybackStart += null,
                new MediaBrowser.Controller.Library.PlaybackProgressEventArgs
                {
                    Item = channel,
                    PlaySessionId = "persist-session-1",
                    DeviceName = "TestTV",
                    ClientName = "Kodi",
                });
            sm.Raise(m => m.PlaybackStopped += null,
                new MediaBrowser.Controller.Library.PlaybackStopEventArgs
                {
                    Item = channel,
                    PlaySessionId = "persist-session-1",
                });

            await sut1.StopAsync(CancellationToken.None);
            sut1.Dispose();

            // Reload from disk
            var sut2 = new Jellyfin.Plugin.TvHeadendApi.Service.Statistics.StatisticsService(
                NullLogger<Jellyfin.Plugin.TvHeadendApi.Service.Statistics.StatisticsService>.Instance,
                sm.Object,
                new PluginConfigurationProvider(() => null),
                new DataFolderPathProvider(() => tempDir));
            await sut2.StartAsync(CancellationToken.None);

            Assert.NotEmpty(sut2.AllSessions);
            Assert.Equal("ARD", sut2.AllSessions[0].ChannelName);

            sut2.Dispose();
        }
        finally
        {
            System.IO.Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private void SetupDiagnosticHappyPathMocks()
    {
        _server
            .Given(Request.Create().WithPath("/api/serverinfo").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{ "sw_version": "4.3", "api_version": 19, "name": "tvh-test" }"""));
        _server
            .Given(Request.Create().WithPath("/api/channel/grid").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{ "entries": [{ "uuid": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }], "total": 1 }"""));
        _server
            .Given(Request.Create().WithPath("/api/dvr/entry/grid").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{ "entries": [], "total": 0 }"""));
        _server
            .Given(Request.Create().WithPath("/api/codec_profile/list").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{ "entries": [] }"""));
        _server
            .Given(Request.Create().WithPath("/api/idnode/load").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{ "entries": [{ "name": "default" }] }"""));
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


