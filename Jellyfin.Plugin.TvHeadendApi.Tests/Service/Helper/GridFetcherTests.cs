using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

/// <summary>
/// Tests for GridFetcher.FetchAllAsync — probe-only, full-fetch, null response, and URL separator paths.
/// </summary>
public class GridFetcherTests
{
    [Fact]
    public async Task FetchAllAsync_WhenTotalBelowProbeLimit_ReturnsProbeResult()
    {
        var json = """{"total":3,"entries":["a","b","c"]}""";
        var handler = new FakeHandler(json);
        using var client = new HttpClient(handler);

        var result = await GridFetcher.FetchAllAsync<TestGrid>(
            client, "http://test/api/grid", g => g.Total,
            NullLogger.Instance, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(3, result.Total);
        Assert.Single(handler.RequestedUrls); // only probe
    }

    [Fact]
    public async Task FetchAllAsync_WhenTotalAboveProbeLimit_MakesTwoRequests()
    {
        var handler = new FakeHandler(_ =>
        {
            // First call: probe
            return """{"total":100,"entries":["a"]}""";
        });
        using var client = new HttpClient(handler);

        var result = await GridFetcher.FetchAllAsync<TestGrid>(
            client, "http://test/api/grid", g => g.Total,
            NullLogger.Instance, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, handler.RequestedUrls.Count);
        Assert.Contains("limit=50", handler.RequestedUrls[0]);
        Assert.Contains("limit=100", handler.RequestedUrls[1]);
    }

    [Fact]
    public async Task FetchAllAsync_UrlWithQueryParam_UsesAmpersand()
    {
        var json = """{"total":1,"entries":[]}""";
        var handler = new FakeHandler(json);
        using var client = new HttpClient(handler);

        await GridFetcher.FetchAllAsync<TestGrid>(
            client, "http://test/api/grid?filter=x", g => g.Total,
            NullLogger.Instance, CancellationToken.None);

        Assert.Contains("&start=0", handler.RequestedUrls[0]);
    }

    [Fact]
    public async Task FetchAllAsync_UrlWithoutQueryParam_UsesQuestionMark()
    {
        var json = """{"total":1,"entries":[]}""";
        var handler = new FakeHandler(json);
        using var client = new HttpClient(handler);

        await GridFetcher.FetchAllAsync<TestGrid>(
            client, "http://test/api/grid", g => g.Total,
            NullLogger.Instance, CancellationToken.None);

        Assert.Contains("?start=0", handler.RequestedUrls[0]);
    }

    [Fact]
    public async Task FetchAllAsync_WhenHttpFails_ThrowsHttpRequestException()
    {
        var handler = new FakeHandler(HttpStatusCode.InternalServerError);
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            GridFetcher.FetchAllAsync<TestGrid>(
                client, "http://test/api/grid", g => g.Total,
                NullLogger.Instance, CancellationToken.None));
    }

    internal sealed class TestGrid
    {
        [JsonPropertyName("total")]
        public int Total { get; init; }

        [JsonPropertyName("entries")]
        public string[] Entries { get; init; } = Array.Empty<string>();
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<string, string>? _responseFactory;
        private readonly string? _fixedJson;
        private readonly HttpStatusCode _statusCode;

        public System.Collections.Generic.List<string> RequestedUrls { get; } = new();

        public FakeHandler(string fixedJson)
        {
            _fixedJson = fixedJson;
            _statusCode = HttpStatusCode.OK;
        }

        public FakeHandler(Func<string, string> responseFactory)
        {
            _responseFactory = responseFactory;
            _statusCode = HttpStatusCode.OK;
        }

        public FakeHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            RequestedUrls.Add(url);

            if (_statusCode != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(_statusCode));
            }

            var json = _responseFactory?.Invoke(url) ?? _fixedJson ?? "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}

