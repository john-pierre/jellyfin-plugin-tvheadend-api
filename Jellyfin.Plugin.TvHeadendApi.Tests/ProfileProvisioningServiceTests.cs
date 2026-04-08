using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Profiles;
using Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class ProfileProvisioningServiceTests
{
    [Fact]
    public void Constructor_WithNullLogger_Throws()
    {
        var idNode = new TvheadendIdNodeService();
        var api = new Mock<ITvheadendApiClient>();

        Assert.Throws<ArgumentNullException>(() => new ProfileProvisioningService(null!, idNode, api.Object));
    }

    [Fact]
    public async Task CreateProfileAsync_WhenConfigurationMissing_ReturnsFailure()
    {
        var idNode = new TvheadendIdNodeService();
        var api = new Mock<ITvheadendApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns((PluginConfiguration?)null);

        var sut = new ProfileProvisioningService(NullLogger<ProfileProvisioningService>.Instance, idNode, api.Object);
        var result = await sut.CreateProfileAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("configuration", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAuthTokenAsync_WhenConfigurationMissing_ReturnsFailure()
    {
        var idNode = new TvheadendIdNodeService();
        var api = new Mock<ITvheadendApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns((PluginConfiguration?)null);

        var sut = new ProfileProvisioningService(NullLogger<ProfileProvisioningService>.Instance, idNode, api.Object);
        var result = await sut.GenerateAuthTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("configuration", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAuthTokenAsync_WhenHttpStatusNotSuccess_ReturnsFailureWithStatus()
    {
        var config = new PluginConfiguration();
        var idNode = new TvheadendIdNodeService();
        var api = new Mock<ITvheadendApiClient>();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateHttpClient(config)).Returns(new HttpClient());
        api.Setup(x => x.GetBaseUrl(config)).Returns("http://127.0.0.1:9981");
        api.Setup(x => x.GetWebRoot(config)).Returns("/");
        api.Setup(x => x.PostFormAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("denied")
            });

        var sut = new ProfileProvisioningService(NullLogger<ProfileProvisioningService>.Instance, idNode, api.Object);
        var result = await sut.GenerateAuthTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("403", result.Message);
    }

    [Fact]
    public async Task GenerateAuthTokenAsync_WhenTokenMissingInResponse_ReturnsFailure()
    {
        var config = new PluginConfiguration();
        var idNode = new TvheadendIdNodeService();
        var api = new Mock<ITvheadendApiClient>();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateHttpClient(config)).Returns(new HttpClient());
        api.Setup(x => x.GetBaseUrl(config)).Returns("http://127.0.0.1:9981");
        api.Setup(x => x.GetWebRoot(config)).Returns("/");
        api.Setup(x => x.PostFormAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });

        var sut = new ProfileProvisioningService(NullLogger<ProfileProvisioningService>.Instance, idNode, api.Object);
        var result = await sut.GenerateAuthTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("token", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAuthTokenAsync_WhenHttpThrows_ReturnsConnectionFailure()
    {
        var config = new PluginConfiguration();
        var idNode = new TvheadendIdNodeService();
        var api = new Mock<ITvheadendApiClient>();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateHttpClient(config)).Returns(new HttpClient());
        api.Setup(x => x.GetBaseUrl(config)).Returns("http://127.0.0.1:9981");
        api.Setup(x => x.GetWebRoot(config)).Returns("/");
        api.Setup(x => x.PostFormAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("boom"));

        var sut = new ProfileProvisioningService(NullLogger<ProfileProvisioningService>.Instance, idNode, api.Object);
        var result = await sut.GenerateAuthTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Cannot connect", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAuthTokenAsync_WhenTokenExistsButPluginInstanceMissing_ReturnsFailure()
    {
        var config = new PluginConfiguration();
        var idNode = new TvheadendIdNodeService();
        var api = new Mock<ITvheadendApiClient>();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateHttpClient(config)).Returns(new HttpClient());
        api.Setup(x => x.GetBaseUrl(config)).Returns("http://127.0.0.1:9981");
        api.Setup(x => x.GetWebRoot(config)).Returns("/");
        api.Setup(x => x.PostFormAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"token\":\"abc123\"}")
            });

        var sut = new ProfileProvisioningService(NullLogger<ProfileProvisioningService>.Instance, idNode, api.Object);
        var result = await sut.GenerateAuthTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Plugin instance", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAuthTokenAsync_WhenTokenContainsUnsupportedCharacters_ReturnsFailure()
    {
        var config = new PluginConfiguration();
        var idNode = new TvheadendIdNodeService();
        var api = new Mock<ITvheadendApiClient>();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateHttpClient(config)).Returns(new HttpClient());
        api.Setup(x => x.GetBaseUrl(config)).Returns("http://127.0.0.1:9981");
        api.Setup(x => x.GetWebRoot(config)).Returns("/");
        api.Setup(x => x.PostFormAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"token\":\"abc.def-123\"}")
            });

        var sut = new ProfileProvisioningService(NullLogger<ProfileProvisioningService>.Instance, idNode, api.Object);
        var result = await sut.GenerateAuthTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Only letters and numbers", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateProfileAsync_WhenHttpThrows_ReturnsConnectionFailure()
    {
        var config = new PluginConfiguration();
        var idNode = new TvheadendIdNodeService();
        var api = new Mock<ITvheadendApiClient>();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateHttpClient(config)).Returns(new HttpClient());
        api.Setup(x => x.GetBaseUrl(config)).Returns("http://127.0.0.1:9981");
        api.Setup(x => x.GetWebRoot(config)).Returns("/");
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"));

        var sut = new ProfileProvisioningService(NullLogger<ProfileProvisioningService>.Instance, idNode, api.Object);
        var result = await sut.CreateProfileAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Cannot connect", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
