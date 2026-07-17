using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Input;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Input;

public class InputMonitorServiceTests
{
    [Fact]
    public void Constructor_WithNullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new InputMonitorService(null!, new Mock<IApiClient>().Object, new Mock<IUrlBuilder>().Object, NullHealthService.Instance));
    }

    [Fact]
    public void Constructor_WithNullApiClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new InputMonitorService(NullLogger<InputMonitorService>.Instance, null!, new Mock<IUrlBuilder>().Object, NullHealthService.Instance));
    }

    [Fact]
    public async Task GetInputStatusAsync_WhenConfigMissing_ReturnsEmpty()
    {
        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns((PluginConfiguration?)null);
        var sut = new InputMonitorService(NullLogger<InputMonitorService>.Instance, api.Object, new Mock<IUrlBuilder>().Object, NullHealthService.Instance);

        var result = await sut.GetInputStatusAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetInputStatusAsync_WhenValid_ReturnsInputEntries()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());
        urlBuilder.Setup(x => x.BuildApiUrl(config, "api/status/inputs")).Returns("http://localhost:9981/api/status/inputs");
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), "http://localhost:9981/api/status/inputs", It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"entries\":[{\"uuid\":\"abc123\",\"input\":\"DVB-T #1\",\"stream\":\"690MHz\",\"subs\":1,\"weight\":100,\"signal\":-450,\"signal_scale\":2,\"ber\":0,\"snr\":280,\"snr_scale\":2,\"unc\":0,\"bps\":12000000,\"te\":0,\"cc\":0,\"ec_bit\":0,\"tc_bit\":1000,\"ec_block\":0,\"tc_block\":50}],\"totalCount\":1}");

        var sut = new InputMonitorService(NullLogger<InputMonitorService>.Instance, api.Object, urlBuilder.Object, NullHealthService.Instance);
        var result = await sut.GetInputStatusAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("abc123", result[0].Uuid);
        Assert.Equal("DVB-T #1", result[0].Input);
        Assert.Equal(-450, result[0].Signal);
        Assert.Equal(280, result[0].Snr);
        Assert.Equal(12000000, result[0].Bps);
    }

    [Fact]
    public async Task GetInputStatusAsync_WhenEmptyGrid_ReturnsEmpty()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());
        urlBuilder.Setup(x => x.BuildApiUrl(config, "api/status/inputs")).Returns("http://localhost:9981/api/status/inputs");
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), "http://localhost:9981/api/status/inputs", It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"entries\":[],\"totalCount\":0}");

        var sut = new InputMonitorService(NullLogger<InputMonitorService>.Instance, api.Object, urlBuilder.Object, NullHealthService.Instance);
        var result = await sut.GetInputStatusAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetInputStatusAsync_WhenCircuitOpen_ReturnsEmptyWithoutCallingApi()
    {
        var api = new Mock<IApiClient>();
        var health = new Mock<IHealthService>();
        health.Setup(h => h.ShouldBlockRequest()).Returns(true);

        var sut = new InputMonitorService(NullLogger<InputMonitorService>.Instance, api.Object, new Mock<IUrlBuilder>().Object, health.Object);
        var result = await sut.GetInputStatusAsync(CancellationToken.None);

        Assert.Empty(result);
        api.Verify(x => x.GetCurrentConfiguration(), Times.Never);
    }
}
