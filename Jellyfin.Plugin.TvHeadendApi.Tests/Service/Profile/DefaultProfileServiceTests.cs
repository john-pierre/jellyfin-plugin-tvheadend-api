using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class DefaultProfileServiceTests
{
    [Fact]
    public void Constructor_WithNullLogger_Throws()
    {
        var api = new Mock<IApiClient>();

        Assert.Throws<ArgumentNullException>(() => new DefaultProfileService(null!, api.Object, new Mock<IUrlBuilder>().Object));
    }

    [Fact]
    public async Task CreateProfileAsync_WhenConfigurationMissing_ReturnsFailure()
    {
        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns((PluginConfiguration?)null);

        var sut = new DefaultProfileService(NullLogger<DefaultProfileService>.Instance, api.Object, new Mock<IUrlBuilder>().Object);
        var result = await sut.CreateProfileAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("configuration", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateProfileAsync_WhenHttpThrows_ReturnsConnectionFailure()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());
        urlBuilder.Setup(x => x.GetBaseUrl(config)).Returns("http://127.0.0.1:9981");
        urlBuilder.Setup(x => x.GetWebRoot(config)).Returns("/");
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"));

        var sut = new DefaultProfileService(NullLogger<DefaultProfileService>.Instance, api.Object, urlBuilder.Object);
        var result = await sut.CreateProfileAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Cannot connect", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
