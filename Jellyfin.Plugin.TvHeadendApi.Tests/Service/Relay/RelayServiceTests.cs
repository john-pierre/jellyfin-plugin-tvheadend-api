// Tests for the RelayService — verifies image/stream relay, header passthrough, error handling, and cancellation.

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Relay;

/// <summary>
/// Integration-style tests for <see cref="RelayService"/> using WireMock as a fake TVHeadend.
/// </summary>
public sealed class RelayServiceTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly RelayService _sut;

    public RelayServiceTests()
    {
        _server = WireMockServer.Start();

        var uri = new Uri(_server.Url!);
        var config = new PluginConfiguration
        {
            Host = uri.Host,
            Port = uri.Port,
            UseSSL = false,
            AllowAnonymousAccess = true,
        };

        var configProvider = new ConfigurationProvider(() => config);
        var urlBuilder = new UrlBuilder();
        var logger = NullLogger<RelayService>.Instance;
        var metricsService = new Mock<IRelayMetricsService>().Object;
        var activityTracker = new RelayActivityTracker();

        _sut = new RelayService(urlBuilder, configProvider, logger, metricsService, activityTracker, NullHealthService.Instance, new RelayImageCache(NullLogger<RelayImageCache>.Instance, configProvider, () => null));
    }

    public void Dispose()
    {
        _sut.Dispose();
        _server.Dispose();
    }

    [Fact]
    public async Task RelayImageAsync_ReturnsContentType()
    {
        _server.Given(Request.Create().WithPath("/imagecache/123").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "image/png")
                .WithBody(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));

        using var result = await _sut.RelayImageAsync("imagecache/123", CancellationToken.None);

        result.StatusCode.Should().Be(200);
        result.ContentType.Should().Contain("image/png");
        result.Body.Should().NotBeNull();
    }

    [Fact]
    public async Task RelayImageAsync_PreservesETagAndLastModified()
    {
        _server.Given(Request.Create().WithPath("/imagecache/456").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "image/jpeg")
                .WithHeader("ETag", "\"abc123\"")
                .WithHeader("Last-Modified", "Tue, 22 Apr 2025 10:00:00 GMT")
                .WithHeader("Cache-Control", "public, max-age=3600")
                .WithBody(new byte[] { 0xFF, 0xD8 }));

        using var result = await _sut.RelayImageAsync("imagecache/456", CancellationToken.None);

        result.StatusCode.Should().Be(200);
        result.ETag.Should().Contain("abc123");
        result.LastModified.Should().NotBeNullOrEmpty();
        result.CacheControl.Should().Contain("max-age");
    }

    [Fact]
    public async Task RelayImageAsync_CorrectsBogusContentType_BySniffingMagicBytes()
    {
        // TVHeadend serves images mislabeled as text/html (or with no type). The relay must sniff the
        // magic bytes and return a correct image/* type, otherwise Jellyfin's ConvertImageToLocal fails
        // with "Unable to convert any images to local" and channel logos / EPG art never load.
        _server.Given(Request.Create().WithPath("/imagecache/789").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/html")
                .WithBody(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01 }));

        using var result = await _sut.RelayImageAsync("imagecache/789", CancellationToken.None);

        result.StatusCode.Should().Be(200);
        result.ContentType.Should().Be("image/png");
    }

    [Fact]
    public async Task RelayStreamAsync_StartsQuickly()
    {
        _server.Given(Request.Create().WithPath("/stream/channel/*").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "video/MP2T")
                .WithBody(new byte[1024]));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var result = await _sut.RelayStreamAsync("test-uuid", null, CancellationToken.None);
        sw.Stop();

        result.StatusCode.Should().Be(200);
        result.ContentType.Should().Contain("video");
        result.Body.Should().NotBeNull();
        sw.ElapsedMilliseconds.Should().BeLessThan(5000, "stream relay should start within 5 seconds");
    }

    [Fact]
    public async Task RelayImageAsync_Returns502_WhenUpstreamUnavailable()
    {
        // Stop the server to simulate TVHeadend being down.
        var port = new Uri(_server.Url!).Port;
        _server.Stop();

        // Create a new service pointing to the stopped port.
        var config = new PluginConfiguration
        {
            Host = "127.0.0.1",
            Port = port,
            UseSSL = false,
            AllowAnonymousAccess = true,
        };
        var configProvider = new ConfigurationProvider(() => config);
        using var sut = new RelayService(new UrlBuilder(), configProvider, NullLogger<RelayService>.Instance, new Mock<IRelayMetricsService>().Object, new RelayActivityTracker(), NullHealthService.Instance, new RelayImageCache(NullLogger<RelayImageCache>.Instance, configProvider, () => null));

        using var result = await sut.RelayImageAsync("imagecache/999", CancellationToken.None);

        result.StatusCode.Should().BeOneOf(502, 504);
    }

    [Fact]
    public async Task RelayImageAsync_Returns404_WhenUpstreamReturns404()
    {
        _server.Given(Request.Create().WithPath("/imagecache/missing").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        using var result = await _sut.RelayImageAsync("imagecache/missing", CancellationToken.None);

        result.StatusCode.Should().Be(404);
        result.Body.Should().BeNull();
    }

    [Fact]
    public async Task RelayImageAsync_CancellationClosesUpstream()
    {
        _server.Given(Request.Create().WithPath("/imagecache/slow").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithDelay(TimeSpan.FromSeconds(30))
                .WithBody(new byte[10]));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        Func<Task> act = async () => await _sut.RelayImageAsync("imagecache/slow", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RelayStreamAsync_AppliesConfiguredProfile()
    {
        _server.Given(Request.Create().WithPath("/stream/channel/*").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "video/MP2T")
                .WithBody(new byte[16]));

        using var result = await _sut.RelayStreamAsync("ch-uuid", "matroska", CancellationToken.None);

        result.StatusCode.Should().Be(200);
        // Verify the request had profile query param.
        var logEntries = _server.LogEntries;
        logEntries.Should().ContainSingle(e =>
            e.RequestMessage.RawQuery != null &&
            e.RequestMessage.RawQuery.Contains("profile=matroska", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RelayImageAsync_RebuildsPipelineOnConfigChange()
    {
        // Start with server1.
        using var server1 = WireMockServer.Start();
        using var server2 = WireMockServer.Start();

        server1.Given(Request.Create().WithPath("/imagecache/1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "image/png")
                .WithBody(new byte[] { 0x01 }));

        server2.Given(Request.Create().WithPath("/imagecache/1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "image/jpeg")
                .WithBody(new byte[] { 0x02 }));

        var uri1 = new Uri(server1.Url!);
        var uri2 = new Uri(server2.Url!);

        // Mutable config — starts pointing at server1.
        var config = new PluginConfiguration
        {
            Host = uri1.Host,
            Port = uri1.Port,
            UseSSL = false,
            AllowAnonymousAccess = true,
        };

        var configProvider = new ConfigurationProvider(() => config);
        using var sut = new RelayService(new UrlBuilder(), configProvider, NullLogger<RelayService>.Instance, new Mock<IRelayMetricsService>().Object, new RelayActivityTracker(), NullHealthService.Instance, new RelayImageCache(NullLogger<RelayImageCache>.Instance, configProvider, () => null));

        // First request goes to server1.
        using var result1 = await sut.RelayImageAsync("imagecache/1", CancellationToken.None);
        result1.ContentType.Should().Contain("image/png");

        // Change config to point at server2 — no restart needed.
        config.Host = uri2.Host;
        config.Port = uri2.Port;

        // Second request should automatically rebuild pipeline and hit server2.
        using var result2 = await sut.RelayImageAsync("imagecache/1", CancellationToken.None);
        result2.ContentType.Should().Contain("image/jpeg");
    }

    [Fact]
    public async Task RelayImageAsync_AuthChangePreservesSocketPool()
    {
        // Verify that changing credentials still works (auth-only rebuild, socket pool preserved).
        _server.Given(Request.Create().WithPath("/imagecache/auth").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "image/png")
                .WithBody(new byte[] { 0xAA }));

        var uri = new Uri(_server.Url!);
        var config = new PluginConfiguration
        {
            Host = uri.Host,
            Port = uri.Port,
            UseSSL = false,
            AllowAnonymousAccess = true,
        };

        var configProvider = new ConfigurationProvider(() => config);
        using var sut = new RelayService(new UrlBuilder(), configProvider, NullLogger<RelayService>.Instance, new Mock<IRelayMetricsService>().Object, new RelayActivityTracker(), NullHealthService.Instance, new RelayImageCache(NullLogger<RelayImageCache>.Instance, configProvider, () => null));

        // First request — anonymous.
        using var result1 = await sut.RelayImageAsync("imagecache/auth", CancellationToken.None);
        result1.StatusCode.Should().Be(200);

        // Change only credentials — socket pool should survive.
        config.AllowAnonymousAccess = false;
        config.Username = "testuser";
        config.Password = "testpass";

        // Second request — same server, different auth. Should still succeed
        // because the socket pool (TCP connections) is preserved.
        using var result2 = await sut.RelayImageAsync("imagecache/auth", CancellationToken.None);
        result2.StatusCode.Should().Be(200);
        result2.ContentType.Should().Contain("image/png");
    }
}
