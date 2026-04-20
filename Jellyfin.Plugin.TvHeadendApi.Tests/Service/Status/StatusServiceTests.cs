using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Jellyfin.Plugin.TvHeadendApi.Service.Status;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Status;

public class StatusServiceTests
{
    private static StatusService CreateSut(Mock<IApiClient>? apiClient = null)
    {
        var api = apiClient ?? new Mock<IApiClient>();
        return new StatusService(NullLogger<StatusService>.Instance, api.Object, new Mock<IUrlBuilder>().Object);
    }

    [Fact]
    public void Constructor_WithNullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new StatusService(null!, new Mock<IApiClient>().Object, new Mock<IUrlBuilder>().Object));
    }

    [Fact]
    public void Constructor_WithNullApiClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new StatusService(NullLogger<StatusService>.Instance, null!, new Mock<IUrlBuilder>().Object));
    }

    [Fact]
    public async Task GetActivityStatusAsync_WhenConfigMissing_ReturnsNull()
    {
        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns((PluginConfiguration?)null);
        var sut = CreateSut(api);

        var result = await sut.GetActivityStatusAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetActivityStatusAsync_WhenValid_ReturnsActivityStatus()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());
        urlBuilder.Setup(x => x.BuildApiUrl(config, "api/status/subscriptions")).Returns("http://localhost:9981/api/status/subscriptions");
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), "http://localhost:9981/api/status/subscriptions", It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"entries\":[{\"id\":1},{\"id\":2}],\"totalCount\":2}");
        urlBuilder.Setup(x => x.BuildApiUrl(config, "api/status/connections")).Returns("http://localhost:9981/api/status/connections");
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), "http://localhost:9981/api/status/connections", It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"entries\":[{\"id\":1},{\"id\":2},{\"id\":3},{\"id\":4},{\"id\":5}],\"totalCount\":5}");

        var sut = new StatusService(NullLogger<StatusService>.Instance, api.Object, urlBuilder.Object);
        var result = await sut.GetActivityStatusAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.CurrentTime > 0);
        Assert.Equal(0, result.NextActivity);
        Assert.Equal(2, result.SubscriptionCount);
        Assert.Equal(5, result.ConnectionCount);
    }

    [Fact]
    public async Task GetConnectionsAsync_WhenConfigMissing_ReturnsEmpty()
    {
        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns((PluginConfiguration?)null);
        var sut = CreateSut(api);

        var result = await sut.GetConnectionsAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetConnectionsAsync_WhenValid_ReturnsConnections()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());
        urlBuilder.Setup(x => x.BuildApiUrl(config, "api/status/connections")).Returns("http://localhost:9981/api/status/connections");
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), "http://localhost:9981/api/status/connections", It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"entries\":[{\"id\":1,\"server\":\"192.168.1.1\",\"server_port\":9981,\"peer\":\"192.168.1.2\",\"peer_port\":54321,\"started\":1700000000,\"streaming\":1,\"type\":\"HTTP\",\"user\":\"admin\"}],\"totalCount\":1}");

        var sut = new StatusService(NullLogger<StatusService>.Instance, api.Object, urlBuilder.Object);
        var result = await sut.GetConnectionsAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("admin", result[0].User);
        Assert.Equal("HTTP", result[0].Type);
        Assert.Equal(1, result[0].Streaming);
    }
}
