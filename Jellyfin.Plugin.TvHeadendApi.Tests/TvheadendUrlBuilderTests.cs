using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class TvheadendUrlBuilderTests
{
    [Fact]
    public void BuildUrl_WithAnonymousAccess_ReturnsPlainEndpointUrl()
    {
        var sut = new TvheadendUrlBuilder();
        var config = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9981,
            Webroot = "/tvh",
            AllowAnonymousAccess = true,
        };

        var url = sut.BuildUrl(config, "api/serverinfo", "url");

        Assert.Equal("http://tvh.local:9981/tvh/api/serverinfo", url);
    }

    [Fact]
    public void BuildUrl_WithUrlAuth_EmbedsEscapedCredentials()
    {
        var sut = new TvheadendUrlBuilder();
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

        var url = sut.BuildUrl(config, "api/serverinfo", "url");

        Assert.StartsWith("https://user%40domain:pa%3Ass@tvh.local:9982/", url);
    }

    [Fact]
    public void BuildUrl_WithParameterAuth_AppendsToken()
    {
        var sut = new TvheadendUrlBuilder();
        var config = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9981,
            AllowAnonymousAccess = false,
            AuthToken = "abc123",
        };

        var url = sut.BuildUrl(config, "api/serverinfo?x=1", "parameter");

        Assert.Equal("http://tvh.local:9981/api/serverinfo?x=1&auth=abc123", url);
    }

    [Fact]
    public void MaskSensitiveData_MasksTokenAndCredentials()
    {
        var sut = new TvheadendUrlBuilder();
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

