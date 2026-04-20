using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Comet;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Comet;

public class TvHeadendCometServiceTests
{
    private readonly UrlBuilder _urlBuilder = new();

    [Fact]
    public void BuildWebSocketUri_WithAuthTokenAndWebRoot_ReturnsWssUriWithAuthQuery()
    {
        var config = new PluginConfiguration
        {
            Host = "tvheadend.local",
            Port = 9981,
            UseSSL = true,
            Webroot = "/tvh",
            AllowAnonymousAccess = false,
            AuthToken = "token123",
        };

        var uri = TvHeadendCometService.BuildWebSocketUri(config, _urlBuilder);

        Assert.Equal("wss", uri.Scheme);
        Assert.Equal("tvheadend.local", uri.Host);
        Assert.Equal(9981, uri.Port);
        Assert.Equal("/tvh/comet/ws", uri.AbsolutePath);
        Assert.Equal("auth=token123", uri.Query.TrimStart('?'));
    }

    [Fact]
    public void BuildWebSocketUri_WithAnonymousAccess_ReturnsWsUriWithoutAuthQuery()
    {
        var config = new PluginConfiguration
        {
            Host = "127.0.0.1",
            Port = 9981,
            UseSSL = false,
            Webroot = "/",
            AllowAnonymousAccess = true,
            AuthToken = "token123",
        };

        var uri = TvHeadendCometService.BuildWebSocketUri(config, _urlBuilder);

        Assert.Equal("ws", uri.Scheme);
        Assert.Equal("/comet/ws", uri.AbsolutePath);
        Assert.True(string.IsNullOrEmpty(uri.Query));
    }

    [Fact]
    public void WebSocketSubProtocol_IsTvHeadendComet()
    {
        Assert.Equal("tvheadend-comet", TvHeadendCometService.WebSocketSubProtocol);
    }
}

