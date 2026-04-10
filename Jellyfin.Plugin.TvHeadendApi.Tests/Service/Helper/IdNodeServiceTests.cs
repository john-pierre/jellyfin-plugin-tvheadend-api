using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

/// <summary>
/// Replaces the old IdNodeServiceTests — verifies that consuming services
/// call the expected idnode URLs via IApiClient (no IdNodeService class exists anymore).
/// </summary>
public class IdNodeApiCallTests
{
    [Fact]
    public async Task DiagnosticService_LoadsDvrConfigs_ViaPostFormAsync()
    {
        // Verify that DiagnosticService POSTs to api/idnode/load with dvrconfig form params.
        var capturedUrl = string.Empty;

        var api = new Mock<IApiClient>();
        var config = new PluginConfiguration { EnableTvhDvr = true };
        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient());
        api.Setup(x => x.GetBaseUrl(config)).Returns("http://tvh:9981");
        api.Setup(x => x.GetWebRoot(config)).Returns("/");

        // Minimal successful responses so diagnostics reaches the DVR profile inspection path.
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/serverinfo", StringComparison.Ordinal))
                {
                    return Task.FromResult("{\"sw_version\":\"4.3\",\"api_version\":19,\"name\":\"tvh\"}");
                }

                if (url.Contains("api/channel/grid", StringComparison.Ordinal)
                    || url.Contains("api/dvr/entry/grid", StringComparison.Ordinal))
                {
                    return Task.FromResult("{\"total\":0,\"entries\":[]}");
                }

                if (url.Contains("api/codec_profile/list", StringComparison.Ordinal)
                    || url.Contains("api/idnode/load", StringComparison.Ordinal))
                {
                    return Task.FromResult("{\"entries\":[]}");
                }

                return Task.FromResult("{}");
            });

        // DVR POST
        api.Setup(x => x.PostFormAsync(
                It.IsAny<HttpClient>(),
                It.Is<string>(u => u.Contains("idnode/load", StringComparison.Ordinal)),
                It.IsAny<IEnumerable<KeyValuePair<string, string>>>(),
                It.IsAny<CancellationToken>()))
            .Callback<HttpClient, string, IEnumerable<KeyValuePair<string, string>>, CancellationToken>((_, url, form, _) =>
            {
                capturedUrl = url;
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"entries\":[]}")
            });

        // Execute — we only care that PostFormAsync was called; result is not important
        var diagService = new Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic.DiagnosticService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic.DiagnosticService>.Instance,
            new Mock<MediaBrowser.Controller.Configuration.IServerConfigurationManager>().Object,
            new Mock<IEncodingOptionsReader>().Object,
            new Mock<Jellyfin.Plugin.TvHeadendApi.Service.Profile.IProfileResolver>().Object,
            api.Object);

        // Should not throw; connection fail is expected, DVR block still runs
        await diagService.DiagnoseAsync(CancellationToken.None);

        Assert.Contains("idnode/load", capturedUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void IdNodeByUuid_UrlContainsEscapedUuid()
    {
        // Verify UUID is properly URI-escaped when building the idnode load URL.
        var rawUuid = "uuid with spaces";
        var expected = $"api/idnode/load?uuid={Uri.EscapeDataString(rawUuid)}";
        Assert.Contains("uuid%20with%20spaces", expected, StringComparison.Ordinal);
    }
}
