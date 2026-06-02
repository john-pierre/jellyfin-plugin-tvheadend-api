// Tests for RelayUrlBuilder — URL construction, tokenized URLs, and effective base URL resolution.

using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using MediaBrowser.Controller;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Relay;

public class RelayUrlBuilderTests
{
    private static ConfigurationProvider CreateConfigProvider(PluginConfiguration? cfg = null)
    {
        return new ConfigurationProvider(() => cfg ?? new PluginConfiguration());
    }

    private static IServerApplicationHost CreateAppHost(string url = "http://localhost:8096")
    {
        var mock = new Mock<IServerApplicationHost>();
        mock.Setup(x => x.GetApiUrlForLocalAccess(It.IsAny<System.Net.IPAddress>(), It.IsAny<bool>())).Returns(url);
        return mock.Object;
    }

    [Fact]
    public void BuildImageRelayUrl_ReturnsCorrectUrl()
    {
        var sut = new RelayUrlBuilder(CreateAppHost(), CreateConfigProvider());
        var url = sut.BuildImageRelayUrl("imagecache/123");

        Assert.Equal("http://localhost:8096/api/tvheadend/images/imagecache/123", url);
    }

    [Fact]
    public void BuildImageRelayUrl_TrimsLeadingSlash()
    {
        var sut = new RelayUrlBuilder(CreateAppHost(), CreateConfigProvider());
        var url = sut.BuildImageRelayUrl("/imagecache/123");

        Assert.Equal("http://localhost:8096/api/tvheadend/images/imagecache/123", url);
    }

    [Fact]
    public void BuildImageRelayUrl_PreservesSlashes_NotPercentEncoded()
    {
        // Regression: encoding "/" as %2F produced a URL the ASP.NET {**path} catch-all could not
        // match (HTTP 404), which is why channel logos / EPG artwork failed to load while streams
        // (single-segment channel UUID) worked.
        var sut = new RelayUrlBuilder(CreateAppHost(), CreateConfigProvider());
        var url = sut.BuildImageRelayUrl("imagecache/1684");

        Assert.DoesNotContain("%2F", url);
        Assert.EndsWith("/api/tvheadend/images/imagecache/1684", url);
    }

    [Fact]
    public void BuildImageRelayUrl_ThrowsOnEmpty()
    {
        var sut = new RelayUrlBuilder(CreateAppHost(), CreateConfigProvider());
        Assert.Throws<ArgumentException>(() => sut.BuildImageRelayUrl(""));
    }

    [Fact]
    public void BuildStreamRelayUrl_WithProfile_IncludesQueryParam()
    {
        var sut = new RelayUrlBuilder(CreateAppHost(), CreateConfigProvider());
        var url = sut.BuildStreamRelayUrl("ch-42", "pass");

        Assert.Contains("stream/ch-42", url);
        Assert.Contains("profile=pass", url);
    }

    [Fact]
    public void BuildStreamRelayUrl_WithoutProfile_OmitsQueryParam()
    {
        var sut = new RelayUrlBuilder(CreateAppHost(), CreateConfigProvider());
        var url = sut.BuildStreamRelayUrl("ch-42", null);

        Assert.EndsWith("stream/ch-42", url);
        Assert.DoesNotContain("profile=", url);
    }

    [Fact]
    public void GetEffectiveBaseUrl_WithOverride_ReturnsOverride()
    {
        var config = new PluginConfiguration { RelayHostOverride = "https://custom.example.com:9999/" };
        var sut = new RelayUrlBuilder(CreateAppHost(), CreateConfigProvider(config));

        Assert.Equal("https://custom.example.com:9999", sut.GetEffectiveBaseUrl());
    }

    [Fact]
    public void GetEffectiveBaseUrl_WithoutOverride_ReturnsAutoDetected()
    {
        var sut = new RelayUrlBuilder(CreateAppHost("http://auto:8096"), CreateConfigProvider());

        Assert.Equal("http://auto:8096", sut.GetEffectiveBaseUrl());
    }

    [Fact]
    public async Task BuildTokenizedStreamRelayUrlAsync_WithTokenDisabled_FallsThroughToSync()
    {
        var tokenOptions = new RelayTokenOptions(
            new ConfigurationProvider(() => new PluginConfiguration { EnableRelayTokenSecurity = false }));
        var sut = new RelayUrlBuilder(CreateAppHost(), CreateConfigProvider(), null, tokenOptions);

        var url = await sut.BuildTokenizedStreamRelayUrlAsync("ch-1", "pass", null, null, null, CancellationToken.None);

        Assert.Contains("stream/ch-1", url);
        Assert.DoesNotContain("token=", url);
    }

    [Fact]
    public async Task BuildTokenizedStreamRelayUrlAsync_WithTokenEnabled_IncludesToken()
    {
        var config = new PluginConfiguration { EnableRelayTokenSecurity = true };
        var tokenOptions = new RelayTokenOptions(new ConfigurationProvider(() => config));
        var tokenService = new Mock<IRelayTokenService>();
        tokenService.Setup(x => x.IssueStreamTokenAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-token-123");

        var sut = new RelayUrlBuilder(CreateAppHost(), CreateConfigProvider(config), tokenService.Object, tokenOptions);
        var url = await sut.BuildTokenizedStreamRelayUrlAsync("ch-1", "pass", null, null, null, CancellationToken.None);

        Assert.Contains("relay/stream/ch-1", url);
        Assert.Contains("token=test-token-123", url);
        Assert.Contains("profile=pass", url);
    }

    [Fact]
    public async Task BuildTokenizedImageRelayUrlAsync_WithTokenEnabled_IncludesToken()
    {
        var config = new PluginConfiguration { EnableRelayTokenSecurity = true };
        var tokenOptions = new RelayTokenOptions(new ConfigurationProvider(() => config));
        var tokenService = new Mock<IRelayTokenService>();
        tokenService.Setup(x => x.IssueImageTokenAsync(
                It.IsAny<string>(), It.IsAny<MediaKind?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("img-token-456");

        var sut = new RelayUrlBuilder(CreateAppHost(), CreateConfigProvider(config), tokenService.Object, tokenOptions);
        var url = await sut.BuildTokenizedImageRelayUrlAsync("imagecache/1", MediaKind.Logo, null, CancellationToken.None);

        Assert.Contains("relay/images/", url);
        Assert.Contains("token=img-token-456", url);
    }

    [Fact]
    public async Task BuildTokenizedImageRelayUrlAsync_WithTokenDisabled_FallsThrough()
    {
        var config = new PluginConfiguration { EnableRelayTokenSecurity = false };
        var tokenOptions = new RelayTokenOptions(new ConfigurationProvider(() => config));
        var sut = new RelayUrlBuilder(CreateAppHost(), CreateConfigProvider(config), null, tokenOptions);

        var url = await sut.BuildTokenizedImageRelayUrlAsync("imagecache/1", null, null, CancellationToken.None);

        Assert.Contains("images/", url);
        Assert.DoesNotContain("token=", url);
    }
}
