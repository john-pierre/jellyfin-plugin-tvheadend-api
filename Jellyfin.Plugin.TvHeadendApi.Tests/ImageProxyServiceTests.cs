using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.Images;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class ImageProxyServiceTests
{
    [Fact]
    public async Task ProxyImageAsync_WithoutPath_ReturnsBadRequest()
    {
        var sut = new ImageProxyService(NullLogger<ImageProxyService>.Instance, new FakeGateway(_ => throw new InvalidOperationException()));

        var result = await sut.ProxyImageAsync(null, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ProxyImageAsync_WhenGatewayThrowsHttpRequestException_Returns502()
    {
        var sut = new ImageProxyService(
            NullLogger<ImageProxyService>.Instance,
            new FakeGateway(_ => throw new HttpRequestException("no route")));

        var result = await sut.ProxyImageAsync("imagecache/1", CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, objectResult.StatusCode);
    }

    [Fact]
    public async Task ProxyImageAsync_WhenTvhReturns404_MapsStatusCode()
    {
        var sut = new ImageProxyService(
            NullLogger<ImageProxyService>.Instance,
            new FakeGateway(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var result = await sut.ProxyImageAsync("imagecache/1", CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, objectResult.StatusCode);
    }

    [Fact]
    public async Task ProxyImageAsync_WhenTvhReturnsImage_ReturnsFileResult()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(new byte[] { 1, 2, 3 })),
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");

        var sut = new ImageProxyService(
            NullLogger<ImageProxyService>.Instance,
            new FakeGateway(_ => response));

        var result = await sut.ProxyImageAsync("imagecache/1", CancellationToken.None);

        var fileResult = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("image/png", fileResult.ContentType);
    }

    private sealed class FakeGateway : ITvheadendImageGateway
    {
        private readonly Func<string, HttpResponseMessage> _resolver;

        public FakeGateway(Func<string, HttpResponseMessage> resolver)
        {
            _resolver = resolver;
        }

        public Task<HttpResponseMessage> FetchImageAsync(string imagePath, CancellationToken cancellationToken)
            => Task.FromResult(_resolver(imagePath));
    }
}
