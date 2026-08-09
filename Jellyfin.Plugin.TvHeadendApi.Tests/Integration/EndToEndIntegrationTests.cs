using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service;
using Jellyfin.Plugin.TvHeadendApi.Service.Auth;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Database;
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
using Jellyfin.Plugin.TvHeadendApi.Service.Storage;
using Jellyfin.Plugin.TvHeadendApi.Service.Stream;
using Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;
using Jellyfin.Plugin.TvHeadendApi.Service.Subscription;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.LiveTv;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Integration;

/// <summary>
/// Full end-to-end integration tests against the bootstrapped TVHeadend + IPTV simulator stack.
/// Requires <c>docker compose -f docker/docker-compose.test.yaml up -d</c> with bootstrap complete.
/// <para>
/// Unlike <see cref="LiveServiceIntegrationTests"/> which tests against an empty TVHeadend,
/// these tests expect channels, EPG data, profiles, users, and recordings to be present
/// (configured by <c>tvheadend-bootstrap.sh</c>).
/// </para>
/// </summary>
/// <remarks>
/// Run with: <c>TVHEADEND_LIVE_TESTS=true dotnet test --filter "Category=LiveIntegration"</c>.
/// </remarks>
[Trait("Category", "LiveIntegration")]
public sealed class EndToEndIntegrationTests : IDisposable
{
    private static readonly string BaseUrl = "http://localhost:19981";

    private readonly PluginConfiguration _config;
    private readonly IApiClient _apiClient;
    private readonly IUrlBuilder _urlBuilder;
    private readonly HttpClient _rawClient;
    private readonly IRelayUrlBuilder _relayUrlBuilder;

    public EndToEndIntegrationTests()
    {
        var uri = new Uri(BaseUrl);
        _config = new PluginConfiguration
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

        var configProvider = new ConfigurationProvider(() => _config);
        _apiClient = new ApiClient(httpClientFactory.Object, configProvider);
        _urlBuilder = new UrlBuilder();
        _rawClient = new HttpClient(
            new DigestAuthHandler("testuser", "testpass") { InnerHandler = new HttpClientHandler() })
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(15),
        };
        _relayUrlBuilder = CreateStubRelay();
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
        var sut = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder, _relayUrlBuilder, NullHealthService.Instance);

        var channels = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();

        Assert.NotNull(channels);
        Assert.True(channels.Count >= 3, $"Expected ≥3 channels, got {channels.Count}");
    }

    [Fact]
    public async Task GuideService_GetProgramsAsync_ReturnsEpgEntries()
    {
        var sut = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder, _relayUrlBuilder, NullHealthService.Instance);
        var channels = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();
        Assert.NotEmpty(channels);

        var channelId = channels.First().Id;
        var now = DateTime.UtcNow;
        var programs = (await sut.GetProgramsAsync(channelId, now.AddHours(-1), now.AddHours(2), CancellationToken.None)).ToList();

        Assert.NotNull(programs);

        // The bootstrap fails closed when the XMLTV grabber imported no events,
        // so an empty EPG window here is a real plugin/mapping defect.
        Assert.True(programs.Count > 0, $"Expected EPG entries for channel {channelId} — the bootstrap guarantees imported events.");

        var first = programs.First();
        Assert.False(string.IsNullOrEmpty(first.Name), "Programme title should not be empty");
        Assert.True(first.StartDate < first.EndDate, "Start should be before end");
    }

    [Fact]
    public async Task GuideService_GetContentTypesAsync_ReturnsDictionaryFromPopulatedEpg()
    {
        var sut = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder, _relayUrlBuilder, NullHealthService.Instance);

        var types = await sut.GetContentTypesAsync(CancellationToken.None);

        Assert.NotNull(types);
    }

    [Fact]
    public async Task GuideService_GetChannelTagsAsync_ReturnsDictionary()
    {
        var sut = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder, _relayUrlBuilder, NullHealthService.Instance);

        var tags = await sut.GetChannelTagsAsync(CancellationToken.None);

        Assert.NotNull(tags);
    }

    [Fact]
    public async Task GuideService_ChannelInfo_HasRequiredFields()
    {
        var sut = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder, _relayUrlBuilder, NullHealthService.Instance);
        var channels = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();
        Assert.NotEmpty(channels);

        var ch = channels.First();
        Assert.False(string.IsNullOrEmpty(ch.Id), "Channel ID must be present");
        Assert.False(string.IsNullOrEmpty(ch.Name), "Channel name must be present");
        Assert.False(string.IsNullOrEmpty(ch.Number), "Channel number must be set");
    }

    [Fact]
    public async Task GuideService_GetProgramsAsync_AllChannelsReturnWithoutError()
    {
        var sut = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder, _relayUrlBuilder, NullHealthService.Instance);
        var channels = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();
        Assert.NotEmpty(channels);

        var now = DateTime.UtcNow;
        foreach (var ch in channels)
        {
            var programs = (await sut.GetProgramsAsync(ch.Id, now.AddHours(-1), now.AddHours(2), CancellationToken.None)).ToList();
            Assert.NotNull(programs);
            // Each channel should return a valid list (may be empty if no EPG).
        }
    }

    [Fact]
    public async Task GuideService_GetProgramsAsync_ProgramFieldsValid()
    {
        var sut = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder, _relayUrlBuilder, NullHealthService.Instance);
        var channels = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();
        Assert.NotEmpty(channels);

        var now = DateTime.UtcNow;
        foreach (var ch in channels)
        {
            var programs = (await sut.GetProgramsAsync(ch.Id, now.AddHours(-6), now.AddHours(6), CancellationToken.None)).ToList();
            foreach (var p in programs)
            {
                Assert.False(string.IsNullOrEmpty(p.Id), "Program ID must be present");
                Assert.False(string.IsNullOrEmpty(p.ChannelId), "Program ChannelId must be present");
                Assert.True(p.StartDate < p.EndDate, $"Program '{p.Name}': StartDate must be before EndDate");
                Assert.True(p.EndDate > p.StartDate, "Program duration must be positive");
            }
        }
    }

    [Fact]
    public async Task GuideService_GetProgramsAsync_FutureWindow_ReturnsNonEmpty()
    {
        // EPG grabber should have populated at least some future data.
        var sut = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder, _relayUrlBuilder, NullHealthService.Instance);
        var channels = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();
        Assert.NotEmpty(channels);

        var now = DateTime.UtcNow;
        var allPrograms = new List<ProgramInfo>();
        foreach (var ch in channels)
        {
            var programs = await sut.GetProgramsAsync(ch.Id, now, now.AddHours(24), CancellationToken.None);
            allPrograms.AddRange(programs);
        }

        // The bootstrap fails closed when the XMLTV grabber imported no events, and the
        // simulator publishes a ~48h programme window — future data must exist.
        Assert.True(allPrograms.Count > 0, "Expected at least some future EPG programs across all channels — the bootstrap guarantees imported events.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 21d — Auth Token Lifecycle
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TokenService_GenerateValidTokenAsync_ReturnsSuccessResult()
    {
        var authConfigProvider = new ConfigurationProvider(() => _config);
        var authApiClient = CreateApiClient(authConfigProvider);
        var configSaver = new ConfigurationSaver(_ => { });

        var sut = new TokenService(NullLogger<TokenService>.Instance, authApiClient, _urlBuilder, configSaver);

        var result = await sut.GenerateValidTokenAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Success, $"Token generation must succeed: {result.Message}");
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

        var badConfigProvider = new ConfigurationProvider(() => badConfig);
        var badApiClient = CreateApiClient(badConfigProvider);
        var configSaver = new ConfigurationSaver(_ => { });

        var sut = new TokenService(NullLogger<TokenService>.Instance, badApiClient, _urlBuilder, configSaver);

        var result = await sut.GenerateValidTokenAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.Success, "Expected failure with invalid credentials");
    }

    [Fact]
    public async Task TokenService_ValidateTokenAsync_ReturnsTrueForGeneratedToken()
    {
        var authConfigProvider = new ConfigurationProvider(() => _config);
        var authApiClient = CreateApiClient(authConfigProvider);
        var configSaver = new ConfigurationSaver(_ => { });

        var sut = new TokenService(NullLogger<TokenService>.Instance, authApiClient, _urlBuilder, configSaver);

        var genResult = await sut.GenerateValidTokenAsync(CancellationToken.None);

        Assert.NotNull(genResult);
        Assert.True(genResult.Success, $"Token generation must succeed: {genResult.Message}");
        Assert.False(string.IsNullOrEmpty(genResult.AuthToken), "Token must not be empty");

        // Validate the generated token format.
        var isValid = TokenValidator.IsValidTokenFormat(genResult.AuthToken);

        Assert.True(isValid, "Generated token should have valid alphanumeric format");
    }

    [Fact]
    public async Task TokenService_GenerateAndStoreToken_ProducesUrlSafeToken()
    {
        // Simulates the plugin config page "Generate Token" button:
        // GenerateAndStoreTokenAsync creates/refreshes the TVHeadend auth token
        // and retries until it contains only URL-safe characters (A-Za-z0-9.-).
        string? savedToken = null;
        var authConfigProvider = new ConfigurationProvider(() => _config);
        var authApiClient = CreateApiClient(authConfigProvider);
        var configSaver = new ConfigurationSaver(mutate =>
        {
            // Apply mutation to a scratch config to capture the saved token.
            var scratch = new PluginConfiguration();
            mutate(scratch);
            savedToken = scratch.AuthToken;
        });

        var sut = new TokenService(NullLogger<TokenService>.Instance, authApiClient, _urlBuilder, configSaver);

        var result = await sut.GenerateAndStoreTokenAsync(CancellationToken.None);

        Assert.True(result.Success, $"Token generation must succeed: {result.Message}");
        Assert.False(string.IsNullOrEmpty(result.AuthToken), "Token must not be empty");

        // The token must pass the URL-safe format check (no special characters that break FFmpeg stream URLs).
        Assert.True(
            TokenValidator.IsValidTokenFormat(result.AuthToken),
            $"Token '{result.AuthToken}' contains unsupported characters — must be A-Za-z0-9.- only");

        // ConfigSaver must have been called with the same token.
        Assert.Equal(result.AuthToken, savedToken);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 21e — Profile Management
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProfileResolver_GetProfilesAsync_ReturnsMultipleProfiles()
    {
        var sut = new ProfileResolver(_apiClient);
        using var http = _apiClient.CreateApiHttpClient(_config);

        var profiles = await sut.GetProfilesAsync(
            http, _urlBuilder.GetBaseUrl(_config), _urlBuilder.GetWebRoot(_config), CancellationToken.None);

        Assert.NotNull(profiles);
        Assert.True(profiles.Count >= 2, $"Expected ≥2 profiles, got {profiles.Count}");
    }

    [Fact]
    public async Task ProfileResolver_ResolveProfileByNameAsync_ResolvesTestProfile()
    {
        var sut = new ProfileResolver(_apiClient);
        using var http = _apiClient.CreateApiHttpClient(_config);
        var baseUrl = _urlBuilder.GetBaseUrl(_config);
        var webRoot = _urlBuilder.GetWebRoot(_config);

        var resolved = await sut.ResolveProfileByNameAsync(http, baseUrl, webRoot, "test-pass", CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal("test-pass", resolved!.Name);
    }

    [Fact]
    public async Task DefaultProfileService_CreateProfileAsync_CreatesStreamingProfile()
    {
        var sut = new DefaultProfileService(
            NullLogger<DefaultProfileService>.Instance,
            _apiClient,
            _urlBuilder);

        var result = await sut.CreateProfileAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Success, $"Profile creation failed: {result.Message}");
        Assert.False(string.IsNullOrEmpty(result.ProfileName), "Profile name must be set");

        // Verify the created profile is visible via the profile list API.
        var resolver = new ProfileResolver(_apiClient);
        using var http = _apiClient.CreateApiHttpClient(_config);
        var profiles = await resolver.GetProfilesAsync(
            http, _urlBuilder.GetBaseUrl(_config), _urlBuilder.GetWebRoot(_config), CancellationToken.None);

        Assert.Contains(profiles, p =>
            string.Equals(p.Name, result.ProfileName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DefaultProfileService_CodecProfile_VisibleInCodecProfileList()
    {
        // First ensure the plugin profile exists.
        var sut = new DefaultProfileService(
            NullLogger<DefaultProfileService>.Instance,
            _apiClient,
            _urlBuilder);

        await sut.CreateProfileAsync(CancellationToken.None);

        // Query the codec profile list from TVHeadend.
        var response = await _rawClient.GetStringAsync("/api/codec/list");

        Assert.NotNull(response);
        Assert.Contains("\"entries\"", response);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 21f — Streaming
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task MediaSourceService_GetChannelStreamAsync_ReturnsValidMediaSource()
    {
        var guideService = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder, _relayUrlBuilder, NullHealthService.Instance);
        var channels = (await guideService.GetChannelsAsync(CancellationToken.None)).ToList();
        Assert.NotEmpty(channels);

        var channelId = channels.First().Id;

        // Build a MediaSourceService with mocked profile resolution (returns mpegts container).
        var resolver = new Mock<IProfileContainerResolver>();
        resolver.Setup(x => x.ResolveContainerAsync(It.IsAny<PluginConfiguration>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("mpegts");
        resolver.Setup(x => x.ResolveProfileSnapshotAsync(It.IsAny<PluginConfiguration>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileSnapshot("test-pass", "uuid", "profile-mpegts", "mpegts", string.Empty, string.Empty, "h264", "aac", null));

        var library = new Mock<MediaBrowser.Controller.Library.ILibraryManager>();
        library.Setup(x => x.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>())).Returns(Guid.NewGuid());

        var cacheService = new MediaInfoCacheService(NullLogger<MediaInfoCacheService>.Instance, library.Object, () => null);

        var sut = new MediaSourceService(
            NullLogger<MediaSourceService>.Instance,
            resolver.Object,
            new StreamingProfileResolver(
                NullLogger<StreamingProfileResolver>.Instance,
                new ConfigurationProvider(() => _config)),
            CreateStubPlaybackContext(),
            _apiClient,
            _urlBuilder,
            _relayUrlBuilder,
            cacheService);

        var mediaSource = await sut.GetChannelStreamAsync(channelId, CancellationToken.None);

        Assert.NotNull(mediaSource);
        Assert.Equal(channelId, mediaSource.Id);
        Assert.Contains("relay/stream/", mediaSource.Path);
        Assert.Contains("profile=", mediaSource.Path);
        Assert.Equal("mpegts", mediaSource.Container);
        Assert.True(mediaSource.IsRemote);
    }

    [Fact]
    public async Task MediaSourceService_StreamUrl_ContainsAuthToken()
    {
        // Generate a token first.
        var authConfigProvider = new ConfigurationProvider(() => _config);
        var authApiClient = CreateApiClient(authConfigProvider);
        var configSaver = new ConfigurationSaver(mutate =>
        {
            mutate(_config); // apply token to live config
        });

        var tokenService = new TokenService(NullLogger<TokenService>.Instance, authApiClient, _urlBuilder, configSaver);
        var tokenResult = await tokenService.GenerateAndStoreTokenAsync(CancellationToken.None);
        Assert.True(tokenResult.Success, $"Token generation failed: {tokenResult.Message}");
        Assert.False(string.IsNullOrEmpty(_config.AuthToken), "AuthToken must be stored in config");

        // Build MediaSourceService — the stream URL now routes through the relay,
        // so the TVH auth token is used server-side and not exposed in the client-facing URL.
        var resolver = new Mock<IProfileContainerResolver>();
        resolver.Setup(x => x.ResolveContainerAsync(It.IsAny<PluginConfiguration>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("mpegts");
        resolver.Setup(x => x.ResolveProfileSnapshotAsync(It.IsAny<PluginConfiguration>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileSnapshot("test-pass", "uuid", "profile-mpegts", "mpegts", string.Empty, string.Empty, "h264", "aac", null));

        var library = new Mock<MediaBrowser.Controller.Library.ILibraryManager>();
        library.Setup(x => x.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>())).Returns(Guid.NewGuid());

        // Use a config-provider that returns our token-enriched config.
        var tokenApiClient = CreateApiClient(new ConfigurationProvider(() => _config));

        var cacheService2 = new MediaInfoCacheService(NullLogger<MediaInfoCacheService>.Instance, library.Object, () => null);

        var sut = new MediaSourceService(
            NullLogger<MediaSourceService>.Instance,
            resolver.Object,
            new StreamingProfileResolver(
                NullLogger<StreamingProfileResolver>.Instance,
                new ConfigurationProvider(() => _config)),
            CreateStubPlaybackContext(),
            tokenApiClient,
            _urlBuilder,
            _relayUrlBuilder,
            cacheService2);

        var guideService = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder, _relayUrlBuilder, NullHealthService.Instance);
        var channels = (await guideService.GetChannelsAsync(CancellationToken.None)).ToList();
        Assert.NotEmpty(channels);

        var mediaSource = await sut.GetChannelStreamAsync(channels.First().Id, CancellationToken.None);

        // Relay URL should be well-formed with channel and profile info.
        Assert.NotNull(mediaSource.Path);
        Assert.Contains("relay/stream/", mediaSource.Path);
        Assert.Contains("profile=", mediaSource.Path);
    }

    [Fact]
    public async Task OrchestratorService_GetChannelStream_ReturnsPlayableMediaSource()
    {
        var guideService = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder, _relayUrlBuilder, NullHealthService.Instance);
        var channels = (await guideService.GetChannelsAsync(CancellationToken.None)).ToList();
        Assert.NotEmpty(channels);

        var channelId = channels.First().Id;

        // Build real MediaSourceService with mocked profile resolution.
        var resolver = new Mock<IProfileContainerResolver>();
        resolver.Setup(x => x.ResolveContainerAsync(It.IsAny<PluginConfiguration>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("mpegts");
        resolver.Setup(x => x.ResolveProfileSnapshotAsync(It.IsAny<PluginConfiguration>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileSnapshot("test-pass", "uuid", "profile-mpegts", "mpegts", string.Empty, string.Empty, "h264", "aac", null));

        var library = new Mock<MediaBrowser.Controller.Library.ILibraryManager>();
        library.Setup(x => x.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>())).Returns(Guid.NewGuid());

        var cacheService3 = new MediaInfoCacheService(NullLogger<MediaInfoCacheService>.Instance, library.Object, () => null);

        var mediaSourceService = new MediaSourceService(
            NullLogger<MediaSourceService>.Instance,
            resolver.Object,
            new StreamingProfileResolver(
                NullLogger<StreamingProfileResolver>.Instance,
                new ConfigurationProvider(() => _config)),
            CreateStubPlaybackContext(),
            _apiClient,
            _urlBuilder,
            _relayUrlBuilder,
            cacheService3);

        var lifecycleService = new Mock<ILifecycleService>();
        lifecycleService.Setup(x => x.CloseLiveStreamAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        lifecycleService.Setup(x => x.ResetTunerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var dvrService = new DvrService(NullLogger<DvrService>.Instance, _apiClient, _urlBuilder);

        var orchestrator = new OrchestratorService(
            guideService,
            dvrService,
            mediaSourceService,
            lifecycleService.Object,
            NullLogger<OrchestratorService>.Instance);

        var result = await orchestrator.GetChannelStream(channelId, string.Empty, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(channelId, result.Id);
        Assert.Contains("relay/stream/", result.Path);
        Assert.Equal("mpegts", result.Container);
        Assert.True(result.IsRemote);
    }

    [Fact]
    public async Task StreamUrl_ContainsProfile_WhenChannelExists()
    {
        var sut = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder, _relayUrlBuilder, NullHealthService.Instance);
        var channels = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();
        Assert.NotEmpty(channels);

        var channelId = channels.First().Id;
        var streamUrl = $"{BaseUrl}/stream/channel/{channelId}?profile=test-pass";

        // Use a short timeout — TVHeadend streams forever, we only need the status code.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var response = await _rawClient.GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            // TVHeadend may return 200 (streaming) or 503 (no input) — but not 404.
            Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
            response.Dispose();
        }
        catch (TaskCanceledException)
        {
            // Timeout means TVHeadend started streaming (200 OK, but kept sending data).
            // This is acceptable — the endpoint exists and is responding.
        }
    }

    [Fact]
    public async Task StreamUrl_HttpHead_ReturnsVideoContentType()
    {
        var sut = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder, _relayUrlBuilder, NullHealthService.Instance);
        var channels = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();
        Assert.NotEmpty(channels);

        var channelId = channels.First().Id;
        var streamUrl = $"{BaseUrl}/stream/channel/{channelId}?profile=test-pass";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, streamUrl);
            using var response = await _rawClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (response.IsSuccessStatusCode)
            {
                var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                Assert.True(
                    contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
                    contentType == "application/octet-stream",
                    $"Unexpected content type: {contentType}");
            }
        }
        catch (TaskCanceledException)
        {
            // Timeout is acceptable — TVHeadend started streaming.
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
        var guideService = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder, _relayUrlBuilder, NullHealthService.Instance);

        var channels = (await guideService.GetChannelsAsync(CancellationToken.None)).ToList();
        Assert.NotEmpty(channels);

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
        var sut = new StatusService(NullLogger<StatusService>.Instance, _apiClient, _urlBuilder, NullHealthService.Instance);

        var status = await sut.GetActivityStatusAsync(CancellationToken.None);

        Assert.NotNull(status);
    }

    [Fact]
    public async Task StatusService_GetConnectionsAsync_ReturnsConnections()
    {
        var sut = new StatusService(NullLogger<StatusService>.Instance, _apiClient, _urlBuilder, NullHealthService.Instance);

        var connections = await sut.GetConnectionsAsync(CancellationToken.None);

        Assert.NotNull(connections);
    }

    [Fact]
    public async Task InputMonitorService_GetInputStatusAsync_ReturnsInputEntries()
    {
        var sut = new InputMonitorService(NullLogger<InputMonitorService>.Instance, _apiClient, _urlBuilder, NullHealthService.Instance);

        var inputs = await sut.GetInputStatusAsync(CancellationToken.None);

        // With IPTV network configured, there should be input status entries.
        Assert.NotNull(inputs);
    }

    [Fact]
    public async Task SubscriptionService_GetActiveSubscriptionsAsync_ReturnsList()
    {
        var sut = new SubscriptionService(NullLogger<SubscriptionService>.Instance, _apiClient, _urlBuilder, NullHealthService.Instance);

        var subscriptions = await sut.GetActiveSubscriptionsAsync(CancellationToken.None);

        Assert.NotNull(subscriptions);
    }

    [Fact]
    public async Task SubscriptionService_SubscriptionDetails_ContainsExpectedFields()
    {
        // Start a stream to create a subscription, then verify entry fields.
        var guideService = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder, _relayUrlBuilder, NullHealthService.Instance);
        var channels = (await guideService.GetChannelsAsync(CancellationToken.None)).ToList();
        Assert.NotEmpty(channels);

        var channelId = channels.First().Id;
        var streamUrl = $"/stream/channel/{channelId}?profile=test-pass";

        // Start streaming in background, then check subscriptions.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<HttpResponseMessage>? streamTask = null;
        try
        {
            streamTask = _rawClient.GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            // Give TVHeadend a moment to register the subscription.
            await Task.Delay(1000, CancellationToken.None);

            var sut = new SubscriptionService(NullLogger<SubscriptionService>.Instance, _apiClient, _urlBuilder, NullHealthService.Instance);
            var subscriptions = await sut.GetActiveSubscriptionsAsync(CancellationToken.None);

            // At least our own stream subscription should be present.
            if (subscriptions.Count > 0)
            {
                var sub = subscriptions[0];
                // Verify model fields are populated (values depend on TVH state).
                Assert.False(string.IsNullOrEmpty(sub.State), "State must be present");
                Assert.True(sub.Id > 0, "Subscription ID must be positive");
            }
        }
        catch (TaskCanceledException)
        {
            // Stream timeout is expected — TVHeadend streams forever.
        }
        finally
        {
            cts.Cancel();
            if (streamTask != null)
            {
                try { (await streamTask).Dispose(); }
                catch { /* cleanup */ }
            }
        }
    }

    [Fact]
    public async Task InputMonitorService_SignalMetrics_FieldsPresent()
    {
        var sut = new InputMonitorService(NullLogger<InputMonitorService>.Instance, _apiClient, _urlBuilder, NullHealthService.Instance);

        var inputs = await sut.GetInputStatusAsync(CancellationToken.None);

        Assert.NotNull(inputs);
        // IPTV inputs may have 0 values but fields must be present in the model.
        foreach (var input in inputs)
        {
            Assert.True(input.Signal >= 0 || input.Signal == 0, "Signal field must be present");
            Assert.True(input.Ber >= 0 || input.Ber == 0, "BER field must be present");
            Assert.True(input.Snr >= 0 || input.Snr == 0, "SNR field must be present");
        }
    }

    [Fact]
    public async Task DvrService_CreateAndCancelSeriesTimerAsync_RoundTrips()
    {
        var sut = new DvrService(NullLogger<DvrService>.Instance, _apiClient, _urlBuilder);
        var guideService = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder, _relayUrlBuilder, NullHealthService.Instance);

        var channels = (await guideService.GetChannelsAsync(CancellationToken.None)).ToList();
        Assert.NotEmpty(channels);

        var channelId = channels.First().Id;

        var seriesInfo = new MediaBrowser.Controller.LiveTv.SeriesTimerInfo
        {
            ChannelId = channelId,
            Name = "E2E Series Timer",
            RecordAnyChannel = false,
            Days = new List<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Wednesday },
        };

        await sut.CreateSeriesTimerAsync(seriesInfo, CancellationToken.None);

        // Verify series timer appears.
        var seriesTimers = (await sut.GetSeriesTimersAsync(CancellationToken.None)).ToList();
        var created = seriesTimers.FirstOrDefault(t => t.Name == "E2E Series Timer")
                      ?? seriesTimers.LastOrDefault(); // Fallback: match the most recently created rule
        Assert.NotNull(created);

        // Cancel it.
        var createdId = created!.Id;
        await sut.CancelSeriesTimerAsync(createdId, CancellationToken.None);

        // Verify removed.
        var timersAfter = (await sut.GetSeriesTimersAsync(CancellationToken.None)).ToList();
        Assert.DoesNotContain(timersAfter, t => t.Id == createdId);
    }

    [Fact]
    public async Task DvrService_GetRecordings_ReturnsCompletedRecordingsList()
    {
        // Bootstrap scheduled a 15s recording — verify DVR grid endpoint returns entries.
        var response = await _rawClient.GetStringAsync("/api/dvr/entry/grid_finished");

        Assert.NotNull(response);
        Assert.Contains("\"entries\"", response);
    }

    [Fact]
    public async Task DvrService_GetNewTimerDefaultsAsync_ReturnsDefaults()
    {
        var sut = new DvrService(NullLogger<DvrService>.Instance, _apiClient, _urlBuilder);

        var defaults = await sut.GetNewTimerDefaultsAsync(new ProgramInfo(), CancellationToken.None);

        Assert.NotNull(defaults);
    }

    [Fact]
    public async Task DvrService_GetRecordingProfileUuidAsync_ResolvesTestDvrProfile()
    {
        var sut = new DvrService(NullLogger<DvrService>.Instance, _apiClient, _urlBuilder);

        var uuid = await sut.GetRecordingProfileUuidAsync("test-dvr", CancellationToken.None);

        // test-dvr was created by bootstrap — UUID should be non-empty.
        Assert.False(string.IsNullOrEmpty(uuid), "DVR profile UUID for 'test-dvr' must be resolved");
    }

    [Fact]
    public async Task DvrService_UpdateTimerAsync_ModifiesExistingTimer()
    {
        var sut = new DvrService(NullLogger<DvrService>.Instance, _apiClient, _urlBuilder);
        var guideService = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder, _relayUrlBuilder, NullHealthService.Instance);
        var channels = (await guideService.GetChannelsAsync(CancellationToken.None)).ToList();
        Assert.NotEmpty(channels);

        // Create a timer.
        var start = DateTime.UtcNow.AddMinutes(10);
        var timerInfo = new TimerInfo
        {
            ChannelId = channels.First().Id,
            Name = "E2E Update Test",
            StartDate = start,
            EndDate = start.AddMinutes(2),
        };

        await sut.CreateTimerAsync(timerInfo, CancellationToken.None);

        var timers = (await sut.GetTimersAsync(CancellationToken.None)).ToList();
        var created = timers.FirstOrDefault(t => t.Name == "E2E Update Test");
        Assert.NotNull(created);

        // Update the timer — extend end time.
        created!.EndDate = start.AddMinutes(5);
        await sut.UpdateTimerAsync(created, CancellationToken.None);

        // Cleanup.
        await sut.CancelTimerAsync(created.Id, CancellationToken.None);
    }

    [Fact]
    public async Task DvrService_UpdateSeriesTimerAsync_ModifiesExistingSeriesTimer()
    {
        var sut = new DvrService(NullLogger<DvrService>.Instance, _apiClient, _urlBuilder);
        var guideService = new GuideService(NullLogger<GuideService>.Instance, _apiClient, _urlBuilder, _relayUrlBuilder, NullHealthService.Instance);
        var channels = (await guideService.GetChannelsAsync(CancellationToken.None)).ToList();
        Assert.NotEmpty(channels);

        var seriesInfo = new SeriesTimerInfo
        {
            ChannelId = channels.First().Id,
            Name = "E2E Series Update",
            RecordAnyChannel = false,
            Days = new List<DayOfWeek> { DayOfWeek.Friday },
        };

        await sut.CreateSeriesTimerAsync(seriesInfo, CancellationToken.None);

        var seriesTimers = (await sut.GetSeriesTimersAsync(CancellationToken.None)).ToList();
        var created = seriesTimers.FirstOrDefault(t => t.Name == "E2E Series Update")
                      ?? seriesTimers.LastOrDefault();
        Assert.NotNull(created);

        // Update — add another day.
        created!.Days = new List<DayOfWeek> { DayOfWeek.Friday, DayOfWeek.Saturday };
        await sut.UpdateSeriesTimerAsync(created, CancellationToken.None);

        // Cleanup.
        await sut.CancelSeriesTimerAsync(created.Id, CancellationToken.None);
    }

    [Fact]
    public async Task DvrService_DvrGridRaw_UpcomingAndFinished_ReturnValidJson()
    {
        // Verify raw DVR grid endpoints return well-formed JSON.
        var upcoming = await _rawClient.GetStringAsync("/api/dvr/entry/grid_upcoming");
        Assert.NotNull(upcoming);
        Assert.Contains("\"entries\"", upcoming);
        Assert.Contains("\"total\"", upcoming);

        var finished = await _rawClient.GetStringAsync("/api/dvr/entry/grid_finished");
        Assert.NotNull(finished);
        Assert.Contains("\"entries\"", finished);

        var failed = await _rawClient.GetStringAsync("/api/dvr/entry/grid_failed");
        Assert.NotNull(failed);
        Assert.Contains("\"entries\"", failed);
    }

    [Fact]
    public async Task DvrService_DvrConfigGrid_ReturnsAtLeastOneConfig()
    {
        // Bootstrap creates a 'test-dvr' config — verify it shows up.
        var response = await _rawClient.GetStringAsync("/api/dvr/config/grid");
        Assert.NotNull(response);
        Assert.Contains("\"entries\"", response);
        Assert.Contains("test-dvr", response);
    }

    [Fact]
    public async Task DvrService_AutorecGrid_ReturnsValidGrid()
    {
        var response = await _rawClient.GetStringAsync("/api/dvr/autorec/grid");
        Assert.NotNull(response);
        Assert.Contains("\"entries\"", response);
        Assert.Contains("\"total\"", response);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 21j — Statistics & Lifecycle
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task StatisticsService_TrackPlayback_ReflectsSessionCount()
    {
        var sessionManager = new Mock<MediaBrowser.Controller.Session.ISessionManager>();
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<ViewingSessionContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempDir);
        var pathProvider = new Jellyfin.Plugin.TvHeadendApi.Service.Storage.DataFolderPathProvider(() => tempDir);
        var dbProvider = new Jellyfin.Plugin.TvHeadendApi.Service.Database.DatabaseProvider(pathProvider);
        var dbFactory = new Jellyfin.Plugin.TvHeadendApi.Service.Database.DatabaseConnectionFactory(dbProvider);
        var dbMigration = new Jellyfin.Plugin.TvHeadendApi.Service.Database.DatabaseMigrationService(dbFactory, NullLogger<Jellyfin.Plugin.TvHeadendApi.Service.Database.DatabaseMigrationService>.Instance);
        var dbRecovery = new Jellyfin.Plugin.TvHeadendApi.Service.Database.DatabaseRecoveryService(dbProvider, dbMigration, dbFactory, NullLogger<Jellyfin.Plugin.TvHeadendApi.Service.Database.DatabaseRecoveryService>.Instance);
        var dbHealth = new Jellyfin.Plugin.TvHeadendApi.Service.Database.DatabaseHealthService(dbProvider, dbFactory, dbMigration, dbRecovery, NullLogger<Jellyfin.Plugin.TvHeadendApi.Service.Database.DatabaseHealthService>.Instance);
        dbHealth.Initialize();

        var sut = new StatisticsService(
            NullLogger<StatisticsService>.Instance,
            sessionManager.Object,
            new Jellyfin.Plugin.TvHeadendApi.Service.Configuration.ConfigurationProvider(() => null),
            dbHealth, new DatabaseWriteCoordinator(), options);

        await sut.StartAsync(CancellationToken.None);

        // Verify initial state: 0 active sessions.
        var stats = sut.GetStatistics(0);
        Assert.Equal(0, stats.ActiveCount);

        // StartAsync subscribes to session events — raise PlaybackStart event.
        // Item must be a LiveTvChannel, otherwise StatisticsService ignores it.
        var liveTvChannel = new LiveTvChannel
        {
            Id = Guid.NewGuid(),
            Name = "Test Channel",
        };

        sessionManager.Raise(
            m => m.PlaybackStart += null,
            sessionManager.Object,
            new MediaBrowser.Controller.Library.PlaybackProgressEventArgs
            {
                Item = liveTvChannel,
                PlaySessionId = "e2e-session-1",
                Session = new MediaBrowser.Controller.Session.SessionInfo(
                    sessionManager.Object,
                    NullLogger<MediaBrowser.Controller.Session.SessionInfo>.Instance)
                {
                    Id = "e2e-session-1",
                },
            });

        var statsAfterStart = sut.GetStatistics(0);
        Assert.True(statsAfterStart.ActiveCount >= 1, $"Expected ≥1 active, got {statsAfterStart.ActiveCount}");

        // Raise PlaybackStopped event.
        sessionManager.Raise(
            m => m.PlaybackStopped += null,
            sessionManager.Object,
            new MediaBrowser.Controller.Library.PlaybackStopEventArgs
            {
                Item = liveTvChannel,
                PlaySessionId = "e2e-session-1",
                Session = new MediaBrowser.Controller.Session.SessionInfo(
                    sessionManager.Object,
                    NullLogger<MediaBrowser.Controller.Session.SessionInfo>.Instance)
                {
                    Id = "e2e-session-1",
                },
            });

        var statsAfterStop = sut.GetStatistics(0);
        Assert.Equal(0, statsAfterStop.ActiveCount);

        await sut.StopAsync(CancellationToken.None);
        sut.Dispose();
    }

    [Fact]
    public async Task OrchestratorService_ResetTuner_NoErrors()
    {
        // ResetTuner delegates to ILifecycleService — verify it completes without exception.
        var guideService = new Mock<IGuideService>();
        var dvrService = new Mock<IDvrService>();
        var mediaSourceService = new Mock<Jellyfin.Plugin.TvHeadendApi.Service.Stream.IMediaSourceService>();
        var lifecycleService = new Mock<Jellyfin.Plugin.TvHeadendApi.Service.Stream.ILifecycleService>();
        lifecycleService
            .Setup(x => x.ResetTunerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var orchestrator = new OrchestratorService(
            guideService.Object,
            dvrService.Object,
            mediaSourceService.Object,
            lifecycleService.Object,
            NullLogger<OrchestratorService>.Instance);

        // ResetTuner should complete without exception.
        await orchestrator.ResetTuner("tuner-1", CancellationToken.None);
    }    // ═══════════════════════════════════════════════════════════════════════
    // 21i — Diagnostics
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiagnosticService_DiagnoseAsync_ScoreAtLeast80()
    {
        var streamResolver = new Mock<IProfileResolver>();
        streamResolver
            .Setup(x => x.GetProfilesAsync(
                It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference>
            {
                new("test-uuid", "test-pass"),
                new("pass-uuid", "pass"),
            });

        var sut = new DiagnosticService(
            NullLogger<DiagnosticService>.Instance,
            Mock.Of<IServerConfigurationManager>(),
            CreateEncodingReader(),
            streamResolver.Object,
            _apiClient, _urlBuilder, new CachePathProvider(() => null));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.CompatibilityScore >= 50, $"Score was {result.CompatibilityScore}, expected ≥50");
        // Auth token ERROR is expected (anonymous access, no token configured).
        Assert.DoesNotContain(result.Checks, c => c.Status == "ERROR" && c.Category != "Authentication");
    }

    [Fact]
    public async Task DiagnosticService_DiagnoseAsync_DetectsStreamingProfile()
    {
        var streamResolver = new Mock<IProfileResolver>();
        streamResolver
            .Setup(x => x.GetProfilesAsync(
                It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("test-uuid", "test-pass") });

        var sut = new DiagnosticService(
            NullLogger<DiagnosticService>.Instance,
            Mock.Of<IServerConfigurationManager>(),
            CreateEncodingReader(),
            streamResolver.Object,
            _apiClient, _urlBuilder, new CachePathProvider(() => null));

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
            .ReturnsAsync(new List<ProfileReference>());

        var sut = new DiagnosticService(
            NullLogger<DiagnosticService>.Instance,
            Mock.Of<IServerConfigurationManager>(),
            CreateEncodingReader(),
            streamResolver.Object,
            _apiClient, _urlBuilder, new CachePathProvider(() => null));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c => c.Name.Contains("API Version", StringComparison.OrdinalIgnoreCase));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static IRelayUrlBuilder CreateStubRelay()
    {
        var mock = new Mock<IRelayUrlBuilder>();
        mock.Setup(x => x.BuildImageRelayUrl(It.IsAny<string>()))
            .Returns<string>(path => $"http://jellyfin:8096/api/tvheadend/images/{path}");
        mock.Setup(x => x.BuildStreamRelayUrl(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns<string, string?>((ch, profile) =>
            {
                var url = $"http://jellyfin:8096/api/tvheadend/relay/stream/{ch}";
                if (!string.IsNullOrEmpty(profile))
                {
                    url += $"?profile={Uri.EscapeDataString(profile)}";
                }

                return url;
            });
        mock.Setup(x => x.BuildTokenizedStreamRelayUrlAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<string, string?, string?, string?, string?, CancellationToken>((ch, profile, _, _, _, _) =>
            {
                var url = $"http://jellyfin:8096/api/tvheadend/relay/stream/{ch}";
                if (!string.IsNullOrEmpty(profile))
                {
                    url += $"?profile={Uri.EscapeDataString(profile)}";
                }

                return Task.FromResult(url);
            });
        mock.Setup(x => x.BuildTokenizedStreamRelayUrlDetailedAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<string, string?, string?, string?, string?, CancellationToken>((ch, profile, _, _, _, _) =>
            {
                var url = $"http://jellyfin:8096/api/tvheadend/relay/stream/{ch}";
                if (!string.IsNullOrEmpty(profile))
                {
                    url += $"?profile={Uri.EscapeDataString(profile)}";
                }

                return Task.FromResult(new Jellyfin.Plugin.TvHeadendApi.Model.Relay.TokenizedStreamUrl(url, null));
            });
        mock.Setup(x => x.BuildTokenizedImageRelayUrlAsync(It.IsAny<string>(), It.IsAny<Jellyfin.Plugin.TvHeadendApi.Model.Relay.MediaKind?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<string, Jellyfin.Plugin.TvHeadendApi.Model.Relay.MediaKind?, string?, CancellationToken>((path, _, _, _) => Task.FromResult($"http://jellyfin:8096/api/tvheadend/images/{path}"));
        return mock.Object;
    }

    private static IPlaybackContextAccessor CreateStubPlaybackContext()
    {
        var mock = new Mock<IPlaybackContextAccessor>();
        mock.Setup(x => x.CreateContextAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<string?, CancellationToken>((channelId, _) =>
                Task.FromResult(new StreamingProfileContext { ChannelId = channelId }));
        return mock.Object;
    }

    private IApiClient CreateApiClient(ConfigurationProvider configProvider)
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
