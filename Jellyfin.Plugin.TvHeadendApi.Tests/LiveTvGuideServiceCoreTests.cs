using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Guide;
using Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class LiveTvGuideServiceCoreTests
{
    [Fact]
    public async Task GetProgramsAsync_WithEmptyChannelId_ThrowsArgumentException()
    {
        var api = new Mock<ITvheadendApiClient>();
        var urlBuilder = new Mock<ITvheadendUrlBuilder>();
        var sut = new LiveTvGuideService(NullLogger<LiveTvGuideService>.Instance, api.Object, urlBuilder.Object);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.GetProgramsAsync(string.Empty, DateTime.UtcNow, DateTime.UtcNow.AddHours(1), CancellationToken.None));
    }

    [Fact]
    public async Task GetContentTypesAsync_WhenConfigMissing_ReturnsEmpty()
    {
        var api = new Mock<ITvheadendApiClient>();
        var urlBuilder = new Mock<ITvheadendUrlBuilder>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns((PluginConfiguration?)null);
        var sut = new LiveTvGuideService(NullLogger<LiveTvGuideService>.Instance, api.Object, urlBuilder.Object);

        var result = await sut.GetContentTypesAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetContentTypesAsync_WithSuccessResponse_ReturnsMappedDictionary()
    {
        var config = new PluginConfiguration();
        var api = new Mock<ITvheadendApiClient>();
        var urlBuilder = new Mock<ITvheadendUrlBuilder>();

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"entries\":[{\"key\":16,\"val\":\"Movie/Drama\"}]}")
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildUrl(config, "api/epg/content_type/list", "header")).Returns("http://tvh/content-types");

        var sut = new LiveTvGuideService(NullLogger<LiveTvGuideService>.Instance, api.Object, urlBuilder.Object);
        var result = await sut.GetContentTypesAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Movie/Drama", result[16]);
    }

    [Fact]
    public async Task GetContentTypesAsync_WithNonSuccessStatus_ReturnsEmpty()
    {
        var config = new PluginConfiguration();
        var api = new Mock<ITvheadendApiClient>();
        var urlBuilder = new Mock<ITvheadendUrlBuilder>();

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("bad")
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildUrl(config, "api/epg/content_type/list", "header")).Returns("http://tvh/content-types");

        var sut = new LiveTvGuideService(NullLogger<LiveTvGuideService>.Instance, api.Object, urlBuilder.Object);
        var result = await sut.GetContentTypesAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetChannelTagsAsync_WithSuccessResponse_ReturnsMappedDictionary()
    {
        var config = new PluginConfiguration();
        var api = new Mock<ITvheadendApiClient>();
        var urlBuilder = new Mock<ITvheadendUrlBuilder>();

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"entries\":[{\"key\":\"tag-1\",\"val\":\"HDTV\"}]}")
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildUrl(config, "api/channeltag/list", "header")).Returns("http://tvh/channel-tags");

        var sut = new LiveTvGuideService(NullLogger<LiveTvGuideService>.Instance, api.Object, urlBuilder.Object);
        var result = await sut.GetChannelTagsAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("HDTV", result["tag-1"]);
    }

    [Fact]
    public async Task GetChannelTagsAsync_WithBrokenJson_ReturnsEmpty()
    {
        var config = new PluginConfiguration();
        var api = new Mock<ITvheadendApiClient>();
        var urlBuilder = new Mock<ITvheadendUrlBuilder>();

        var handler = new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not-json")
        });

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateHttpClient(config)).Returns(new HttpClient(handler));
        urlBuilder.Setup(x => x.BuildUrl(config, "api/channeltag/list", "header")).Returns("http://tvh/channel-tags");

        var sut = new LiveTvGuideService(NullLogger<LiveTvGuideService>.Instance, api.Object, urlBuilder.Object);
        var result = await sut.GetChannelTagsAsync(CancellationToken.None);

        Assert.Empty(result);
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
