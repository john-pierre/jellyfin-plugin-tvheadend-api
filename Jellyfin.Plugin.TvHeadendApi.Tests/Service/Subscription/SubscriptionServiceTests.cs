using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Jellyfin.Plugin.TvHeadendApi.Service.Subscription;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Subscription;

public class SubscriptionServiceTests
{
    [Fact]
    public void Constructor_WithNullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SubscriptionService(null!, new Mock<IApiClient>().Object, new Mock<IUrlBuilder>().Object, NullHealthService.Instance));
    }

    [Fact]
    public void Constructor_WithNullApiClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SubscriptionService(NullLogger<SubscriptionService>.Instance, null!, new Mock<IUrlBuilder>().Object, NullHealthService.Instance));
    }

    [Fact]
    public async Task GetActiveSubscriptionsAsync_WhenConfigMissing_ReturnsEmpty()
    {
        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns((PluginConfiguration?)null);
        var sut = new SubscriptionService(NullLogger<SubscriptionService>.Instance, api.Object, new Mock<IUrlBuilder>().Object, NullHealthService.Instance);

        var result = await sut.GetActiveSubscriptionsAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetActiveSubscriptionsAsync_WhenValid_ReturnsSubscriptions()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());
        urlBuilder.Setup(x => x.BuildApiUrl(config, "api/status/subscriptions")).Returns("http://localhost:9981/api/status/subscriptions");
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), "http://localhost:9981/api/status/subscriptions", It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"entries\":[{\"id\":42,\"start\":1700000000,\"errors\":0,\"state\":\"Running\",\"hostname\":\"192.168.1.2\",\"username\":\"admin\",\"client\":\"Jellyfin\",\"title\":\"epg\",\"channel\":\"BBC One\",\"service\":\"DVB-T/690MHz\",\"profile\":\"pass\",\"in\":1500000,\"out\":1500000,\"total_in\":75000000,\"total_out\":75000000}],\"totalCount\":1}");

        var sut = new SubscriptionService(NullLogger<SubscriptionService>.Instance, api.Object, urlBuilder.Object, NullHealthService.Instance);
        var result = await sut.GetActiveSubscriptionsAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(42, result[0].Id);
        Assert.Equal("Running", result[0].State);
        Assert.Equal("BBC One", result[0].Channel);
        Assert.Equal("pass", result[0].Profile);
        Assert.Equal(1500000, result[0].In);
    }

    [Fact]
    public async Task GetActiveSubscriptionsAsync_WhenEmptyGrid_ReturnsEmpty()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());
        urlBuilder.Setup(x => x.BuildApiUrl(config, "api/status/subscriptions")).Returns("http://localhost:9981/api/status/subscriptions");
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), "http://localhost:9981/api/status/subscriptions", It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"entries\":[],\"totalCount\":0}");

        var sut = new SubscriptionService(NullLogger<SubscriptionService>.Instance, api.Object, urlBuilder.Object, NullHealthService.Instance);
        var result = await sut.GetActiveSubscriptionsAsync(CancellationToken.None);

        Assert.Empty(result);
    }
}
