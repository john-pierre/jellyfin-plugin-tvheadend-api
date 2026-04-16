using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Guide;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class GuideServiceCoreTests
{
    [Fact]
    public async Task GetChannelsAsync_WithSuccessResponse_MapsNumberAndImageProxyUrl()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();

        var json = """
                   {
                     "entries": [
                       { "uuid": "ch-1", "name": "One", "number": 7000000, "icon_public_url": "/imagecache/my icon.png" },
                       { "uuid": "ch-2", "name": "Two", "number": 7000005, "icon_public_url": "" }
                     ],
                     "total": 2
                   }
                   """;

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.MaskSensitiveData(It.IsAny<string>(), config)).Returns<string, PluginConfiguration>((value, _) => value);
        urlBuilder
            .Setup(x => x.BuildUrlWithHeaderAuth(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));
        urlBuilder
            .Setup(x => x.BuildUrlWithParameterAuth(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));
        urlBuilder
            .Setup(x => x.BuildUrlWithUrlAuth(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object);
        var result = (await sut.GetChannelsAsync(CancellationToken.None)).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal("7", result[0].Number);
        Assert.Equal("http://tvh/imagecache/my icon.png", result[0].ImageUrl);
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
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildUrlWithHeaderAuth(config, It.IsAny<string>())).Returns("http://tvh/channels");
        urlBuilder.Setup(x => x.BuildUrlWithParameterAuth(config, It.IsAny<string>())).Returns("http://tvh/channels");
        urlBuilder.Setup(x => x.MaskSensitiveData(It.IsAny<string>(), config)).Returns("http://tvh/channels");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object);
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
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildUrlWithHeaderAuth(config, It.IsAny<string>())).Returns("http://tvh/channels");
        urlBuilder.Setup(x => x.BuildUrlWithParameterAuth(config, It.IsAny<string>())).Returns("http://tvh/channels");
        urlBuilder.Setup(x => x.MaskSensitiveData(It.IsAny<string>(), config)).Returns("http://tvh/channels");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object);
        var result = await sut.GetChannelsAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenConfigMissing_ReturnsEmpty()
    {
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns((PluginConfiguration?)null);
        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object);

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
                                        { "entries": [ { "uuid": "ch-1", "name": "One", "number": 1000000, "icon_public_url": "   " } ], "total": 1 }
                                        """)
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildUrlWithHeaderAuth(config, It.IsAny<string>())).Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));
        urlBuilder.Setup(x => x.BuildUrlWithParameterAuth(config, It.IsAny<string>())).Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));
        urlBuilder.Setup(x => x.MaskSensitiveData(It.IsAny<string>(), config)).Returns<string, PluginConfiguration>((value, _) => value);

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object);
        var result = (await sut.GetChannelsAsync(CancellationToken.None)).Single();

        Assert.False(result.HasImage);
        Assert.Null(result.ImageUrl);
        urlBuilder.Verify(x => x.BuildUrlWithParameterAuth(config, It.Is<string>(endpoint => endpoint.StartsWith("imagecache/", StringComparison.Ordinal))), Times.Never);
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
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildUrlWithHeaderAuth(config, It.IsAny<string>())).Returns("http://tvh/channels");
        urlBuilder.Setup(x => x.BuildUrlWithParameterAuth(config, It.IsAny<string>())).Returns("http://tvh/channels");
        urlBuilder.Setup(x => x.MaskSensitiveData(It.IsAny<string>(), config)).Returns("http://tvh/channels");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object);
        var result = await sut.GetChannelsAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetProgramsAsync_WithEmptyChannelId_ThrowsArgumentException()
    {
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object);

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
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildUrlWithHeaderAuth(config, It.IsAny<string>())).Returns("http://tvh/epg");
        urlBuilder.Setup(x => x.BuildUrlWithParameterAuth(config, It.IsAny<string>())).Returns("http://tvh/epg");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object);
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
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder
            .Setup(x => x.BuildUrlWithHeaderAuth(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));
        urlBuilder.Setup(x => x.BuildUrlWithParameterAuth(config, It.IsAny<string>())).Returns("http://tvh/image");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object);
        var result = (await sut.GetProgramsAsync("ch/1", startUtc, endUtc, CancellationToken.None)).ToList();

        Assert.Single(result);
        urlBuilder.Verify(
            x => x.BuildUrlWithHeaderAuth(
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
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder
            .Setup(x => x.BuildUrlWithHeaderAuth(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));
        urlBuilder
            .Setup(x => x.BuildUrlWithParameterAuth(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object);
        var program = (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();

        Assert.Equal("http://tvh/imagecache/poster 1.png", program.ImageUrl);
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
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder
            .Setup(x => x.BuildUrlWithHeaderAuth(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));
        urlBuilder
            .Setup(x => x.BuildUrlWithParameterAuth(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object);
        var program = (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();

        Assert.Equal("http://tvh/imagecache/ch1.png", program.ImageUrl);
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
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildUrlWithHeaderAuth(config, It.IsAny<string>())).Returns("http://tvh/epg");
        urlBuilder.Setup(x => x.BuildUrlWithParameterAuth(config, It.IsAny<string>())).Returns("http://tvh/epg");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object);
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
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildUrlWithHeaderAuth(config, It.IsAny<string>())).Returns("http://tvh/epg");
        urlBuilder.Setup(x => x.BuildUrlWithParameterAuth(config, It.IsAny<string>())).Returns("http://tvh/epg");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object);
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
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildUrlWithHeaderAuth(config, It.IsAny<string>())).Returns("http://tvh/epg");
        urlBuilder.Setup(x => x.BuildUrlWithParameterAuth(config, It.IsAny<string>())).Returns("http://tvh/epg");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object);
        var program = (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();

        Assert.Equal(externalImage, program.ImageUrl);
        Assert.True(program.HasImage);
        urlBuilder.Verify(x => x.BuildUrlWithParameterAuth(config, It.Is<string>(endpoint => endpoint.Contains("imagecache/", StringComparison.Ordinal))), Times.Never);
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
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildUrlWithHeaderAuth(config, "api/epg/content_type/list")).Returns("http://tvh/epg");
        urlBuilder.Setup(x => x.BuildUrlWithParameterAuth(config, "api/epg/content_type/list")).Returns("http://tvh/epg");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object);
        var result = await sut.GetProgramsAsync("ch-1", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1), CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetContentTypesAsync_WhenConfigMissing_ReturnsEmpty()
    {
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns((PluginConfiguration?)null);
        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object);

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
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildUrlWithHeaderAuth(config, "api/epg/content_type/list")).Returns("http://tvh/content-types");
        urlBuilder.Setup(x => x.BuildUrlWithParameterAuth(config, "api/epg/content_type/list")).Returns("http://tvh/content-types");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object);
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
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildUrlWithHeaderAuth(config, "api/epg/content_type/list")).Returns("http://tvh/content-types");
        urlBuilder.Setup(x => x.BuildUrlWithParameterAuth(config, "api/epg/content_type/list")).Returns("http://tvh/content-types");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object);
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
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildUrlWithHeaderAuth(config, "api/channeltag/list")).Returns("http://tvh/channel-tags");
        urlBuilder.Setup(x => x.BuildUrlWithParameterAuth(config, "api/channeltag/list")).Returns("http://tvh/channel-tags");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object);
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
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildUrlWithHeaderAuth(config, "api/channeltag/list")).Returns("http://tvh/channel-tags");
        urlBuilder.Setup(x => x.BuildUrlWithParameterAuth(config, "api/channeltag/list")).Returns("http://tvh/channel-tags");

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object);
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
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder
            .Setup(x => x.BuildUrlWithHeaderAuth(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));
        urlBuilder
            .Setup(x => x.BuildUrlWithParameterAuth(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object);
        var program = (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();

        Assert.Equal("http://tvh/imagecache/poster.png", program.ImageUrl);
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
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder
            .Setup(x => x.BuildUrlWithHeaderAuth(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));
        urlBuilder
            .Setup(x => x.BuildUrlWithParameterAuth(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object);
        var program = (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();

        Assert.Equal("http://tvh/imagecache/ch-fallback.png", program.ImageUrl);
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
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder
            .Setup(x => x.BuildUrlWithHeaderAuth(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));
        urlBuilder
            .Setup(x => x.BuildUrlWithParameterAuth(config, It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));

        var sut = new GuideService(NullLogger<GuideService>.Instance, api.Object, urlBuilder.Object);
        var program = (await sut.GetProgramsAsync("ch-1", startUtc.AddMinutes(-1), endUtc.AddMinutes(1), CancellationToken.None)).Single();

        Assert.Equal("http://tvh/imagecache/poster.png", program.ImageUrl);
        Assert.Equal("http://tvh/imagecache/rating-pg.png", program.LogoImageUrl);
        Assert.True(program.HasImage);
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
