using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

/// <summary>
/// Tests for UrlBuilder edge cases and URL construction.
/// </summary>
public class UrlBuilderEdgeCaseTests
{
    private readonly UrlBuilder _sut = new();

    [Fact]
    public void BuildUrlWithHeaderAuth_IncludesHostnameAndPort()
    {
        var config = new PluginConfiguration { Host = "tvheadend.local", Port = 9981 };
        var url = _sut.BuildUrlWithHeaderAuth(config, "/api/test");

        Assert.NotEmpty(url);
        Assert.Contains("tvheadend.local", url);
        Assert.Contains("9981", url);
    }

    [Fact]
    public void BuildUrlWithHeaderAuth_WithLocalhost_WorksCorrectly()
    {
        var config = new PluginConfiguration { Host = "localhost", Port = 9981 };
        var url = _sut.BuildUrlWithHeaderAuth(config, "/api/test");

        Assert.Contains("localhost", url);
    }

    [Fact]
    public void BuildUrlWithHeaderAuth_WithIPAddress_WorksCorrectly()
    {
        var config = new PluginConfiguration { Host = "192.168.1.100", Port = 9981 };
        var url = _sut.BuildUrlWithHeaderAuth(config, "/api/test");

        Assert.Contains("192.168.1.100", url);
    }

    [Fact]
    public void BuildUrlWithHeaderAuth_WithHttps_UsesCorrectProtocol()
    {
        var config = new PluginConfiguration
        {
            Host = "tvheadend.local",
            Port = 9981,
            UseSSL = true
        };

        var url = _sut.BuildUrlWithHeaderAuth(config, "/api/channel/grid");

        Assert.StartsWith("https://", url);
    }

    [Fact]
    public void BuildUrlWithHeaderAuth_WithHttp_UsesCorrectProtocol()
    {
        var config = new PluginConfiguration
        {
            Host = "tvheadend.local",
            Port = 9981,
            UseSSL = false
        };

        var url = _sut.BuildUrlWithHeaderAuth(config, "/api/channel/grid");

        Assert.StartsWith("http://", url);
    }

    [Fact]
    public void BuildUrlWithParameterAuth_IncludesAuthToken()
    {
        var config = new PluginConfiguration
        {
            Host = "localhost",
            Port = 9981,
            AuthToken = "token123",
            AllowAnonymousAccess = false,
        };

        var url = _sut.BuildUrlWithParameterAuth(config, "/api/channel/grid");

        Assert.Contains("auth=token123", url);
    }

    [Fact]
    public void BuildUrlWithHeaderAuth_MultipleCallsWithSameConfig_ProduceConsistentResults()
    {
        var config = new PluginConfiguration { Host = "localhost", Port = 9981 };

        var url1 = _sut.BuildUrlWithHeaderAuth(config, "/api/test");
        var url2 = _sut.BuildUrlWithHeaderAuth(config, "/api/test");

        Assert.Equal(url1, url2);
    }

    [Fact]
    public void MaskSensitiveData_WithCredentialsInUrl_PreservesStructure()
    {
        var config = new PluginConfiguration
        {
            Host = "tvheadend.local",
            Port = 9981,
            Username = "testuser",
            Password = "testpass",
            UseSSL = false
        };
        var url = "http://testuser:testpass@tvheadend.local:9981/api/test";

        var masked = _sut.MaskSensitiveData(url, config);

        Assert.Contains("tvheadend.local", masked);
        Assert.DoesNotContain("testpass", masked);
    }

    [Fact]
    public void MaskSensitiveData_WithEmptyPassword_StillWorks()
    {
        var config = new PluginConfiguration
        {
            Host = "tvheadend.local",
            Port = 9981,
            Username = "user",
            Password = "",
            UseSSL = false
        };
        var url = "http://tvheadend.local:9981/api/test";

        var masked = _sut.MaskSensitiveData(url, config);

        Assert.NotEmpty(masked);
        Assert.Contains("tvheadend.local", masked);
    }
}
