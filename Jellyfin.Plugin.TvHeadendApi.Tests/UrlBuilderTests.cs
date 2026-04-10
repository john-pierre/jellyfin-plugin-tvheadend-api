using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Infrastructure;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class UrlBuilderTests
{
    [Fact]
    public void BuildUrlWithUrlAuth_WithAnonymousAccess_ReturnsPlainEndpointUrl()
    {
        var sut = new UrlBuilder();
        var config = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9981,
            Webroot = "/tvh",
            AllowAnonymousAccess = true,
        };

        var url = sut.BuildUrlWithUrlAuth(config, "api/serverinfo");

        Assert.Equal("http://tvh.local:9981/tvh/api/serverinfo", url);
    }

    [Fact]
    public void BuildUrlWithUrlAuth_EmbedsEscapedCredentials()
    {
        var sut = new UrlBuilder();
        var config = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9982,
            UseSSL = true,
            Webroot = "/",
            AllowAnonymousAccess = false,
            Username = "user@domain",
            Password = "pa:ss",
        };

        var url = sut.BuildUrlWithUrlAuth(config, "api/serverinfo");

        Assert.StartsWith("https://user%40domain:pa%3Ass@tvh.local:9982/", url);
    }

    [Fact]
    public void BuildUrlWithParameterAuth_AppendsToken()
    {
        var sut = new UrlBuilder();
        var config = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9981,
            AllowAnonymousAccess = false,
            AuthToken = "abc123",
        };

        var url = sut.BuildUrlWithParameterAuth(config, "api/serverinfo?x=1");

        Assert.Equal("http://tvh.local:9981/api/serverinfo?x=1&auth=abc123", url);
    }

    [Fact]
    public void MaskSensitiveData_MasksTokenAndCredentials()
    {
        var sut = new UrlBuilder();
        var config = new PluginConfiguration
        {
            Username = "alice",
            Password = "secret",
            AuthToken = "token123",
        };

        var input = "http://alice:secret@host/api?a=token123";
        var output = sut.MaskSensitiveData(input, config);

        Assert.DoesNotContain("secret", output);
        Assert.DoesNotContain("token123", output);
        Assert.Contains("***", output);
    }
}
