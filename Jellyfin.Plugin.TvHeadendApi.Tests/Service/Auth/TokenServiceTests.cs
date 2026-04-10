using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Auth;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class TokenServiceTests
{
    [Fact]
    public async Task GenerateValidTokenAsync_WhenConfigurationMissing_ReturnsFailure()
    {
        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns((PluginConfiguration?)null);

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object);
        var result = await sut.GenerateValidTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("configuration", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateValidTokenAsync_WhenUserNotFound_ReturnsFailure()
    {
        var config = new PluginConfiguration { Username = "user", Password = "0000" };
        var api = new Mock<IApiClient>();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient());
        api.Setup(x => x.BuildUrl(config, "api/user/list")).Returns("http://127.0.0.1:9981/api/user/list");
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), "http://127.0.0.1:9981/api/user/list", It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"entries\":[]}");

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object);
        var result = await sut.GenerateValidTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateValidTokenAsync_WhenFirstTokenInvalid_RefreshesAndReturnsValidToken()
    {
        var config = new PluginConfiguration { Username = "user", Password = "0000" };
        var api = new Mock<IApiClient>();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient());
        api.Setup(x => x.BuildUrl(config, "api/user/list")).Returns("http://127.0.0.1:9981/api/user/list");
        api.Setup(x => x.BuildUrl(config, "api/idnode/load")).Returns("http://127.0.0.1:9981/api/idnode/load");
        api.Setup(x => x.BuildUrl(config, "api/idnode/save")).Returns("http://127.0.0.1:9981/api/idnode/save");
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), "http://127.0.0.1:9981/api/user/list", It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"entries\":[{\"key\":\"uuid-1\",\"val\":\"user\"}]}");

        var loadCall = 0;
        api.Setup(x => x.PostFormAsync(
                It.IsAny<HttpClient>(),
                "http://127.0.0.1:9981/api/idnode/load",
                It.IsAny<System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                loadCall++;
                var token = loadCall == 1 ? "ab.cd" : "abc123";
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"entries\":[{{\"enabled\":true,\"username\":\"user\",\"password\":\"0000\",\"comment\":\"\",\"authcode\":\"{token}\"}}]}}")
                };
            });

        api.Setup(x => x.PostFormAsync(
                It.IsAny<HttpClient>(),
                "http://127.0.0.1:9981/api/idnode/save",
                It.IsAny<System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object);
        var result = await sut.GenerateValidTokenAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("abc123", result.AuthToken);
        Assert.Equal(1, result.AttemptCount);
        Assert.False(result.UsedRefresh);

        api.Verify(x => x.PostFormAsync(
            It.IsAny<HttpClient>(),
            "http://127.0.0.1:9981/api/idnode/save",
            It.IsAny<System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateValidTokenAsync_WhenAllAttemptsInvalid_ReturnsFailureAfterMaxAttempts()
    {
        var config = new PluginConfiguration { Username = "user", Password = "0000" };
        var api = new Mock<IApiClient>();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient());
        api.Setup(x => x.BuildUrl(config, "api/user/list")).Returns("http://127.0.0.1:9981/api/user/list");
        api.Setup(x => x.BuildUrl(config, "api/idnode/load")).Returns("http://127.0.0.1:9981/api/idnode/load");
        api.Setup(x => x.BuildUrl(config, "api/idnode/save")).Returns("http://127.0.0.1:9981/api/idnode/save");
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), "http://127.0.0.1:9981/api/user/list", It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"entries\":[{\"key\":\"uuid-1\",\"val\":\"user\"}]}");

        api.Setup(x => x.PostFormAsync(
                It.IsAny<HttpClient>(),
                "http://127.0.0.1:9981/api/idnode/load",
                It.IsAny<System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"entries\":[{\"enabled\":true,\"username\":\"user\",\"password\":\"0000\",\"comment\":\"\",\"authcode\":\"ab.cd\"}]}")
            });

        api.Setup(x => x.PostFormAsync(
                It.IsAny<HttpClient>(),
                "http://127.0.0.1:9981/api/idnode/save",
                It.IsAny<System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object);
        var result = await sut.GenerateValidTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(5, result.AttemptCount);
        api.Verify(x => x.PostFormAsync(
            It.IsAny<HttpClient>(),
            "http://127.0.0.1:9981/api/idnode/save",
            It.IsAny<System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>>>(),
            It.IsAny<CancellationToken>()), Times.Exactly(5));
    }

    [Fact]
    public async Task GenerateAndStoreTokenAsync_WhenGenerationFails_ReturnsFailure()
    {
        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns((PluginConfiguration?)null);

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object);
        var result = await sut.GenerateAndStoreTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("configuration", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAndStoreTokenAsync_WhenPluginInstanceMissing_ReturnsFailure()
    {
        var config = new PluginConfiguration { Username = "user", Password = "0000" };
        var api = new Mock<IApiClient>();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient());
        api.Setup(x => x.BuildUrl(config, "api/user/list")).Returns("http://127.0.0.1:9981/api/user/list");
        api.Setup(x => x.BuildUrl(config, "api/idnode/load")).Returns("http://127.0.0.1:9981/api/idnode/load");
        api.Setup(x => x.BuildUrl(config, "api/idnode/save")).Returns("http://127.0.0.1:9981/api/idnode/save");
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), "http://127.0.0.1:9981/api/user/list", It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"entries\":[{\"key\":\"uuid-1\",\"val\":\"user\"}]}");

        api.Setup(x => x.PostFormAsync(
                It.IsAny<HttpClient>(),
                "http://127.0.0.1:9981/api/idnode/load",
                It.IsAny<System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"entries\":[{\"enabled\":true,\"username\":\"user\",\"password\":\"0000\",\"comment\":\"\",\"authcode\":\"abc123\"}]}")
            });

        api.Setup(x => x.PostFormAsync(
                It.IsAny<HttpClient>(),
                "http://127.0.0.1:9981/api/idnode/save",
                It.IsAny<System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object);
        var result = await sut.GenerateAndStoreTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Plugin instance", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
