using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

/// <summary>
/// Tests for TvheadendUrlBuilder edge cases and URL construction.
/// </summary>
public class TvheadendUrlBuilderEdgeCaseTests
{
    [Fact]
    public void BuildUrl_IncludesHostnameAndPort()
    {
        var config = new PluginConfiguration { Host = "tvheadend.local", Port = 9981 };
        var url = TvheadendUrlBuilder.BuildUrl(config, "/api/test", "");

        Assert.NotEmpty(url);
        Assert.Contains("tvheadend.local", url);
        Assert.Contains("9981", url);
    }

    [Fact]
    public void BuildUrl_WithLocalhost_WorksCorrectly()
    {
        var config = new PluginConfiguration { Host = "localhost", Port = 9981 };
        var url = TvheadendUrlBuilder.BuildUrl(config, "/api/test", "");

        Assert.Contains("localhost", url);
    }

    [Fact]
    public void BuildUrl_WithIPAddress_WorksCorrectly()
    {
        var config = new PluginConfiguration { Host = "192.168.1.100", Port = 9981 };
        var url = TvheadendUrlBuilder.BuildUrl(config, "/api/test", "");

        Assert.Contains("192.168.1.100", url);
    }

    [Fact]
    public void BuildUrl_WithHttps_UsesCorrectProtocol()
    {
        var config = new PluginConfiguration
        {
            Host = "tvheadend.local",
            Port = 9981,
            UseSSL = true
        };

        var url = TvheadendUrlBuilder.BuildUrl(config, "/api/channel/grid", "");

        Assert.StartsWith("https://", url);
    }

    [Fact]
    public void BuildUrl_WithHttp_UsesCorrectProtocol()
    {
        var config = new PluginConfiguration
        {
            Host = "tvheadend.local",
            Port = 9981,
            UseSSL = false
        };

        var url = TvheadendUrlBuilder.BuildUrl(config, "/api/channel/grid", "");

        Assert.StartsWith("http://", url);
    }

    [Fact]
    public void BuildUrl_WithQueryParams_IncludesAllParams()
    {
        var config = new PluginConfiguration { Host = "localhost", Port = 9981 };
        var url = TvheadendUrlBuilder.BuildUrl(config, "/api/channel/grid", "limit=50&start=0");

        Assert.Contains("limit=50", url);
        Assert.Contains("start=0", url);
    }

    [Fact]
    public void BuildUrl_MultipleCallsWithSameConfig_ProduceConsistentResults()
    {
        var config = new PluginConfiguration { Host = "localhost", Port = 9981 };

        var url1 = TvheadendUrlBuilder.BuildUrl(config, "/api/test", "");
        var url2 = TvheadendUrlBuilder.BuildUrl(config, "/api/test", "");

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

        var masked = TvheadendUrlBuilder.MaskSensitiveData(url, config);

        Assert.Contains("tvheadend.local", masked);
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

        var masked = TvheadendUrlBuilder.MaskSensitiveData(url, config);

        Assert.NotEmpty(masked);
        Assert.Contains("tvheadend.local", masked);
    }
}
