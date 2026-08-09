using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Guide;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class GuideServiceCoreTests
{
    private static IRelayUrlBuilder StubRelay()
    {
        var mock = new Mock<IRelayUrlBuilder>();
        mock.Setup(x => x.BuildImageRelayUrl(It.IsAny<string>()))
            .Returns<string>(path => $"http://jellyfin:8096/api/tvheadend/images/{path}");
        mock.Setup(x => x.BuildStreamRelayUrl(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns<string, string?>((ch, _) => $"http://jellyfin:8096/api/tvheadend/stream/{ch}");
        mock.Setup(x => x.BuildTokenizedImageRelayUrlAsync(It.IsAny<string>(), It.IsAny<MediaKind?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<string, MediaKind?, string?, CancellationToken>((path, _, _, _) => Task.FromResult($"http://jellyfin:8096/api/tvheadend/images/{path}"));
        return mock.Object;
    }

    [Fact]
    public async Task GetChannelsAsync_WithSuccessResponse_MapsNumberAndImageProxyUrl()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();

        var json = """
                   {
                     "entries": [
                       { "uuid": "ch-1", "name": "One", "number": 7, "icon_public_url": "/imagecache/my icon.png", "enabled": true },
                       { "uuid": "ch-2", "name": "Two", "number": "7.5", "icon_public_url": "", "enabled": true }
                     ],
                     "total": 2
                   }
                   """;

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.MaskSensitiveData(It.IsAny<string>(), config)).Returns<string, PluginConfiguration>((value, _) => value);
        urlBuilder
            .Setup(x => x.BuildApiUrl(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));
        urlBuilder
            .Setup(x => x.BuildResourceUrl(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));
        urlBuilder
            .Setup(x => x.BuildApiUrl(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var result = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal("7", result[0].Number);
        Assert.Equal("http://jellyfin:8096/api/tvheadend/images/imagecache/my icon.png", result[0].ImageUrl);
        Assert.True(result[0].HasImage);
        Assert.Equal("7.5", result[1].Number);
        Assert.Null(result[1].ImageUrl);
        Assert.False(result[1].HasImage);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenEntriesEmpty_ReturnsEmpty()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"entries\":[],\"total\":0}")
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns("http://tvh/channels");
        urlBuilder.Setup(x => x.BuildResourceUrl(config, It.IsAny<string>())).Returns("http://tvh/channels");
        urlBuilder.Setup(x => x.MaskSensitiveData(It.IsAny<string>(), config)).Returns("http://tvh/channels");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var result = await sut.GetChannelsAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenHttpNonSuccess_ReturnsEmpty()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("upstream failed")
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns("http://tvh/channels");
        urlBuilder.Setup(x => x.BuildResourceUrl(config, It.IsAny<string>())).Returns("http://tvh/channels");
        urlBuilder.Setup(x => x.MaskSensitiveData(It.IsAny<string>(), config)).Returns("http://tvh/channels");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var result = await sut.GetChannelsAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenConfigMissing_ReturnsEmpty()
    {
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns((PluginConfiguration?)null);
        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);

        var result = await sut.GetChannelsAsync(CancellationToken.None);

        Assert.Empty(result);
        api.Verify(x => x.GetCurrentConfiguration(), Times.Once);
    }

    [Fact]
    public async Task GetChannelsAsync_WithWhitespaceIcon_DoesNotBuildImageProxyUrl()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                                        { "entries": [ { "uuid": "ch-1", "name": "One", "number": 1, "icon_public_url": "   ", "enabled": true } ], "total": 1 }
                                        """)
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));
        urlBuilder.Setup(x => x.BuildResourceUrl(config, It.IsAny<string>())).Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));
        urlBuilder.Setup(x => x.MaskSensitiveData(It.IsAny<string>(), config)).Returns<string, PluginConfiguration>((value, _) => value);

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var result = (await sut.GetChannelsAsync(CancellationToken.None)).Single();

        Assert.False(result.HasImage);
        Assert.Null(result.ImageUrl);
        urlBuilder.Verify(x => x.BuildResourceUrl(config, It.Is<string>(endpoint => endpoint.StartsWith("imagecache/", StringComparison.Ordinal))), Times.Never);
    }

    [Fact]
    public async Task GetChannelsAsync_WithInvalidJson_ReturnsEmpty()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ not-json")
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns("http://tvh/channels");
        urlBuilder.Setup(x => x.BuildResourceUrl(config, It.IsAny<string>())).Returns("http://tvh/channels");
        urlBuilder.Setup(x => x.MaskSensitiveData(It.IsAny<string>(), config)).Returns("http://tvh/channels");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var result = await sut.GetChannelsAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetProgramsAsync_WithEmptyChannelId_ThrowsArgumentException()
    {
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.GetProgramsAsync(string.Empty, DateTime.UtcNow, DateTime.UtcNow.AddHours(1), CancellationToken.None));
    }

    [Fact]
    public async Task GetProgramsAsync_FiltersByChannelAndDateRange()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        var startUtc = new DateTime(2026, 4, 8, 20, 0, 0, DateTimeKind.Utc);
        var endUtc = startUtc.AddHours(1);
        var eventStart = new DateTimeOffset(startUtc.AddMinutes(10)).ToUnixTimeSeconds();
        var eventStop = new DateTimeOffset(startUtc.AddMinutes(40)).ToUnixTimeSeconds();

        var payload = JsonSerializer.Serialize(new
        {
            Entries = new object[]
            {
                new { EventId = 1, ChannelUuid = "ch-1", Title = "Keep", Start = eventStart, Stop = eventStop, Genre = new[] { 16 }, Hd = 1 },
                new { EventId = 2, ChannelUuid = "ch-2", Title = "Drop channel", Start = eventStart, Stop = eventStop, Genre = new[] { 16 }, Hd = 1 },
                new { EventId = 3, ChannelUuid = "ch-1", Title = "Drop time", Start = new DateTimeOffset(endUtc).ToUnixTimeSeconds(), Stop = new DateTimeOffset(endUtc.AddMinutes(30)).ToUnixTimeSeconds(), Genre = new[] { 16 }, Hd = 1 }
            }
        });

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload)
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");
        urlBuilder.Setup(x => x.BuildResourceUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var result = (await sut.GetProgramsAsync("ch-1", startUtc, endUtc, CancellationToken.None)).ToList();

        Assert.Single(result);
        Assert.Equal("1", result[0].Id);
        Assert.Equal("ch-1", result[0].ChannelId);
    }

    [Fact]
    public async Task GetProgramsAsync_EncodesChannelIdInEndpoint()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        var startUtc = new DateTime(2026, 4, 8, 20, 0, 0, DateTimeKind.Utc);
        var endUtc = startUtc.AddHours(1);

        var payload = JsonSerializer.Serialize(new
        {
            Entries = new object[]
            {
                new { EventId = 1, ChannelUuid = "ch/1", Title = "Keep", Start = new DateTimeOffset(startUtc).ToUnixTimeSeconds(), Stop = new DateTimeOffset(endUtc).ToUnixTimeSeconds(), Genre = new[] { 16 } }
            }
        });

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload)
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder
            .Setup(x => x.BuildApiUrl(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));
        urlBuilder.Setup(x => x.BuildResourceUrl(config, It.IsAny<string>())).Returns("http://tvh/image");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var result = (await sut.GetProgramsAsync("ch/1", startUtc, endUtc, CancellationToken.None)).ToList();

        Assert.Single(result);
        urlBuilder.Verify(
            x => x.BuildApiUrl(
                config,
                It.Is<string>(endpoint => endpoint.Contains("channel=ch%2F1", StringComparison.Ordinal))),
            Times.Once);
    }

    [Fact]
    public async Task GetProgramsAsync_ImageCachePath_UsesImageProxyUrl()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        var startUtc = new DateTime(2026, 4, 8, 20, 0, 0, DateTimeKind.Utc);
        var endUtc = startUtc.AddHours(1);

        var payload = JsonSerializer.Serialize(new
        {
            Entries = new object[]
            {
                new { EventId = 11, ChannelUuid = "ch-1", Title = "Image", Start = new DateTimeOffset(startUtc).ToUnixTimeSeconds(), Stop = new DateTimeOffset(endUtc).ToUnixTimeSeconds(), Image = "imagecache/poster 1.png", Genre = new[] { 16 } }
            }
        });

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload)
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder
            .Setup(x => x.BuildApiUrl(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));
        urlBuilder
            .Setup(x => x.BuildResourceUrl(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var program = (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();

        Assert.Equal("http://jellyfin:8096/api/tvheadend/images/imagecache/poster 1.png", program.ImageUrl);
        Assert.True(program.HasImage);
    }

    [Fact]
    public async Task GetProgramsAsync_ChannelIconFallback_UsesImageProxyUrlWhenImageMissing()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        var startUtc = new DateTime(2026, 4, 8, 20, 0, 0, DateTimeKind.Utc);
        var endUtc = startUtc.AddHours(1);

        var payload = JsonSerializer.Serialize(new
        {
            Entries = new object[]
            {
                new { EventId = 21, ChannelUuid = "ch-1", Title = "Fallback", Start = new DateTimeOffset(startUtc).ToUnixTimeSeconds(), Stop = new DateTimeOffset(endUtc).ToUnixTimeSeconds(), Image = string.Empty, ChannelIcon = "imagecache/ch1.png", Genre = new[] { 16 } }
            }
        });

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload)
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder
            .Setup(x => x.BuildApiUrl(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));
        urlBuilder
            .Setup(x => x.BuildResourceUrl(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var program = (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();

        Assert.Equal("http://jellyfin:8096/api/tvheadend/images/imagecache/ch1.png", program.ImageUrl);
        Assert.True(program.HasImage);
    }

    [Fact]
    public async Task GetProgramsAsync_MapsGenresAndCategoryFlags()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        var startUtc = new DateTime(2026, 4, 8, 20, 0, 0, DateTimeKind.Utc);
        var endUtc = startUtc.AddHours(1);

        var payload = JsonSerializer.Serialize(new
        {
            Entries = new object[]
            {
                new { EventId = 31, ChannelUuid = "ch-1", Title = "Flags", Start = new DateTimeOffset(startUtc).ToUnixTimeSeconds(), Stop = new DateTimeOffset(endUtc).ToUnixTimeSeconds(), Genre = new[] { 20, 33, 67, 81, 49, 999 } }
            }
        });

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload)
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");
        urlBuilder.Setup(x => x.BuildResourceUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var program = (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();

        Assert.Contains("Comedy", program.Genres);
        Assert.Contains("News/Weather Report", program.Genres);
        Assert.Contains("Football/Soccer", program.Genres);
        Assert.Contains("Pre-school Children's Programs", program.Genres);
        Assert.Contains("Game Show/Quiz/Contest", program.Genres);
        Assert.Contains("Unknown (999)", program.Genres);
        Assert.True(program.IsNews);
        Assert.True(program.IsSports);
        Assert.True(program.IsKids);
        Assert.True(program.IsSeries);
    }

    [Fact]
    public async Task GetProgramsAsync_BoundaryEventsAreExcluded()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        var startUtc = new DateTime(2026, 4, 8, 20, 0, 0, DateTimeKind.Utc);
        var endUtc = startUtc.AddHours(1);

        var payload = JsonSerializer.Serialize(new
        {
            Entries = new object[]
            {
                new { EventId = 41, ChannelUuid = "ch-1", Title = "Start equals end", Start = new DateTimeOffset(endUtc).ToUnixTimeSeconds(), Stop = new DateTimeOffset(endUtc.AddMinutes(30)).ToUnixTimeSeconds(), Genre = new[] { 16 } },
                new { EventId = 42, ChannelUuid = "ch-1", Title = "Stop equals start", Start = new DateTimeOffset(startUtc.AddMinutes(-30)).ToUnixTimeSeconds(), Stop = new DateTimeOffset(startUtc).ToUnixTimeSeconds(), Genre = new[] { 16 } }
            }
        });

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload)
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");
        urlBuilder.Setup(x => x.BuildResourceUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var result = await sut.GetProgramsAsync("ch-1", startUtc, endUtc, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetProgramsAsync_ExternalImageUrl_IsKeptAsIs()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        var startUtc = new DateTime(2026, 4, 8, 20, 0, 0, DateTimeKind.Utc);
        var endUtc = startUtc.AddHours(1);
        const string externalImage = "https://cdn.example/poster.jpg";

        var payload = JsonSerializer.Serialize(new
        {
            Entries = new object[]
            {
                new { EventId = 51, ChannelUuid = "ch-1", Title = "External image", Start = new DateTimeOffset(startUtc).ToUnixTimeSeconds(), Stop = new DateTimeOffset(endUtc).ToUnixTimeSeconds(), Image = externalImage, Genre = new[] { 16 } }
            }
        });

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload)
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");
        urlBuilder.Setup(x => x.BuildResourceUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var program = (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();

        Assert.Equal(externalImage, program.ImageUrl);
        Assert.True(program.HasImage);
        urlBuilder.Verify(x => x.BuildResourceUrl(config, It.Is<string>(endpoint => endpoint.Contains("imagecache/", StringComparison.Ordinal))), Times.Never);
    }

    [Fact]
    public async Task GetProgramsAsync_WhenHttpNonSuccess_ReturnsEmpty()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("bad gateway")
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, "api/epg/content_type/list")).Returns("http://tvh/epg");
        urlBuilder.Setup(x => x.BuildResourceUrl(config, "api/epg/content_type/list")).Returns("http://tvh/epg");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var result = await sut.GetProgramsAsync("ch-1", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1), CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetContentTypesAsync_WhenConfigMissing_ReturnsEmpty()
    {
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns((PluginConfiguration?)null);
        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);

        var result = await sut.GetContentTypesAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetContentTypesAsync_WithSuccessResponse_ReturnsMappedDictionary()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"entries\":[{\"key\":16,\"val\":\"Movie/Drama\"}]}")
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, "api/epg/content_type/list")).Returns("http://tvh/content-types");
        urlBuilder.Setup(x => x.BuildResourceUrl(config, "api/epg/content_type/list")).Returns("http://tvh/content-types");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var result = await sut.GetContentTypesAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Movie/Drama", result[16]);
    }

    [Fact]
    public async Task GetContentTypesAsync_WithNonSuccessStatus_ReturnsEmpty()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("bad")
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, "api/epg/content_type/list")).Returns("http://tvh/content-types");
        urlBuilder.Setup(x => x.BuildResourceUrl(config, "api/epg/content_type/list")).Returns("http://tvh/content-types");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var result = await sut.GetContentTypesAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetChannelTagsAsync_WithSuccessResponse_ReturnsMappedDictionary()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"entries\":[{\"key\":\"tag-1\",\"val\":\"HDTV\"}]}")
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, "api/channeltag/list")).Returns("http://tvh/channel-tags");
        urlBuilder.Setup(x => x.BuildResourceUrl(config, "api/channeltag/list")).Returns("http://tvh/channel-tags");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var result = await sut.GetChannelTagsAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("HDTV", result["tag-1"]);
    }

    [Fact]
    public async Task GetChannelTagsAsync_WithBrokenJson_ReturnsEmpty()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not-json")
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, "api/channeltag/list")).Returns("http://tvh/channel-tags");
        urlBuilder.Setup(x => x.BuildResourceUrl(config, "api/channeltag/list")).Returns("http://tvh/channel-tags");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var result = await sut.GetChannelTagsAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetProgramsAsync_ImageCacheWithLeadingSlash_UsesImageProxyUrl()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        var startUtc = new DateTime(2026, 4, 8, 20, 0, 0, DateTimeKind.Utc);
        var endUtc = startUtc.AddHours(1);

        var payload = JsonSerializer.Serialize(new
        {
            Entries = new object[]
            {
                new { EventId = 61, ChannelUuid = "ch-1", Title = "Image", Start = new DateTimeOffset(startUtc).ToUnixTimeSeconds(), Stop = new DateTimeOffset(endUtc).ToUnixTimeSeconds(), Image = "/imagecache/poster.png", Genre = new[] { 16 } }
            },
            TotalCount = 1
        });

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload)
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder
            .Setup(x => x.BuildApiUrl(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));
        urlBuilder
            .Setup(x => x.BuildResourceUrl(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var program = (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();

        Assert.Equal("http://jellyfin:8096/api/tvheadend/images/imagecache/poster.png", program.ImageUrl);
        Assert.True(program.HasImage);
    }

    [Fact]
    public async Task GetProgramsAsync_ChannelIconRelativePath_UsesImageProxyUrlFallback()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        var startUtc = new DateTime(2026, 4, 8, 20, 0, 0, DateTimeKind.Utc);
        var endUtc = startUtc.AddHours(1);

        var payload = JsonSerializer.Serialize(new
        {
            Entries = new object[]
            {
                new { EventId = 71, ChannelUuid = "ch-1", Title = "Fallback", Start = new DateTimeOffset(startUtc).ToUnixTimeSeconds(), Stop = new DateTimeOffset(endUtc).ToUnixTimeSeconds(), Image = string.Empty, ChannelIcon = "/imagecache/ch-fallback.png", Genre = new[] { 16 } }
            },
            TotalCount = 1
        });

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload)
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder
            .Setup(x => x.BuildApiUrl(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));
        urlBuilder
            .Setup(x => x.BuildResourceUrl(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var program = (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();

        Assert.Equal("http://jellyfin:8096/api/tvheadend/images/imagecache/ch-fallback.png", program.ImageUrl);
        Assert.True(program.HasImage);
    }

    [Fact]
    public async Task GetProgramsAsync_RatingLabelIcon_MapsToLogoImageUrl()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        var startUtc = new DateTime(2026, 4, 8, 20, 0, 0, DateTimeKind.Utc);
        var endUtc = startUtc.AddHours(1);

        var payload = JsonSerializer.Serialize(new
        {
            Entries = new object[]
            {
                new
                {
                    EventId = 81,
                    ChannelUuid = "ch-1",
                    Title = "Rated",
                    Start = new DateTimeOffset(startUtc).ToUnixTimeSeconds(),
                    Stop = new DateTimeOffset(endUtc).ToUnixTimeSeconds(),
                    Image = "imagecache/poster.png",
                    RatingLabel = "PG",
                    RatingLabelIcon = "/imagecache/rating-pg.png",
                    Genre = new[] { 16 }
                }
            },
            TotalCount = 1
        });

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload)
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder
            .Setup(x => x.BuildApiUrl(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));
        urlBuilder
            .Setup(x => x.BuildResourceUrl(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var program = (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();

        Assert.Equal("http://jellyfin:8096/api/tvheadend/images/imagecache/poster.png", program.ImageUrl);
        Assert.Equal("http://jellyfin:8096/api/tvheadend/images/imagecache/rating-pg.png", program.LogoImageUrl);
        Assert.True(program.HasImage);
    }

    [Fact]
    public async Task GetProgramsAsync_MetadataEnrichmentDisabled_DoesNotAddProviderHints()
    {
        var config = new PluginConfiguration { EnableJellyfinMetadataEnrichment = false };
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        var startUtc = new DateTime(2026, 4, 8, 20, 0, 0, DateTimeKind.Utc);
        var endUtc = startUtc.AddHours(1);

        var payload = JsonSerializer.Serialize(new
        {
            Entries = new object[]
            {
                new
                {
                    EventId = 91,
                    ChannelUuid = "ch-1",
                    Title = "No hints",
                    Start = new DateTimeOffset(startUtc).ToUnixTimeSeconds(),
                    Stop = new DateTimeOffset(endUtc).ToUnixTimeSeconds(),
                    EpisodeUri = "tt1234567",
                    Genre = new[] { 16 }
                }
            },
            TotalCount = 1
        });

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload)
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");
        urlBuilder.Setup(x => x.BuildResourceUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var program = (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();

        Assert.Empty(program.ProviderIds);
        Assert.Empty(program.SeriesProviderIds);
    }

    [Fact]
    public async Task GetProgramsAsync_MetadataEnrichmentEnabled_AddsProviderHintsFromUris()
    {
        var config = new PluginConfiguration { EnableJellyfinMetadataEnrichment = true };
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        var startUtc = new DateTime(2026, 4, 8, 20, 0, 0, DateTimeKind.Utc);
        var endUtc = startUtc.AddHours(1);

        var payload = JsonSerializer.Serialize(new
        {
            Entries = new object[]
            {
                new
                {
                    EventId = 101,
                    ChannelUuid = "ch-1",
                    Title = "Hints",
                    Start = new DateTimeOffset(startUtc).ToUnixTimeSeconds(),
                    Stop = new DateTimeOffset(endUtc).ToUnixTimeSeconds(),
                    EpisodeUri = "crid://example/tt7654321",
                    SerieslinkUri = "tmdb://tv/12345",
                    Genre = new[] { 16 }
                }
            },
            TotalCount = 1
        });

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload)
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");
        urlBuilder.Setup(x => x.BuildResourceUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var program = (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();

        Assert.Equal("tt7654321", program.ProviderIds["Imdb"]);
        Assert.Equal("12345", program.SeriesProviderIds["Tmdb"]);
    }

    [Fact]
    public async Task GetChannelsAsync_WithBouquet_ResolvesBouquetNameAsChannelGroup()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();

        // TVHeadend's channel grid carries the bouquet's opaque idnode UUID, not its name.
        var json = """
                   {
                     "entries": [
                       { "uuid": "ch-1", "name": "BBC One", "number": 1, "enabled": true, "bouquet": "de1a9c4fb033626f7e8e08d4c04083ea", "tags": ["tag-1"] }
                     ],
                     "total": 1
                   }
                   """;

        // Tag and bouquet lookups use separate HTTP calls; use a multi-response handler.
        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"entries":[{"key":"tag-1","val":"Entertainment"}]}""") });
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"entries":[{"uuid":"de1a9c4fb033626f7e8e08d4c04083ea","name":"Freeview"}],"total":1}""") });

        var handler = new QueueResponseHandler(responses);

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        // A fresh client per call: three sequential requests are made (grid, tags, bouquets)
        // and the service disposes each client after use.
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(() => new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns<PluginConfiguration, string>((_, ep) => "http://tvh/" + ep.TrimStart('/'));
        urlBuilder.Setup(x => x.BuildResourceUrl(config, It.IsAny<string>())).Returns<PluginConfiguration, string>((_, ep) => "http://tvh/" + ep.TrimStart('/'));
        urlBuilder.Setup(x => x.MaskSensitiveData(It.IsAny<string>(), config)).Returns<string, PluginConfiguration>((v, _) => v);

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var result = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();

        Assert.Single(result);
        Assert.Equal("Freeview", result[0].ChannelGroup);
        urlBuilder.Verify(x => x.BuildApiUrl(config, It.Is<string>(ep => ep.StartsWith("api/bouquet/grid", StringComparison.Ordinal))), Times.Once);
    }

    [Fact]
    public async Task GetChannelsAsync_WithUnresolvableBouquet_FallsBackToFirstTagName()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();

        var json = """
                   {
                     "entries": [
                       { "uuid": "ch-1", "name": "BBC One", "number": 1, "enabled": true, "bouquet": "bq-unknown", "tags": ["tag-1"] }
                     ],
                     "total": 1
                   }
                   """;

        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"entries":[{"key":"tag-1","val":"Entertainment"}]}""") });
        // Bouquet grid does not know the UUID → fall back to the tag name, never the raw UUID.
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"entries":[],"total":0}""") });

        var handler = new QueueResponseHandler(responses);

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(() => new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns<PluginConfiguration, string>((_, ep) => "http://tvh/" + ep.TrimStart('/'));
        urlBuilder.Setup(x => x.MaskSensitiveData(It.IsAny<string>(), config)).Returns<string, PluginConfiguration>((v, _) => v);

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var result = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();

        Assert.Single(result);
        Assert.Equal("Entertainment", result[0].ChannelGroup);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenBouquetLookupFailsAndNoTags_ChannelGroupIsNull()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();

        var json = """
                   {
                     "entries": [
                       { "uuid": "ch-1", "name": "BBC One", "number": 1, "enabled": true, "bouquet": "bq-1" }
                     ],
                     "total": 1
                   }
                   """;

        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"entries":[]}""") });
        // The queue is now empty: the bouquet grid request receives HTTP 500,
        // which must degrade to "no bouquet names" — not to a raw UUID group.
        var handler = new QueueResponseHandler(responses);

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(() => new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns<PluginConfiguration, string>((_, ep) => "http://tvh/" + ep.TrimStart('/'));
        urlBuilder.Setup(x => x.MaskSensitiveData(It.IsAny<string>(), config)).Returns<string, PluginConfiguration>((v, _) => v);

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var result = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();

        Assert.Single(result);
        Assert.Null(result[0].ChannelGroup);
    }

    [Fact]
    public async Task GetChannelsAsync_WithoutBouquets_DoesNotFetchBouquetGrid()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();

        var json = """{"entries": [{ "uuid": "ch-1", "name": "BBC One", "number": 1, "enabled": true, "tags": ["tag-1"] }], "total": 1}""";

        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"entries":[{"key":"tag-1","val":"Entertainment"}]}""") });
        var handler = new QueueResponseHandler(responses);

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns<PluginConfiguration, string>((_, ep) => "http://tvh/" + ep.TrimStart('/'));
        urlBuilder.Setup(x => x.MaskSensitiveData(It.IsAny<string>(), config)).Returns<string, PluginConfiguration>((v, _) => v);

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var result = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();

        Assert.Single(result);
        Assert.Equal("Entertainment", result[0].ChannelGroup);
        urlBuilder.Verify(x => x.BuildApiUrl(config, It.Is<string>(ep => ep.StartsWith("api/bouquet/grid", StringComparison.Ordinal))), Times.Never);
    }

    [Fact]
    public async Task GetChannelsAsync_WithMultipleBouquetChannels_FetchesBouquetGridOnce()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();

        var json = """
                   {
                     "entries": [
                       { "uuid": "ch-1", "name": "One", "number": 1, "enabled": true, "bouquet": "bq-1" },
                       { "uuid": "ch-2", "name": "Two", "number": 2, "enabled": true, "bouquet": "bq-2" }
                     ],
                     "total": 2
                   }
                   """;

        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"entries":[]}""") });
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"entries":[{"uuid":"bq-1","name":"Sky"},{"uuid":"bq-2","name":"Freesat"}],"total":2}""") });
        var handler = new QueueResponseHandler(responses);

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(() => new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns<PluginConfiguration, string>((_, ep) => "http://tvh/" + ep.TrimStart('/'));
        urlBuilder.Setup(x => x.MaskSensitiveData(It.IsAny<string>(), config)).Returns<string, PluginConfiguration>((v, _) => v);

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var result = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal("Sky", result[0].ChannelGroup);
        Assert.Equal("Freesat", result[1].ChannelGroup);
        urlBuilder.Verify(x => x.BuildApiUrl(config, It.Is<string>(ep => ep.StartsWith("api/bouquet/grid", StringComparison.Ordinal))), Times.Once);
    }

    [Fact]
    public async Task GetChannelsAsync_WithTagsAndTagNames_ResolvesTagNamesFromDictionary()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();

        var channelJson = """
                          {
                            "entries": [
                              { "uuid": "ch-1", "name": "BBC Two", "number": 2, "enabled": true, "tags": ["tag-abc"] }
                            ],
                            "total": 1
                          }
                          """;
        var tagJson = """{"entries":[{"key":"tag-abc","val":"Sports"}]}""";

        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(channelJson) });
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tagJson) });

        var handler = new QueueResponseHandler(responses);

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns<PluginConfiguration, string>((_, ep) => "http://tvh/" + ep.TrimStart('/'));
        urlBuilder.Setup(x => x.BuildResourceUrl(config, It.IsAny<string>())).Returns<PluginConfiguration, string>((_, ep) => "http://tvh/" + ep.TrimStart('/'));
        urlBuilder.Setup(x => x.MaskSensitiveData(It.IsAny<string>(), config)).Returns<string, PluginConfiguration>((v, _) => v);

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var result = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();

        Assert.Single(result);
        Assert.Contains("Sports", result[0].Tags!);
        Assert.Equal("Sports", result[0].ChannelGroup);
    }

    [Fact]
    public async Task GetChannelsAsync_WithUnknownTagId_KeepsRawTagId()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();

        var channelJson = """
                          {
                            "entries": [
                              { "uuid": "ch-1", "name": "ITV", "number": 3, "enabled": true, "tags": ["unknown-tag-99"] }
                            ],
                            "total": 1
                          }
                          """;
        // No matching tag in tag response
        var tagJson = """{"entries":[{"key":"tag-1","val":"News"}]}""";

        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(channelJson) });
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tagJson) });

        var handler = new QueueResponseHandler(responses);

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns<PluginConfiguration, string>((_, ep) => "http://tvh/" + ep.TrimStart('/'));
        urlBuilder.Setup(x => x.BuildResourceUrl(config, It.IsAny<string>())).Returns<PluginConfiguration, string>((_, ep) => "http://tvh/" + ep.TrimStart('/'));
        urlBuilder.Setup(x => x.MaskSensitiveData(It.IsAny<string>(), config)).Returns<string, PluginConfiguration>((v, _) => v);

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var result = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();

        Assert.Single(result);
        Assert.Contains("unknown-tag-99", result[0].Tags!);
    }

    [Fact]
    public async Task GetProgramsAsync_WithFirstAired_SetsOriginalAirDate()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        var startUtc = new DateTime(2026, 4, 8, 20, 0, 0, DateTimeKind.Utc);
        var endUtc = startUtc.AddHours(1);
        var firstAiredTs = new DateTimeOffset(new DateTime(2020, 1, 15, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var evStart = new DateTimeOffset(startUtc).ToUnixTimeSeconds();
        var evStop = new DateTimeOffset(endUtc).ToUnixTimeSeconds();

        var payload = $$"""
                        {"entries":[{"eventId":42,"channelUuid":"ch-1","title":"Old Show","start":{{evStart}},"stop":{{evStop}},"first_aired":{{firstAiredTs}}}],"totalCount":1}
                        """;

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");
        urlBuilder.Setup(x => x.BuildResourceUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var program = (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();

        Assert.NotNull(program.OriginalAirDate);
        Assert.Equal(2020, program.OriginalAirDate!.Value.Year);
    }

    [Fact]
    public async Task GetProgramsAsync_WithIsNewZero_SetsIsRepeat()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        var startUtc = new DateTime(2026, 4, 8, 20, 0, 0, DateTimeKind.Utc);
        var endUtc = startUtc.AddHours(1);
        var evStart = new DateTimeOffset(startUtc).ToUnixTimeSeconds();
        var evStop = new DateTimeOffset(endUtc).ToUnixTimeSeconds();

        var payload = $$"""
                        {"entries":[{"eventId":43,"channelUuid":"ch-1","title":"Repeat Show","start":{{evStart}},"stop":{{evStop}},"new":0}],"totalCount":1}
                        """;

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");
        urlBuilder.Setup(x => x.BuildResourceUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var program = (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();

        Assert.True(program.IsRepeat);
    }

    [Theory]
    [InlineData(1, "Mono")]
    [InlineData(2, "Stereo")]
    [InlineData(3, "Stereo")]
    [InlineData(4, "DolbyDigital")]
    [InlineData(0, null)]
    public async Task GetProgramsAsync_MapsProgramAudio(int stereoMode, string? expectedAudio)
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        var startUtc = new DateTime(2026, 4, 8, 20, 0, 0, DateTimeKind.Utc);
        var endUtc = startUtc.AddHours(1);

        var payload = JsonSerializer.Serialize(new
        {
            Entries = new object[]
            {
                new
                {
                    EventId = 50 + stereoMode,
                    ChannelUuid = "ch-1",
                    Title = "Audio Test",
                    Start = new DateTimeOffset(startUtc).ToUnixTimeSeconds(),
                    Stop = new DateTimeOffset(endUtc).ToUnixTimeSeconds(),
                    Stereo = stereoMode
                }
            },
            TotalCount = 1
        });

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");
        urlBuilder.Setup(x => x.BuildResourceUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var program = (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();

        Assert.Equal(expectedAudio, program.Audio?.ToString());
    }

    [Fact]
    public async Task GetProgramsAsync_WithCopyrightYear_SetsProductionYear()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        var startUtc = new DateTime(2026, 4, 8, 20, 0, 0, DateTimeKind.Utc);
        var endUtc = startUtc.AddHours(1);
        var evStart = new DateTimeOffset(startUtc).ToUnixTimeSeconds();
        var evStop = new DateTimeOffset(endUtc).ToUnixTimeSeconds();

        var payload = $$"""
                        {"entries":[{"eventId":99,"channelUuid":"ch-1","title":"Classic Film","start":{{evStart}},"stop":{{evStop}},"copyright_year":1984}],"totalCount":1}
                        """;

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");
        urlBuilder.Setup(x => x.BuildResourceUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var program = (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();

        Assert.Equal(1984, program.ProductionYear);
    }

    [Fact]
    public async Task GetProgramsAsync_WithAbsoluteEpisodeUri_SetsHomePageUrl()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        var startUtc = new DateTime(2026, 4, 8, 20, 0, 0, DateTimeKind.Utc);
        var endUtc = startUtc.AddHours(1);

        var payload = JsonSerializer.Serialize(new
        {
            Entries = new object[]
            {
                new
                {
                    EventId = 77,
                    ChannelUuid = "ch-1",
                    Title = "Web Show",
                    Start = new DateTimeOffset(startUtc).ToUnixTimeSeconds(),
                    Stop = new DateTimeOffset(endUtc).ToUnixTimeSeconds(),
                    EpisodeUri = "https://example.com/show/123"
                }
            },
            TotalCount = 1
        });

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload)
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");
        urlBuilder.Setup(x => x.BuildResourceUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var program = (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();

        Assert.Equal("https://example.com/show/123", program.HomePageUrl);
    }

    [Fact]
    public async Task GetProgramsAsync_MetadataEnrichment_TvdbAndTmdbMovieProviderIds()
    {
        var config = new PluginConfiguration { EnableJellyfinMetadataEnrichment = true };
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        var startUtc = new DateTime(2026, 4, 8, 20, 0, 0, DateTimeKind.Utc);
        var endUtc = startUtc.AddHours(1);

        var payload = JsonSerializer.Serialize(new
        {
            Entries = new object[]
            {
                new
                {
                    EventId = 88,
                    ChannelUuid = "ch-1",
                    Title = "TVDb Movie",
                    Start = new DateTimeOffset(startUtc).ToUnixTimeSeconds(),
                    Stop = new DateTimeOffset(endUtc).ToUnixTimeSeconds(),
                    EpisodeUri = "tvdb://series/456",
                    SerieslinkUri = "tmdb://movie/789"
                }
            },
            TotalCount = 1
        });

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload)
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");
        urlBuilder.Setup(x => x.BuildResourceUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var program = (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();

        Assert.Equal("456", program.ProviderIds["Tvdb"]);
        Assert.Equal("789", program.SeriesProviderIds["Tmdb"]);
    }

    [Fact]
    public async Task GetChannelTagsAsync_WithNonSuccessResponse_ReturnsEmpty()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();

        // channel grid succeeds but tag lookup fails
        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"entries":[{"uuid":"ch-1","name":"BBC","number":1000000,"enabled":true}],"total":1}""") });
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("unavailable") });

        var handler = new QueueResponseHandler(responses);

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns<PluginConfiguration, string>((_, ep) => "http://tvh/" + ep.TrimStart('/'));
        urlBuilder.Setup(x => x.BuildResourceUrl(config, It.IsAny<string>())).Returns<PluginConfiguration, string>((_, ep) => "http://tvh/" + ep.TrimStart('/'));
        urlBuilder.Setup(x => x.MaskSensitiveData(It.IsAny<string>(), config)).Returns<string, PluginConfiguration>((v, _) => v);

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var result = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();

        // Channel should still be returned, tags will be empty
        Assert.Single(result);
        Assert.Empty(result[0].Tags!);
    }

    [Fact]
    public async Task GetContentTypesAsync_WithBrokenJson_ReturnsEmpty()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not-json-at-all") });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns("http://tvh/content_type");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var result = await sut.GetContentTypesAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetChannelTagsAsync_WhenConfigMissing_ReturnsEmpty()
    {
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns((PluginConfiguration?)null);
        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);

        var result = await sut.GetChannelTagsAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    // ── Constructor null-guard tests ──

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new GuideService(null!, new Mock<IApiClient>().Object, new Mock<IUrlBuilder>().Object, StubRelay(), NullHealthService.Instance));
    }

    [Fact]
    public void Constructor_NullApiClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new GuideService(NullLogger<GuideService>.Instance, null!, new Mock<IUrlBuilder>().Object, StubRelay(), NullHealthService.Instance));
    }

    [Fact]
    public void Constructor_NullUrlBuilder_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new GuideService(NullLogger<GuideService>.Instance, new Mock<IApiClient>().Object, null!, StubRelay(), NullHealthService.Instance));
    }

    [Fact]
    public void Constructor_NullRelayUrlBuilder_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new GuideService(NullLogger<GuideService>.Instance, new Mock<IApiClient>().Object, new Mock<IUrlBuilder>().Object, null!, NullHealthService.Instance));
    }

    [Fact]
    public void Constructor_NullHealthService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new GuideService(NullLogger<GuideService>.Instance, new Mock<IApiClient>().Object, new Mock<IUrlBuilder>().Object, StubRelay(), null!));
    }

    // ── Circuit breaker tests ──

    [Fact]
    public async Task GetChannelsAsync_WhenCircuitBreakerOpen_ReturnsEmpty()
    {
        var health = new Mock<IHealthService>();
        health.Setup(x => x.ShouldBlockRequest()).Returns(true);
        var sut = new GuideService(NullLogger<GuideService>.Instance, new Mock<IApiClient>().Object, new Mock<IUrlBuilder>().Object, StubRelay(), health.Object);

        var result = await sut.GetChannelsAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetProgramsAsync_WhenCircuitBreakerOpen_ReturnsEmpty()
    {
        var health = new Mock<IHealthService>();
        health.Setup(x => x.ShouldBlockRequest()).Returns(true);
        var sut = new GuideService(NullLogger<GuideService>.Instance, new Mock<IApiClient>().Object, new Mock<IUrlBuilder>().Object, StubRelay(), health.Object);

        var result = await sut.GetProgramsAsync("ch-1", DateTime.UtcNow, DateTime.UtcNow.AddHours(1), CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetContentTypesAsync_WhenCircuitBreakerOpen_ReturnsEmpty()
    {
        var health = new Mock<IHealthService>();
        health.Setup(x => x.ShouldBlockRequest()).Returns(true);
        var sut = new GuideService(NullLogger<GuideService>.Instance, new Mock<IApiClient>().Object, new Mock<IUrlBuilder>().Object, StubRelay(), health.Object);

        var result = await sut.GetContentTypesAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetChannelTagsAsync_WhenCircuitBreakerOpen_ReturnsEmpty()
    {
        var health = new Mock<IHealthService>();
        health.Setup(x => x.ShouldBlockRequest()).Returns(true);
        var sut = new GuideService(NullLogger<GuideService>.Instance, new Mock<IApiClient>().Object, new Mock<IUrlBuilder>().Object, StubRelay(), health.Object);

        var result = await sut.GetChannelTagsAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    // ── FormatChannelNumber edge cases ──

    [Fact]
    public void FormatChannelNumber_UndefinedJsonElement_ReturnsZero()
    {
        var element = default(JsonElement);
        Assert.Equal("0", GuideService.FormatChannelNumber(element));
    }

    [Fact]
    public void FormatChannelNumber_NumberElement_ReturnsIntString()
    {
        using var doc = JsonDocument.Parse("42");
        Assert.Equal("42", GuideService.FormatChannelNumber(doc.RootElement));
    }

    [Fact]
    public void FormatChannelNumber_StringElement_ReturnsString()
    {
        using var doc = JsonDocument.Parse("\"7.1\"");
        Assert.Equal("7.1", GuideService.FormatChannelNumber(doc.RootElement));
    }

    [Fact]
    public void FormatChannelNumber_EmptyStringElement_ReturnsZero()
    {
        using var doc = JsonDocument.Parse("\"\"");
        Assert.Equal("0", GuideService.FormatChannelNumber(doc.RootElement));
    }

    [Fact]
    public void FormatChannelNumber_WhitespaceStringElement_ReturnsZero()
    {
        using var doc = JsonDocument.Parse("\"  \"");
        Assert.Equal("0", GuideService.FormatChannelNumber(doc.RootElement));
    }

    [Fact]
    public void FormatChannelNumber_NullElement_ReturnsZero()
    {
        using var doc = JsonDocument.Parse("null");
        Assert.Equal("0", GuideService.FormatChannelNumber(doc.RootElement));
    }

    [Fact]
    public void FormatChannelNumber_BooleanElement_ReturnsZero()
    {
        using var doc = JsonDocument.Parse("true");
        Assert.Equal("0", GuideService.FormatChannelNumber(doc.RootElement));
    }

    // ── Disabled channels filtered out ──

    [Fact]
    public async Task GetChannelsAsync_DisabledChannels_AreFilteredOut()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();

        var json = """
                   {
                     "entries": [
                       { "uuid": "ch-1", "name": "Active", "number": 1, "enabled": true },
                       { "uuid": "ch-2", "name": "Disabled", "number": 2, "enabled": false }
                     ],
                     "total": 2
                   }
                   """;

        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"entries":[]}""") });
        var handler = new QueueResponseHandler(responses);

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns<PluginConfiguration, string>((_, ep) => "http://tvh/" + ep.TrimStart('/'));
        urlBuilder.Setup(x => x.MaskSensitiveData(It.IsAny<string>(), config)).Returns<string, PluginConfiguration>((v, _) => v);

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var result = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();

        Assert.Single(result);
        Assert.Equal("ch-1", result[0].Id);
    }

    // ── GetProgramsAsync null channelId ──

    [Fact]
    public async Task GetProgramsAsync_NullChannelId_ThrowsArgumentException()
    {
        var sut = new GuideService(NullLogger<GuideService>.Instance, new Mock<IApiClient>().Object, new Mock<IUrlBuilder>().Object, StubRelay(), NullHealthService.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            sut.GetProgramsAsync(null!, DateTime.UtcNow, DateTime.UtcNow.AddHours(1), CancellationToken.None));
    }

    // ── Category flag tests ──

    [Fact]
    public async Task GetProgramsAsync_WithPremiereCategory_SetsIsPremiereAndNotRepeat()
    {
        var program = await BuildProgramWithCategories("Premiere");

        Assert.True(program.IsPremiere);
        Assert.False(program.IsRepeat);
    }

    [Fact]
    public async Task GetProgramsAsync_WithLiveCategory_SetsIsLive()
    {
        var program = await BuildProgramWithCategories("Live");

        Assert.True(program.IsLive);
    }

    [Fact]
    public async Task GetProgramsAsync_WithRepeatCategory_SetsIsRepeat()
    {
        var program = await BuildProgramWithCategories("repeat");

        Assert.True(program.IsRepeat);
        Assert.False(program.IsPremiere);
    }

    [Fact]
    public async Task GetProgramsAsync_WithWiederholungCategory_SetsIsRepeat()
    {
        var program = await BuildProgramWithCategories("Wiederholung");

        Assert.True(program.IsRepeat);
    }

    [Fact]
    public async Task GetProgramsAsync_WithFirstRunCategory_SetsIsPremiere()
    {
        var program = await BuildProgramWithCategories("first run");

        Assert.True(program.IsPremiere);
        Assert.False(program.IsRepeat);
    }

    [Fact]
    public async Task GetProgramsAsync_WithNewCategory_SetsIsPremiere()
    {
        var program = await BuildProgramWithCategories("new");

        Assert.True(program.IsPremiere);
    }

    // ── IsSeries / SeriesId / ShowId via SerieslinkUri ──

    [Fact]
    public async Task GetProgramsAsync_WithSerieslinkUri_SetsIsSeriesAndSeriesId()
    {
        var program = await BuildProgramWithFields(serieslinkUri: "crid://example/series/123");

        Assert.True(program.IsSeries);
        Assert.Equal("crid://example/series/123", program.SeriesId);
        Assert.Equal("crid://example/series/123", program.ShowId);
    }

    [Fact]
    public async Task GetProgramsAsync_WithoutSerieslinkUri_NoGenre_IsSeriesFalse()
    {
        var program = await BuildProgramWithFields(genres: new[] { 16 });

        Assert.False(program.IsSeries);
        Assert.Null(program.SeriesId);
        Assert.Null(program.ShowId);
    }

    // ── HomePageUrl with non-absolute EpisodeUri ──

    [Fact]
    public async Task GetProgramsAsync_WithNonAbsoluteEpisodeUri_SetsHomePageUrlNull()
    {
        // "episode/456" is not an absolute URI, so HomePageUrl should be null
        var program = await BuildProgramWithFields(episodeUri: "episode/456");

        Assert.Null(program.HomePageUrl);
    }

    [Fact]
    public async Task GetProgramsAsync_WithCridEpisodeUri_SetsHomePageUrlNull()
    {
        // DVB CRIDs parse as absolute URIs but have no browser protocol handler,
        // so they must not surface as a clickable homepage link.
        var program = await BuildProgramWithFields(episodeUri: "crid://www.channel4.com/41408/013");

        Assert.Null(program.HomePageUrl);
    }

    [Fact]
    public async Task GetProgramsAsync_WithHttpEpisodeUri_SetsHomePageUrl()
    {
        var program = await BuildProgramWithFields(episodeUri: "http://example.com/show/9");

        Assert.Equal("http://example.com/show/9", program.HomePageUrl);
    }

    // ── No image and no channelIcon ──

    [Fact]
    public async Task GetProgramsAsync_WithNoImageOrChannelIcon_HasImageFalse()
    {
        var program = await BuildProgramWithFields();

        Assert.Null(program.ImageUrl);
        Assert.False(program.HasImage);
    }

    // ── FirstAired = 0 → OriginalAirDate null ──

    [Fact]
    public async Task GetProgramsAsync_WithFirstAiredZero_OriginalAirDateNull()
    {
        var program = await BuildProgramWithFields(firstAired: 0);

        Assert.Null(program.OriginalAirDate);
    }

    // ── CopyrightYear = 0 → ProductionYear null ──

    [Fact]
    public async Task GetProgramsAsync_WithCopyrightYearZero_ProductionYearNull()
    {
        var program = await BuildProgramWithFields();

        Assert.Null(program.ProductionYear);
    }

    // ── TryAddKnownProviderIds edge cases ──

    [Fact]
    public async Task GetProgramsAsync_MetadataEnrichment_ImdbTooShort_NotAdded()
    {
        var config = new PluginConfiguration { EnableJellyfinMetadataEnrichment = true };
        var program = await BuildProgramWithFields(episodeUri: "tt12345", config: config);

        Assert.False(program.ProviderIds.ContainsKey("Imdb"));
    }

    [Fact]
    public async Task GetProgramsAsync_MetadataEnrichment_TmdbNonDigitId_NotAdded()
    {
        var config = new PluginConfiguration { EnableJellyfinMetadataEnrichment = true };
        var program = await BuildProgramWithFields(serieslinkUri: "tmdb://movie/abc", config: config);

        Assert.False(program.SeriesProviderIds.ContainsKey("Tmdb"));
    }

    [Fact]
    public async Task GetProgramsAsync_MetadataEnrichment_TvdbNonDigitId_NotAdded()
    {
        var config = new PluginConfiguration { EnableJellyfinMetadataEnrichment = true };
        var program = await BuildProgramWithFields(episodeUri: "tvdb://series/abc", config: config);

        Assert.False(program.ProviderIds.ContainsKey("Tvdb"));
    }

    [Fact]
    public async Task GetProgramsAsync_MetadataEnrichment_EmptySource_NoProviderIds()
    {
        var config = new PluginConfiguration { EnableJellyfinMetadataEnrichment = true };
        var program = await BuildProgramWithFields(episodeUri: "", serieslinkUri: "", config: config);

        Assert.Empty(program.ProviderIds);
        Assert.Empty(program.SeriesProviderIds);
    }

    [Fact]
    public async Task GetProgramsAsync_MetadataEnrichment_WhitespaceSource_NoProviderIds()
    {
        var config = new PluginConfiguration { EnableJellyfinMetadataEnrichment = true };
        var program = await BuildProgramWithFields(episodeUri: "   ", serieslinkUri: "   ", config: config);

        Assert.Empty(program.ProviderIds);
        Assert.Empty(program.SeriesProviderIds);
    }

    // ── IsMovie / IsEducational flags ──

    [Fact]
    public async Task GetProgramsAsync_WithMovieGenre_SetsIsMovie()
    {
        var program = await BuildProgramWithFields(genres: new[] { 16 });

        Assert.True(program.IsMovie);
    }

    [Fact]
    public async Task GetProgramsAsync_WithEducationalGenre_SetsIsEducational()
    {
        var program = await BuildProgramWithFields(genres: new[] { 145 });

        Assert.True(program.IsEducational);
    }

    [Fact]
    public async Task GetProgramsAsync_WithNoGenre_AllFlagsFalse()
    {
        var program = await BuildProgramWithFields(genres: Array.Empty<int>());

        Assert.False(program.IsMovie);
        Assert.False(program.IsSports);
        Assert.False(program.IsNews);
        Assert.False(program.IsKids);
        Assert.False(program.IsEducational);
        Assert.Empty(program.Genres);
    }

    [Fact]
    public async Task GetProgramsAsync_WithNullGenre_AllFlagsFalse()
    {
        var program = await BuildProgramWithFields();

        Assert.False(program.IsMovie);
        Assert.False(program.IsSeries);
    }

    // ── HD flag ──

    [Fact]
    public async Task GetProgramsAsync_WithHdFlag_SetsIsHD()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        var startUtc = new DateTime(2026, 4, 8, 20, 0, 0, DateTimeKind.Utc);
        var endUtc = startUtc.AddHours(1);

        var payload = JsonSerializer.Serialize(new
        {
            Entries = new object[]
            {
                new
                {
                    EventId = 200,
                    ChannelUuid = "ch-1",
                    Title = "HD Show",
                    Start = new DateTimeOffset(startUtc).ToUnixTimeSeconds(),
                    Stop = new DateTimeOffset(endUtc).ToUnixTimeSeconds(),
                    Hd = 1
                }
            },
            TotalCount = 1
        });

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var program = (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();

        Assert.True(program.IsHD);
    }

    // ── Channel with no tags ──

    [Fact]
    public async Task GetChannelsAsync_WithNoTags_ReturnsEmptyTags()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();

        var json = """{"entries": [{ "uuid": "ch-1", "name": "No Tags", "number": 1, "enabled": true }], "total": 1}""";

        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"entries":[]}""") });
        var handler = new QueueResponseHandler(responses);

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns<PluginConfiguration, string>((_, ep) => "http://tvh/" + ep.TrimStart('/'));
        urlBuilder.Setup(x => x.MaskSensitiveData(It.IsAny<string>(), config)).Returns<string, PluginConfiguration>((v, _) => v);

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var result = (await sut.GetChannelsAsync(CancellationToken.None)).Single();

        Assert.Empty(result.Tags!);
        Assert.Null(result.ChannelGroup);
    }

    // ── Channel with whitespace tag IDs ──

    [Fact]
    public async Task GetChannelsAsync_WithWhitespaceTagIds_FiltersThemOut()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();

        var json = """{"entries": [{ "uuid": "ch-1", "name": "WS Tags", "number": 1, "enabled": true, "tags": ["", "  ", "valid-tag"] }], "total": 1}""";

        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"entries":[{"key":"valid-tag","val":"News"}]}""") });
        var handler = new QueueResponseHandler(responses);

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns<PluginConfiguration, string>((_, ep) => "http://tvh/" + ep.TrimStart('/'));
        urlBuilder.Setup(x => x.MaskSensitiveData(It.IsAny<string>(), config)).Returns<string, PluginConfiguration>((v, _) => v);

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var result = (await sut.GetChannelsAsync(CancellationToken.None)).Single();

        Assert.Single(result.Tags!);
        Assert.Contains("News", result.Tags!);
    }

    // ── Stereo mode null (no field) ──

    [Fact]
    public async Task GetProgramsAsync_WithNoStereoField_AudioIsNull()
    {
        var program = await BuildProgramWithFields();

        Assert.Null(program.Audio);
    }

    // ── EpisodeTitle / Subtitle ──

    [Fact]
    public async Task GetProgramsAsync_WithSubtitle_SetsEpisodeTitle()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        var startUtc = new DateTime(2026, 4, 8, 20, 0, 0, DateTimeKind.Utc);
        var endUtc = startUtc.AddHours(1);

        var payload = JsonSerializer.Serialize(new
        {
            Entries = new object[]
            {
                new
                {
                    EventId = 300,
                    ChannelUuid = "ch-1",
                    Title = "Main Title",
                    Subtitle = "Episode Subtitle",
                    Start = new DateTimeOffset(startUtc).ToUnixTimeSeconds(),
                    Stop = new DateTimeOffset(endUtc).ToUnixTimeSeconds()
                }
            },
            TotalCount = 1
        });

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var program = (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();

        Assert.Equal("Episode Subtitle", program.EpisodeTitle);
    }

    // ── RatingLabel → OfficialRating ──

    [Fact]
    public async Task GetProgramsAsync_WithRatingLabel_SetsOfficialRating()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        var startUtc = new DateTime(2026, 4, 8, 20, 0, 0, DateTimeKind.Utc);
        var endUtc = startUtc.AddHours(1);

        var payload = JsonSerializer.Serialize(new
        {
            Entries = new object[]
            {
                new
                {
                    EventId = 301,
                    ChannelUuid = "ch-1",
                    Title = "Rated Show",
                    RatingLabel = "R",
                    Start = new DateTimeOffset(startUtc).ToUnixTimeSeconds(),
                    Stop = new DateTimeOffset(endUtc).ToUnixTimeSeconds()
                }
            },
            TotalCount = 1
        });

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        var program = (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();

        Assert.Equal("R", program.OfficialRating);
    }

    // ── Helper methods for compact test setup ──

    private async Task<MediaBrowser.Controller.LiveTv.ProgramInfo> BuildProgramWithCategories(params string[] categories)
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        var startUtc = new DateTime(2026, 4, 8, 20, 0, 0, DateTimeKind.Utc);
        var endUtc = startUtc.AddHours(1);

        var payload = JsonSerializer.Serialize(new
        {
            Entries = new object[]
            {
                new
                {
                    EventId = 500,
                    ChannelUuid = "ch-1",
                    Title = "Category Test",
                    Start = new DateTimeOffset(startUtc).ToUnixTimeSeconds(),
                    Stop = new DateTimeOffset(endUtc).ToUnixTimeSeconds(),
                    Category = categories
                }
            },
            TotalCount = 1
        });

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        return (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();
    }

    private async Task<MediaBrowser.Controller.LiveTv.ProgramInfo> BuildProgramWithFields(
        string? serieslinkUri = null,
        string? episodeUri = null,
        string? image = null,
        string? channelIcon = null,
        int[]? genres = null,
        long? firstAired = null,
        int copyrightYear = 0,
        PluginConfiguration? config = null)
    {
        config ??= new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        var startUtc = new DateTime(2026, 4, 8, 20, 0, 0, DateTimeKind.Utc);
        var endUtc = startUtc.AddHours(1);

        var entry = new Dictionary<string, object?>
        {
            { "eventId", 600 },
            { "channelUuid", "ch-1" },
            { "title", "Field Test" },
            { "start", new DateTimeOffset(startUtc).ToUnixTimeSeconds() },
            { "stop", new DateTimeOffset(endUtc).ToUnixTimeSeconds() },
        };

        if (serieslinkUri != null) entry["serieslinkUri"] = serieslinkUri;
        if (episodeUri != null) entry["episodeUri"] = episodeUri;
        if (image != null) entry["image"] = image;
        if (channelIcon != null) entry["channelIcon"] = channelIcon;
        if (genres != null) entry["genre"] = genres;
        if (firstAired.HasValue) entry["first_aired"] = firstAired.Value;
        if (copyrightYear > 0) entry["copyright_year"] = copyrightYear;

        var payload = JsonSerializer.Serialize(new { entries = new[] { entry }, totalCount = 1 });

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildApiUrl(config, It.IsAny<string>())).Returns("http://tvh/epg");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object, StubRelay(), NullHealthService.Instance);
        return (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();
    }

    private sealed class QueueResponseHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _queue;

        public QueueResponseHandler(Queue<HttpResponseMessage> queue)
        {
            _queue = queue;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_queue.Count > 0 ? _queue.Dequeue() : new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }

    private sealed class FixedResponseHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public FixedResponseHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_response);
    }
}
