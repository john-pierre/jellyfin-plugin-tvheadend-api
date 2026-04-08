using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class TvheadendIdNodeServiceTests
{
    [Fact]
    public async Task LoadIdNodeByUuidAsync_BuildsExpectedGetUrl()
    {
        var handler = new CaptureHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"entries\":[]}")
            }
        };
        using var httpClient = new HttpClient(handler);
        var sut = new TvheadendIdNodeService();

        using var doc = await sut.LoadIdNodeByUuidAsync(httpClient, "http://tvh:9981", "/", "uuid 123", CancellationToken.None);

        Assert.NotNull(doc);
        Assert.Equal(HttpMethod.Get, handler.LastRequestMethod);
        Assert.Contains("api/idnode/load?uuid=", handler.LastRequestUrl);
        Assert.Contains("uuid", handler.LastRequestUrl);
    }

    [Fact]
    public async Task LoadDvrConfigsAsync_PostsExpectedFormValues()
    {
        var handler = new CaptureHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"entries\":[]}")
            }
        };
        using var httpClient = new HttpClient(handler);
        var sut = new TvheadendIdNodeService();

        using var doc = await sut.LoadDvrConfigsAsync(httpClient, "http://tvh:9981", "/", CancellationToken.None);

        Assert.NotNull(doc);
        Assert.Equal(HttpMethod.Post, handler.LastRequestMethod);
        Assert.Contains("api/idnode/load", handler.LastRequestUrl);
        Assert.Contains("enum=1", handler.LastBody);
        Assert.Contains("class=dvrconfig", handler.LastBody);
    }

    [Fact]
    public async Task LoadDvrConfigsAsync_WhenHttpNotSuccess_ThrowsHttpRequestException()
    {
        var handler = new CaptureHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("bad")
            }
        };
        using var httpClient = new HttpClient(handler);
        var sut = new TvheadendIdNodeService();

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            sut.LoadDvrConfigsAsync(httpClient, "http://tvh:9981", "/", CancellationToken.None));
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);
        public string LastRequestUrl { get; private set; } = string.Empty;
        public HttpMethod LastRequestMethod { get; private set; } = HttpMethod.Get;
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUrl = request.RequestUri?.ToString() ?? string.Empty;
            LastRequestMethod = request.Method;
            if (request.Content != null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return Response;
        }
    }
}
