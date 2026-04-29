// Tests for RelayController stream endpoint — covers HEAD support, Accept-Ranges header,
// Content-Length suppression, and content-type fallback for Apple/Android/Smart TV compatibility.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.TvHeadendApi.Api;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Api;

/// <summary>
/// Unit tests for <see cref="RelayController"/> stream endpoint behavior.
/// Covers HEAD support, Accept-Ranges, Content-Length suppression, and content-type defaults.
/// </summary>
public sealed class RelayControllerStreamTests
{
    private readonly Mock<IRelayService> _relayServiceMock;
    private readonly Mock<IRelayMetricsService> _metricsMock;
    private readonly Mock<IRelayUrlBuilder> _urlBuilderMock;
    private readonly Mock<IRelayTokenValidator> _tokenValidatorMock;
    private readonly RelayController _controller;

    public RelayControllerStreamTests()
    {
        _relayServiceMock = new Mock<IRelayService>();
        _metricsMock = new Mock<IRelayMetricsService>();
        _urlBuilderMock = new Mock<IRelayUrlBuilder>();
        _tokenValidatorMock = new Mock<IRelayTokenValidator>();

        var config = new PluginConfiguration();
        var configProvider = new ConfigurationProvider(() => config);
        var activityTracker = new RelayActivityTracker();
        var tokenOptions = new RelayTokenOptions(configProvider);
        var healthService = NullHealthService.Instance;

        _urlBuilderMock.Setup(x => x.GetEffectiveBaseUrl()).Returns("http://localhost:8096");

        _controller = new RelayController(
            _relayServiceMock.Object,
            _metricsMock.Object,
            activityTracker,
            _urlBuilderMock.Object,
            _tokenValidatorMock.Object,
            tokenOptions,
            healthService,
            configProvider);

        // Setup HttpContext with response stream.
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };
    }

    // ── HEAD Request Tests ──────────────────────────────────────────────────

    [Fact]
    public async Task GetStream_HeadRequest_SetsHeadersAndReturnsWithoutBody()
    {
        // Arrange — simulate HEAD request.
        _controller.HttpContext.Request.Method = HttpMethods.Head;

        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = "video/mp2t",
            ContentLength = 12345,
            AcceptRanges = "bytes",
            Body = new MemoryStream(new byte[100]),
        };

        _relayServiceMock
            .Setup(x => x.RelayStreamAsync("ch-head", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        // Act
        await _controller.GetStream("ch-head", null, CancellationToken.None);

        // Assert — headers set, no body written.
        _controller.Response.StatusCode.Should().Be(200);
        _controller.Response.ContentType.Should().Be("video/mp2t");
        _controller.Response.Headers["Accept-Ranges"].ToString().Should().Be("bytes");

        // Body stream should be empty (HEAD = no body).
        _controller.Response.Body.Position.Should().Be(0, "HEAD response must have empty body");
    }

    [Fact]
    public async Task GetStream_HeadRequest_WithNullContentType_UsesDefault()
    {
        _controller.HttpContext.Request.Method = HttpMethods.Head;

        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = null,
            Body = new MemoryStream(new byte[10]),
        };

        _relayServiceMock
            .Setup(x => x.RelayStreamAsync("ch-head-default", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        await _controller.GetStream("ch-head-default", null, CancellationToken.None);

        _controller.Response.ContentType.Should().Be("video/mp2t",
            "default content type must be lowercase 'video/mp2t'");
    }

    // ── Accept-Ranges Header Tests ──────────────────────────────────────────

    [Fact]
    public async Task GetStream_WithUpstreamAcceptRanges_PassesThrough()
    {
        _controller.HttpContext.Request.Method = HttpMethods.Get;

        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = "video/mp2t",
            AcceptRanges = "bytes",
            Body = new MemoryStream(Array.Empty<byte>()),
        };

        _relayServiceMock
            .Setup(x => x.RelayStreamAsync("ch-ranges", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        await _controller.GetStream("ch-ranges", null, CancellationToken.None);

        _controller.Response.Headers["Accept-Ranges"].ToString().Should().Be("bytes");
    }

    [Fact]
    public async Task GetStream_WithoutUpstreamAcceptRanges_SetsNone()
    {
        _controller.HttpContext.Request.Method = HttpMethods.Get;

        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = "video/mp2t",
            AcceptRanges = null,
            Body = new MemoryStream(Array.Empty<byte>()),
        };

        _relayServiceMock
            .Setup(x => x.RelayStreamAsync("ch-no-ranges", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        await _controller.GetStream("ch-no-ranges", null, CancellationToken.None);

        _controller.Response.Headers["Accept-Ranges"].ToString().Should().Be("none",
            "live streams without upstream range support must explicitly set 'none'");
    }

    // ── Content-Length Suppression Tests ─────────────────────────────────────

    [Fact]
    public async Task GetStream_NeverSetsContentLength_ForLiveStreams()
    {
        _controller.HttpContext.Request.Method = HttpMethods.Get;

        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = "video/mp2t",
            ContentLength = 999999, // TVHeadend sends Content-Length — should be suppressed.
            Body = new MemoryStream(new byte[100]),
        };

        _relayServiceMock
            .Setup(x => x.RelayStreamAsync("ch-no-cl", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        await _controller.GetStream("ch-no-cl", null, CancellationToken.None);

        _controller.Response.ContentLength.Should().BeNull(
            "Content-Length must NOT be set on live stream responses — " +
            "ExoPlayer, VLC, and other clients use its absence as 'infinite stream' signal");
    }

    // ── Default Content-Type Tests ──────────────────────────────────────────

    [Fact]
    public async Task GetStream_NullContentType_DefaultsToVideoMp2tLowercase()
    {
        _controller.HttpContext.Request.Method = HttpMethods.Get;

        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = null, // No Content-Type from upstream.
            Body = new MemoryStream(new byte[10]),
        };

        _relayServiceMock
            .Setup(x => x.RelayStreamAsync("ch-default-ct", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        await _controller.GetStream("ch-default-ct", null, CancellationToken.None);

        _controller.Response.ContentType.Should().Be("video/mp2t",
            "default must be 'video/mp2t' (IANA lowercase) not 'video/MP2T'");
    }

    // ── Error Handling Tests ────────────────────────────────────────────────

    [Fact]
    public async Task GetStream_EmptyChannelId_Returns400()
    {
        await _controller.GetStream("", null, CancellationToken.None);

        _controller.Response.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task GetStream_WhitespaceChannelId_Returns400()
    {
        await _controller.GetStream("   ", null, CancellationToken.None);

        _controller.Response.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task GetStream_NullBody_ReturnsUpstreamStatusCode()
    {
        _controller.HttpContext.Request.Method = HttpMethods.Get;

        var relayResult = new RelayResult
        {
            StatusCode = 502,
            Body = null,
        };

        _relayServiceMock
            .Setup(x => x.RelayStreamAsync("ch-err", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        await _controller.GetStream("ch-err", null, CancellationToken.None);

        _controller.Response.StatusCode.Should().Be(502);
    }

    [Fact]
    public async Task GetStream_NullBody_504_ReturnsGatewayTimeout()
    {
        _controller.HttpContext.Request.Method = HttpMethods.Get;

        var relayResult = new RelayResult
        {
            StatusCode = 504,
            Body = null,
        };

        _relayServiceMock
            .Setup(x => x.RelayStreamAsync("ch-timeout", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        await _controller.GetStream("ch-timeout", null, CancellationToken.None);

        _controller.Response.StatusCode.Should().Be(504);
    }

    // ── Body Streaming Tests ────────────────────────────────────────────────

    [Fact]
    public async Task GetStream_GetRequest_StreamsBodyToResponse()
    {
        _controller.HttpContext.Request.Method = HttpMethods.Get;

        var payload = new byte[] { 0x47, 0x00, 0x01, 0x02 };
        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = "video/mp2t",
            Body = new MemoryStream(payload),
        };

        _relayServiceMock
            .Setup(x => x.RelayStreamAsync("ch-body", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        await _controller.GetStream("ch-body", null, CancellationToken.None);

        _controller.Response.Body.Position = 0;
        var written = new byte[payload.Length];
        var read = await _controller.Response.Body.ReadAsync(written);

        read.Should().Be(payload.Length);
        written.Should().BeEquivalentTo(payload, "relay must stream body to response unchanged");
    }

    [Fact]
    public async Task GetStream_GetRequest_EmptyBody_WritesNothing()
    {
        _controller.HttpContext.Request.Method = HttpMethods.Get;

        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = "video/mp2t",
            Body = new MemoryStream(Array.Empty<byte>()),
        };

        _relayServiceMock
            .Setup(x => x.RelayStreamAsync("ch-empty", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        await _controller.GetStream("ch-empty", null, CancellationToken.None);

        _controller.Response.StatusCode.Should().Be(200);
        ((MemoryStream)_controller.Response.Body).Length.Should().Be(0);
    }

    // ── Token-Secured Endpoint Tests ────────────────────────────────────────

    [Fact]
    public async Task GetTokenSecuredStream_EmptyChannelId_Returns400()
    {
        await _controller.GetTokenSecuredStream("", null, CancellationToken.None);

        _controller.Response.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task GetTokenSecuredStream_InvalidToken_ReturnsUnauthorized()
    {
        _tokenValidatorMock
            .Setup(x => x.ValidateAsync(It.IsAny<string?>(), RelayType.Stream, "ch-invalid", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RelayTokenValidationResult { IsValid = false, FailureReason = RelayTokenFailureReason.MissingToken });

        await _controller.GetTokenSecuredStream("ch-invalid", null, CancellationToken.None);

        _controller.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task GetTokenSecuredStream_ExpiredToken_ReturnsGone()
    {
        _tokenValidatorMock
            .Setup(x => x.ValidateAsync(It.IsAny<string?>(), RelayType.Stream, "ch-expired", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RelayTokenValidationResult { IsValid = false, FailureReason = RelayTokenFailureReason.Expired });

        await _controller.GetTokenSecuredStream("ch-expired", null, CancellationToken.None);

        _controller.Response.StatusCode.Should().Be(410);
    }

    [Fact]
    public async Task GetTokenSecuredStream_ValidToken_DelegatesToGetStream()
    {
        _controller.HttpContext.Request.Method = HttpMethods.Get;

        _tokenValidatorMock
            .Setup(x => x.ValidateAsync(It.IsAny<string?>(), RelayType.Stream, "ch-valid", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RelayTokenValidationResult { IsValid = true });

        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = "video/mp2t",
            Body = new MemoryStream(new byte[] { 0x47 }),
        };

        _relayServiceMock
            .Setup(x => x.RelayStreamAsync("ch-valid", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        await _controller.GetTokenSecuredStream("ch-valid", null, CancellationToken.None);

        _controller.Response.StatusCode.Should().Be(200);
    }

    // ── HEAD on Token-Secured Endpoint ──────────────────────────────────────

    [Fact]
    public async Task GetTokenSecuredStream_HeadRequest_ValidToken_ReturnsHeadersOnly()
    {
        _controller.HttpContext.Request.Method = HttpMethods.Head;

        _tokenValidatorMock
            .Setup(x => x.ValidateAsync(It.IsAny<string?>(), RelayType.Stream, "ch-head-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RelayTokenValidationResult { IsValid = true });

        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = "video/mp2t",
            AcceptRanges = "none",
            Body = new MemoryStream(new byte[50]),
        };

        _relayServiceMock
            .Setup(x => x.RelayStreamAsync("ch-head-token", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        await _controller.GetTokenSecuredStream("ch-head-token", null, CancellationToken.None);

        _controller.Response.StatusCode.Should().Be(200);
        _controller.Response.ContentType.Should().Be("video/mp2t");
        ((MemoryStream)_controller.Response.Body).Length.Should().Be(0, "HEAD must not write body");
    }

    // ── Image Relay Tests ───────────────────────────────────────────────────

    [Fact]
    public async Task GetImage_EmptyPath_ReturnsBadRequest()
    {
        var result = await _controller.GetImage("", CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetImage_WhitespacePath_ReturnsBadRequest()
    {
        var result = await _controller.GetImage("   ", CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetImage_NullBody_ReturnsStatusCode()
    {
        var relayResult = new RelayResult
        {
            StatusCode = 404,
            Body = null,
        };

        _relayServiceMock
            .Setup(x => x.RelayImageAsync("imagecache/missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        var result = await _controller.GetImage("imagecache/missing", CancellationToken.None);

        result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task GetImage_WithBody_ReturnsFileStreamResult()
    {
        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = "image/png",
            ETag = "\"etag-123\"",
            LastModified = "Tue, 22 Apr 2025 10:00:00 GMT",
            CacheControl = "public, max-age=3600",
            Body = new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47 }),
        };

        _relayServiceMock
            .Setup(x => x.RelayImageAsync("imagecache/logo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        var result = await _controller.GetImage("imagecache/logo", CancellationToken.None);

        var fileResult = result.Should().BeOfType<FileStreamResult>().Subject;
        fileResult.ContentType.Should().Be("image/png");
        fileResult.EnableRangeProcessing.Should().BeTrue();
    }

    [Fact]
    public async Task GetImage_WithBody_NullContentType_DefaultsToOctetStream()
    {
        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = null,
            Body = new MemoryStream(new byte[] { 0x00 }),
        };

        _relayServiceMock
            .Setup(x => x.RelayImageAsync("imagecache/unknown", It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        var result = await _controller.GetImage("imagecache/unknown", CancellationToken.None);

        var fileResult = result.Should().BeOfType<FileStreamResult>().Subject;
        fileResult.ContentType.Should().Be("application/octet-stream");
    }

    [Fact]
    public async Task GetImage_SetsPassthroughHeaders()
    {
        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = "image/jpeg",
            ETag = "\"img-etag\"",
            LastModified = "Mon, 01 Jan 2024 00:00:00 GMT",
            CacheControl = "public, max-age=7200",
            Body = new MemoryStream(new byte[] { 0xFF, 0xD8 }),
        };

        _relayServiceMock
            .Setup(x => x.RelayImageAsync("imagecache/headers", It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        await _controller.GetImage("imagecache/headers", CancellationToken.None);

        _controller.Response.Headers["ETag"].ToString().Should().Contain("img-etag");
        _controller.Response.Headers["Last-Modified"].ToString().Should().Contain("2024");
        _controller.Response.Headers["Cache-Control"].ToString().Should().Contain("max-age=7200");
    }

    [Fact]
    public async Task GetImage_NoCacheControl_SetsDefault()
    {
        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = "image/png",
            CacheControl = null,
            Body = new MemoryStream(new byte[] { 0x89 }),
        };

        _relayServiceMock
            .Setup(x => x.RelayImageAsync("imagecache/nocache", It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        await _controller.GetImage("imagecache/nocache", CancellationToken.None);

        _controller.Response.Headers["Cache-Control"].ToString().Should().Contain("public");
        _controller.Response.Headers["Cache-Control"].ToString().Should().Contain("stale-while-revalidate");
    }

    // ── Token-Secured Image Endpoint ────────────────────────────────────────

    [Fact]
    public async Task GetTokenSecuredImage_EmptyPath_ReturnsBadRequest()
    {
        var result = await _controller.GetTokenSecuredImage("", CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetTokenSecuredImage_InvalidToken_ReturnsUnauthorized()
    {
        _tokenValidatorMock
            .Setup(x => x.ValidateAsync(It.IsAny<string?>(), RelayType.Image, "imagecache/secured", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RelayTokenValidationResult { IsValid = false, FailureReason = RelayTokenFailureReason.TokenNotFound });

        var result = await _controller.GetTokenSecuredImage("imagecache/secured", CancellationToken.None);

        result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task GetTokenSecuredImage_ValidToken_DelegatesToGetImage()
    {
        _tokenValidatorMock
            .Setup(x => x.ValidateAsync(It.IsAny<string?>(), RelayType.Image, "imagecache/valid", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RelayTokenValidationResult { IsValid = true });

        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = "image/png",
            Body = new MemoryStream(new byte[] { 0x89, 0x50 }),
        };

        _relayServiceMock
            .Setup(x => x.RelayImageAsync("imagecache/valid", It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        var result = await _controller.GetTokenSecuredImage("imagecache/valid", CancellationToken.None);

        result.Should().BeOfType<FileStreamResult>();
    }

    // ── Relay Status Endpoint ───────────────────────────────────────────────

    [Fact]
    public void GetRelayStatus_ReturnsOkWithStatusObject()
    {
        var result = _controller.GetRelayStatus();

        result.Result.Should().BeOfType<OkObjectResult>();
    }
}

