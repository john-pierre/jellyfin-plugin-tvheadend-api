using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
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

    [Fact]
    public async Task FetchAllAsync_WhenResponseIsNull_ReturnsNull()
    {
        // Return JSON that deserializes to null
        var handler = new FakeHandler("null");
        using var client = new HttpClient(handler);

        var result = await GridFetcher.FetchAllAsync<TestGrid>(
            client, "http://test/api/grid", g => g.Total,
            NullLogger.Instance, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchAllAsync_WithInvalidUtf8_SanitizesAndDeserializes()
    {
        // Build a response with invalid UTF-8 byte sequence embedded in valid JSON
        var inner = new RawBytesHandler(() =>
        {
            // {"total":1,"entries":["hello\xff world"]}
            // The 0xFF byte is invalid UTF-8 and triggers the SanitizeUtf8 slow path
            var jsonStart = System.Text.Encoding.UTF8.GetBytes("{\"total\":1,\"entries\":[\"hello");
            var invalidByte = new byte[] { 0xFF };
            var jsonEnd = System.Text.Encoding.UTF8.GetBytes(" world\"]}");
            var combined = new byte[jsonStart.Length + invalidByte.Length + jsonEnd.Length];
            Buffer.BlockCopy(jsonStart, 0, combined, 0, jsonStart.Length);
            Buffer.BlockCopy(invalidByte, 0, combined, jsonStart.Length, invalidByte.Length);
            Buffer.BlockCopy(jsonEnd, 0, combined, jsonStart.Length + invalidByte.Length, jsonEnd.Length);
            return combined;
        });
        using var client = new HttpClient(inner);

        var result = await GridFetcher.FetchAllAsync<TestGrid>(
            client, "http://test/api/grid", g => g.Total,
            NullLogger.Instance, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, result.Total);
    }

    [Fact]
    public async Task FetchAllAsync_WhenTotalExactlyAtProbeLimit_ReturnsProbeResult()
    {
        var json = """{"total":50,"entries":["a"]}""";
        var handler = new FakeHandler(json);
        using var client = new HttpClient(handler);

        var result = await GridFetcher.FetchAllAsync<TestGrid>(
            client, "http://test/api/grid", g => g.Total,
            NullLogger.Instance, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(handler.RequestedUrls); // only probe, no full fetch
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

    /// <summary>
    /// Handler that returns raw bytes (potentially invalid UTF-8) for testing SanitizeUtf8.
    /// </summary>
    private sealed class RawBytesHandler : HttpMessageHandler
    {
        private readonly Func<byte[]> _bytesFactory;

        public RawBytesHandler(Func<byte[]> bytesFactory)
        {
            _bytesFactory = bytesFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bytes = _bytesFactory();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return Task.FromResult(response);
        }
    }
}
