using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class UrlBuilderTests
{
    [Fact]
    public void BuildUrl_WithAnonymousAccess_ReturnsPlainEndpointUrl()
    {
        var sut = new UrlBuilder();
        var config = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9981,
            Webroot = "/tvh",
            AllowAnonymousAccess = true,
        };

        var url = sut.BuildApiUrl(config, "api/serverinfo");

        Assert.Equal("http://tvh.local:9981/tvh/api/serverinfo", url);
    }

    [Fact]
    public void BuildUrl_WithCredentials_ReturnsPlainUrlWithoutCredentials()
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

        var url = sut.BuildApiUrl(config, "api/serverinfo");

        Assert.Equal("https://tvh.local:9982/api/serverinfo", url);
        Assert.DoesNotContain("@", url);
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

        var url = sut.BuildResourceUrl(config, "api/serverinfo?x=1");

        Assert.Equal("http://tvh.local:9981/api/serverinfo?x=1&auth=abc123", url);
    }

    [Fact]
    public void MaskSensitiveData_MasksToken()
    {
        var sut = new UrlBuilder();
        var config = new PluginConfiguration
        {
            AuthToken = "token123",
        };

        var input = "http://host/api?auth=token123";
        var output = sut.MaskSensitiveData(input, config);

        Assert.DoesNotContain("token123", output);
        Assert.Contains("***", output);
    }

    [Fact]
    public void GetBaseUrl_Http_ReturnsCorrectUrl()
    {
        var sut = new UrlBuilder();
        var config = new PluginConfiguration { Host = "tvh.local", Port = 9981, UseSSL = false };
        Assert.Equal("http://tvh.local:9981", sut.GetBaseUrl(config));
    }

    [Fact]
    public void GetBaseUrl_Https_ReturnsCorrectUrl()
    {
        var sut = new UrlBuilder();
        var config = new PluginConfiguration { Host = "tvh.local", Port = 443, UseSSL = true };
        Assert.Equal("https://tvh.local:443", sut.GetBaseUrl(config));
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData(" ", "/")]
    [InlineData("/", "/")]
    [InlineData("tvh", "/tvh/")]
    [InlineData("/tvh", "/tvh/")]
    [InlineData("tvh/", "/tvh/")]
    [InlineData("/tvh/", "/tvh/")]
    public void GetWebRoot_NormalizesCorrectly(string? webroot, string expected)
    {
        var sut = new UrlBuilder();
        var config = new PluginConfiguration { Webroot = webroot! };
        Assert.Equal(expected, sut.GetWebRoot(config));
    }

    [Fact]
    public void BuildResourceUrl_AnonymousAccess_NoAuthParam()
    {
        var sut = new UrlBuilder();
        var config = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9981,
            AllowAnonymousAccess = true,
            AuthToken = "abc123",
        };
        var url = sut.BuildResourceUrl(config, "stream/channel/1");
        Assert.DoesNotContain("auth=", url);
    }

    [Fact]
    public void BuildResourceUrl_NoToken_NoAuthParam()
    {
        var sut = new UrlBuilder();
        var config = new PluginConfiguration
        {
            Host = "tvh.local",
            Port = 9981,
            AllowAnonymousAccess = false,
            AuthToken = "",
        };
        var url = sut.BuildResourceUrl(config, "stream/channel/1");
        Assert.DoesNotContain("auth=", url);
    }

    [Fact]
    public void MaskSensitiveData_MasksPasswordAndUsername()
    {
        var sut = new UrlBuilder();
        var config = new PluginConfiguration
        {
            Username = "admin",
            Password = "s3cret",
            AuthToken = "tok42",
        };
        var input = "http://admin:s3cret@host/api?auth=tok42";
        var output = sut.MaskSensitiveData(input, config);
        Assert.DoesNotContain("admin", output);
        Assert.DoesNotContain("s3cret", output);
        Assert.DoesNotContain("tok42", output);
    }
}
