using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Auth;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

/// <summary>
/// Extended tests for TokenService covering GenerateValidTokenAsync, GenerateAndStoreTokenAsync, and error paths.
/// </summary>
public class TokenServiceExtendedTests
{
    private static PluginConfiguration ConfigWithUser(string username = "admin", int maxAttempts = 2) =>
        new() { Username = username, AuthTokenMaxAttempts = maxAttempts };

    private static Mock<IUrlBuilder> CreateUrlBuilder()
    {
        var ub = new Mock<IUrlBuilder>();
        ub.Setup(x => x.BuildApiUrl(It.IsAny<PluginConfiguration>(), It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, path) => $"http://tvh:9981/{path}");
        return ub;
    }

    private static Mock<IApiClient> SetupApi(
        PluginConfiguration config,
        Func<string, string>? getStringHandler = null,
        Func<string, HttpResponseMessage>? postFormHandler = null)
    {
        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(It.IsAny<PluginConfiguration>())).Returns(new HttpClient());

        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
                Task.FromResult(getStringHandler?.Invoke(url) ?? "{}"));

        api.Setup(x => x.PostFormAsync(
                It.IsAny<HttpClient>(), It.IsAny<string>(),
                It.IsAny<IEnumerable<KeyValuePair<string, string>>>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, IEnumerable<KeyValuePair<string, string>>, CancellationToken>((_, url, _, _) =>
            {
                var response = postFormHandler?.Invoke(url)
                    ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                return Task.FromResult(response);
            });

        return api;
    }

    private static string UserGrid(string uuid, string username) =>
        $$"""{"entries":[{"uuid":"{{uuid}}","username":"{{username}}"}]}""";

    private static string UserLoad(string authCode) =>
        $$"""{"entries":[{"enabled":true,"username":"admin","password":"pass","comment":"","authcode":"{{authCode}}"}]}""";

    private static ConfigurationSaver NoopSaver() => new(_ => { });

    [Fact]
    public async Task GenerateValidTokenAsync_NullConfig_ReturnsFailure()
    {
        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns((PluginConfiguration?)null);
        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object, CreateUrlBuilder().Object, NoopSaver());

        var result = await sut.GenerateValidTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not available", result.Message);
    }

    [Fact]
    public async Task GenerateValidTokenAsync_EmptyUsername_ReturnsFailure()
    {
        var config = new PluginConfiguration { Username = "" };
        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object, CreateUrlBuilder().Object, NoopSaver());

        var result = await sut.GenerateValidTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("username is required", result.Message);
    }

    [Fact]
    public async Task GenerateValidTokenAsync_UserNotFound_ReturnsFailure()
    {
        var config = ConfigWithUser();
        var api = SetupApi(config, url =>
        {
            if (url.Contains("passwd/entry/grid")) return """{"entries":[]}""";
            return "{}";
        });

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object, CreateUrlBuilder().Object, NoopSaver());
        var result = await sut.GenerateValidTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public async Task GenerateValidTokenAsync_ValidAlphanumericToken_ReturnsSuccess()
    {
        var config = ConfigWithUser();
        var api = SetupApi(config,
            url =>
            {
                if (url.Contains("passwd/entry/grid")) return UserGrid("u1", "admin");
                return "{}";
            },
            url =>
            {
                if (url.Contains("idnode/load"))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(UserLoad("abc123")) };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object, CreateUrlBuilder().Object, NoopSaver());
        var result = await sut.GenerateValidTokenAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("abc123", result.AuthToken);
    }

    [Fact]
    public async Task GenerateValidTokenAsync_NonAlphanumericToken_Retries()
    {
        var config = ConfigWithUser(maxAttempts: 2);
        var api = SetupApi(config,
            url =>
            {
                if (url.Contains("passwd/entry/grid")) return UserGrid("u1", "admin");
                return "{}";
            },
            url =>
            {
                if (url.Contains("idnode/load"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(UserLoad("abc+def")) };
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object, CreateUrlBuilder().Object, NoopSaver());
        var result = await sut.GenerateValidTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("unsupported characters", result.Message);
    }

    [Fact]
    public async Task GenerateValidTokenAsync_EmptyTokenOnLoad_ReturnsFailure()
    {
        var config = ConfigWithUser(maxAttempts: 1);
        var api = SetupApi(config,
            url =>
            {
                if (url.Contains("passwd/entry/grid")) return UserGrid("u1", "admin");
                return "{}";
            },
            url =>
            {
                if (url.Contains("idnode/load"))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(UserLoad("")) };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object, CreateUrlBuilder().Object, NoopSaver());
        var result = await sut.GenerateValidTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task GenerateValidTokenAsync_LoadSnapshotReturnsNull_ReturnsFailure()
    {
        var config = ConfigWithUser();
        var api = SetupApi(config,
            url =>
            {
                if (url.Contains("passwd/entry/grid")) return UserGrid("u1", "admin");
                return "{}";
            },
            url =>
            {
                if (url.Contains("idnode/load"))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"entries":[]}""") };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object, CreateUrlBuilder().Object, NoopSaver());
        var result = await sut.GenerateValidTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Could not load", result.Message);
    }

    [Fact]
    public async Task GenerateValidTokenAsync_HttpException_ReturnsFailure()
    {
        var config = ConfigWithUser();
        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(It.IsAny<PluginConfiguration>())).Returns(new HttpClient());
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"));

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object, CreateUrlBuilder().Object, NoopSaver());
        var result = await sut.GenerateValidTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Cannot connect", result.Message);
    }

    [Fact]
    public async Task GenerateValidTokenAsync_UnexpectedException_ReturnsFailure()
    {
        var config = ConfigWithUser();
        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(It.IsAny<PluginConfiguration>())).Returns(new HttpClient());
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object, CreateUrlBuilder().Object, NoopSaver());
        var result = await sut.GenerateValidTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Unexpected error", result.Message);
    }

    [Fact]
    public async Task GenerateAndStoreTokenAsync_Success_SavesConfig()
    {
        var savedToken = string.Empty;
        var config = ConfigWithUser();
        var api = SetupApi(config,
            url =>
            {
                if (url.Contains("passwd/entry/grid")) return UserGrid("u1", "admin");
                return "{}";
            },
            url =>
            {
                if (url.Contains("idnode/load"))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(UserLoad("goodtoken")) };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });

        var saver = new ConfigurationSaver(mutate =>
        {
            var cfg = new PluginConfiguration();
            mutate(cfg);
            savedToken = cfg.AuthToken;
        });

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object, CreateUrlBuilder().Object, saver);
        var result = await sut.GenerateAndStoreTokenAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("goodtoken", savedToken);
        Assert.Contains("Saved to plugin", result.Message);
    }

    [Fact]
    public async Task GenerateAndStoreTokenAsync_GenerationFails_ReturnsFailure()
    {
        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns((PluginConfiguration?)null);

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object, CreateUrlBuilder().Object, NoopSaver());
        var result = await sut.GenerateAndStoreTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task GenerateAndStoreTokenAsync_HttpException_ReturnsFailure()
    {
        var config = ConfigWithUser();
        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(It.IsAny<PluginConfiguration>())).Returns(new HttpClient());
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("fail"));

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object, CreateUrlBuilder().Object, NoopSaver());
        var result = await sut.GenerateAndStoreTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task GenerateValidTokenAsync_UserMatchesByVal()
    {
        var config = ConfigWithUser();
        var api = SetupApi(config,
            url =>
            {
                if (url.Contains("passwd/entry/grid"))
                    return """{"entries":[{"uuid":"u1","val":"admin"}]}""";
                return "{}";
            },
            url =>
            {
                if (url.Contains("idnode/load"))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(UserLoad("token123")) };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object, CreateUrlBuilder().Object, NoopSaver());
        var result = await sut.GenerateValidTokenAsync(CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task GenerateValidTokenAsync_UserMatchesByTitle()
    {
        var config = ConfigWithUser();
        var api = SetupApi(config,
            url =>
            {
                if (url.Contains("passwd/entry/grid"))
                    return """{"entries":[{"uuid":"u1","title":"admin"}]}""";
                return "{}";
            },
            url =>
            {
                if (url.Contains("idnode/load"))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(UserLoad("tkn")) };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object, CreateUrlBuilder().Object, NoopSaver());
        var result = await sut.GenerateValidTokenAsync(CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task GenerateValidTokenAsync_UserHasKeyButNoUuid_UsesKey()
    {
        var config = ConfigWithUser();
        var api = SetupApi(config,
            url =>
            {
                if (url.Contains("passwd/entry/grid"))
                    return """{"entries":[{"key":"k1","username":"admin"}]}""";
                return "{}";
            },
            url =>
            {
                if (url.Contains("idnode/load"))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(UserLoad("abc")) };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });

        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object, CreateUrlBuilder().Object, NoopSaver());
        var result = await sut.GenerateValidTokenAsync(CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TokenService(null!, new Mock<IApiClient>().Object, CreateUrlBuilder().Object, NoopSaver()));
    }

    [Fact]
    public void Constructor_NullApiClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TokenService(NullLogger<TokenService>.Instance, null!, CreateUrlBuilder().Object, NoopSaver()));
    }

    [Fact]
    public void Constructor_NullConfigSaver_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TokenService(NullLogger<TokenService>.Instance, new Mock<IApiClient>().Object, CreateUrlBuilder().Object, null!));
    }

    [Fact]
    public async Task GenerateAndStoreTokenAsync_WhenSaveThrows_ReturnsFailure()
    {
        var config = ConfigWithUser();
        var api = SetupApi(config,
            url =>
            {
                if (url.Contains("passwd/entry/grid")) return UserGrid("u1", "admin");
                return "{}";
            },
            url =>
            {
                if (url.Contains("idnode/load"))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(UserLoad("goodtoken")) };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });

        var saver = new ConfigurationSaver(_ => throw new InvalidOperationException("save failed"));
        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object, CreateUrlBuilder().Object, saver);
        var result = await sut.GenerateAndStoreTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Unexpected error", result.Message);
    }

    [Fact]
    public async Task GenerateValidTokenAsync_WhitespaceUsername_ReturnsFailure()
    {
        var config = new PluginConfiguration { Username = "   " };
        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        var sut = new TokenService(NullLogger<TokenService>.Instance, api.Object, CreateUrlBuilder().Object, NoopSaver());

        var result = await sut.GenerateValidTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("username is required", result.Message);
    }
}
