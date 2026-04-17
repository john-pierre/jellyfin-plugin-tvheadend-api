using System;
using System.Collections.Generic;
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

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object, new PluginConfigurationSaver(_ => { }));
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
        api.Setup(x => x.BuildUrl(config, "api/passwd/entry/grid")).Returns("http://127.0.0.1:9981/api/passwd/entry/grid");
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), "http://127.0.0.1:9981/api/passwd/entry/grid", It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"entries\":[]}");

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object, new PluginConfigurationSaver(_ => { }));
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
        api.Setup(x => x.BuildUrl(config, "api/passwd/entry/grid")).Returns("http://127.0.0.1:9981/api/passwd/entry/grid");
        api.Setup(x => x.BuildUrl(config, "api/idnode/load")).Returns("http://127.0.0.1:9981/api/idnode/load");
        api.Setup(x => x.BuildUrl(config, "api/idnode/save")).Returns("http://127.0.0.1:9981/api/idnode/save");
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), "http://127.0.0.1:9981/api/passwd/entry/grid", It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"entries\":[{\"uuid\":\"uuid-1\",\"username\":\"user\"}]}");

        var loadCall = 0;
        api.Setup(x => x.PostFormAsync(
                It.IsAny<HttpClient>(),
                "http://127.0.0.1:9981/api/idnode/load",
                It.IsAny<System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                loadCall++;
                var token = loadCall >= 4 ? "abc123" : "ab_cd";
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

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object, new PluginConfigurationSaver(_ => { }));
        var result = await sut.GenerateValidTokenAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("abc123", result.AuthToken);
        Assert.Equal(2, result.AttemptCount);
        Assert.True(result.UsedRefresh);

        api.Verify(x => x.PostFormAsync(
            It.IsAny<HttpClient>(),
            "http://127.0.0.1:9981/api/idnode/save",
            It.IsAny<System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>>>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GenerateValidTokenAsync_WhenAllAttemptsInvalid_ReturnsFailureAfterMaxAttempts()
    {
        var config = new PluginConfiguration { Username = "user", Password = "0000" };
        var api = new Mock<IApiClient>();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient());
        api.Setup(x => x.BuildUrl(config, "api/passwd/entry/grid")).Returns("http://127.0.0.1:9981/api/passwd/entry/grid");
        api.Setup(x => x.BuildUrl(config, "api/idnode/load")).Returns("http://127.0.0.1:9981/api/idnode/load");
        api.Setup(x => x.BuildUrl(config, "api/idnode/save")).Returns("http://127.0.0.1:9981/api/idnode/save");
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), "http://127.0.0.1:9981/api/passwd/entry/grid", It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"entries\":[{\"uuid\":\"uuid-1\",\"username\":\"user\"}]}");

        api.Setup(x => x.PostFormAsync(
                It.IsAny<HttpClient>(),
                "http://127.0.0.1:9981/api/idnode/load",
                It.IsAny<System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"entries\":[{\"enabled\":true,\"username\":\"user\",\"password\":\"0000\",\"comment\":\"\",\"authcode\":\"ab_cd\"}]}")
            });

        api.Setup(x => x.PostFormAsync(
                It.IsAny<HttpClient>(),
                "http://127.0.0.1:9981/api/idnode/save",
                It.IsAny<System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object, new PluginConfigurationSaver(_ => { }));
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

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object, new PluginConfigurationSaver(_ => { }));
        var result = await sut.GenerateAndStoreTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("configuration", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAndStoreTokenAsync_WhenSuccessful_CallsConfigSaver()
    {
        var config = new PluginConfiguration { Username = "user", Password = "0000" };
        var api = new Mock<IApiClient>();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient());
        api.Setup(x => x.BuildUrl(config, "api/passwd/entry/grid")).Returns("http://127.0.0.1:9981/api/passwd/entry/grid");
        api.Setup(x => x.BuildUrl(config, "api/idnode/load")).Returns("http://127.0.0.1:9981/api/idnode/load");
        api.Setup(x => x.BuildUrl(config, "api/idnode/save")).Returns("http://127.0.0.1:9981/api/idnode/save");
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), "http://127.0.0.1:9981/api/passwd/entry/grid", It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"entries\":[{\"uuid\":\"uuid-1\",\"username\":\"user\"}]}");

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

        string? savedToken = null;
        var saver = new PluginConfigurationSaver(mutate =>
        {
            var cfg = new PluginConfiguration();
            mutate(cfg);
            savedToken = cfg.AuthToken;
        });

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object, saver);
        var result = await sut.GenerateAndStoreTokenAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(savedToken);
        Assert.Equal(result.AuthToken, savedToken);
    }

    [Fact]
    public async Task GenerateValidTokenAsync_WhenLoadOmitsPassword_UsesConfiguredPasswordOnSave()
    {
        var config = new PluginConfiguration { Username = "user", Password = "secret" };
        var api = new Mock<IApiClient>();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient());
        api.Setup(x => x.BuildUrl(config, "api/passwd/entry/grid")).Returns("http://127.0.0.1:9981/api/passwd/entry/grid");
        api.Setup(x => x.BuildUrl(config, "api/idnode/load")).Returns("http://127.0.0.1:9981/api/idnode/load");
        api.Setup(x => x.BuildUrl(config, "api/idnode/save")).Returns("http://127.0.0.1:9981/api/idnode/save");
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), "http://127.0.0.1:9981/api/passwd/entry/grid", It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"entries\":[{\"uuid\":\"uuid-1\",\"username\":\"user\"}]}");

        var loadCall = 0;
        api.Setup(x => x.PostFormAsync(
                It.IsAny<HttpClient>(),
                "http://127.0.0.1:9981/api/idnode/load",
                It.IsAny<IEnumerable<KeyValuePair<string, string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                loadCall++;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(loadCall == 1
                        ? "{\"entries\":[{\"enabled\":true,\"username\":\"user\",\"comment\":\"\",\"authcode\":\"\"}]}"
                        : "{\"entries\":[{\"enabled\":true,\"username\":\"user\",\"comment\":\"\",\"authcode\":\"abc123\"}]}")
                };
            });

        IEnumerable<KeyValuePair<string, string>>? postedValues = null;
        api.Setup(x => x.PostFormAsync(
                It.IsAny<HttpClient>(),
                "http://127.0.0.1:9981/api/idnode/save",
                It.IsAny<IEnumerable<KeyValuePair<string, string>>>(),
                It.IsAny<CancellationToken>()))
            .Callback<HttpClient, string, IEnumerable<KeyValuePair<string, string>>, CancellationToken>((_, _, values, _) => postedValues = values)
            .ReturnsAsync(() => new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object, new PluginConfigurationSaver(_ => { }));
        var result = await sut.GenerateValidTokenAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(postedValues);
        var nodeJson = Assert.Single(postedValues!, pair => pair.Key == "node").Value;
        Assert.Contains("\"password\":\"secret\"", nodeJson, StringComparison.Ordinal);
    }
}
