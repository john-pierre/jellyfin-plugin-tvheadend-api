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
using Jellyfin.Plugin.TvHeadendApi.Service.Metrics;
using Jellyfin.Plugin.TvHeadendApi.Service.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly Mock<IHealthService> _healthServiceMock;
    private readonly PluginConfiguration _config;
    private readonly RelayActivityTracker _activityTracker;
    private readonly RelayController _controller;

    public RelayControllerStreamTests()
    {
        _relayServiceMock = new Mock<IRelayService>();
        _metricsMock = new Mock<IRelayMetricsService>();
        _urlBuilderMock = new Mock<IRelayUrlBuilder>();
        _tokenValidatorMock = new Mock<IRelayTokenValidator>();
        _healthServiceMock = new Mock<IHealthService>();

        _config = new PluginConfiguration();

        // These tests exercise the open /stream endpoint's response shaping (HEAD, Accept-Ranges,
        // Content-Length, content-type), not the token gate. Disable relay token security so the
        // open endpoint serves without a token; dedicated tests cover the security gate explicitly.
        _config.EnableRelayTokenSecurity = false;

        var configProvider = new ConfigurationProvider(() => _config);
        _activityTracker = new RelayActivityTracker();
        var tokenOptions = new RelayTokenOptions(configProvider);

        // Default: return Unknown health snapshot.
        _healthServiceMock.Setup(x => x.GetSnapshot()).Returns(new HealthSnapshot());

        // Create a SessionTracker for unit tests — it is in-memory only (no persistence).
        var activeSessionStore = new ActiveSessionStore();
        var sessionTracker = new SessionTracker(activeSessionStore, NullLogger<SessionTracker>.Instance);

        _urlBuilderMock.Setup(x => x.GetEffectiveBaseUrl()).Returns("http://localhost:8096");

        _controller = new RelayController(
            _relayServiceMock.Object,
            _metricsMock.Object,
            _activityTracker,
            sessionTracker,
            _urlBuilderMock.Object,
            _tokenValidatorMock.Object,
            tokenOptions,
            _healthServiceMock.Object,
            configProvider);

        // Setup HttpContext with response stream.
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };
    }

    // ── Constructor Guard Tests ─────────────────────────────────────────────

    [Fact]
    public void Constructor_NullRelay_ThrowsArgumentNullException()
    {
        var config = new PluginConfiguration();
        var configProvider = new ConfigurationProvider(() => config);
        var activityTracker = new RelayActivityTracker();
        var tokenOptions = new RelayTokenOptions(configProvider);
        var activeSessionStore = new ActiveSessionStore();
        var sessionTracker = new SessionTracker(activeSessionStore, NullLogger<SessionTracker>.Instance);

        var act = () => new RelayController(
            null!,
            _metricsMock.Object,
            activityTracker,
            sessionTracker,
            _urlBuilderMock.Object,
            _tokenValidatorMock.Object,
            tokenOptions,
            _healthServiceMock.Object,
            configProvider);

        act.Should().Throw<ArgumentNullException>().WithParameterName("relay");
    }

    [Fact]
    public void Constructor_NullMetrics_ThrowsArgumentNullException()
    {
        var config = new PluginConfiguration();
        var configProvider = new ConfigurationProvider(() => config);
        var activityTracker = new RelayActivityTracker();
        var tokenOptions = new RelayTokenOptions(configProvider);
        var activeSessionStore = new ActiveSessionStore();
        var sessionTracker = new SessionTracker(activeSessionStore, NullLogger<SessionTracker>.Instance);

        var act = () => new RelayController(
            _relayServiceMock.Object,
            null!,
            activityTracker,
            sessionTracker,
            _urlBuilderMock.Object,
            _tokenValidatorMock.Object,
            tokenOptions,
            _healthServiceMock.Object,
            configProvider);

        act.Should().Throw<ArgumentNullException>().WithParameterName("metrics");
    }

    [Fact]
    public void Constructor_NullActivityTracker_ThrowsArgumentNullException()
    {
        var config = new PluginConfiguration();
        var configProvider = new ConfigurationProvider(() => config);
        var tokenOptions = new RelayTokenOptions(configProvider);
        var activeSessionStore = new ActiveSessionStore();
        var sessionTracker = new SessionTracker(activeSessionStore, NullLogger<SessionTracker>.Instance);

        var act = () => new RelayController(
            _relayServiceMock.Object,
            _metricsMock.Object,
            null!,
            sessionTracker,
            _urlBuilderMock.Object,
            _tokenValidatorMock.Object,
            tokenOptions,
            _healthServiceMock.Object,
            configProvider);

        act.Should().Throw<ArgumentNullException>().WithParameterName("activityTracker");
    }

    [Fact]
    public void Constructor_NullSessionTracker_ThrowsArgumentNullException()
    {
        var config = new PluginConfiguration();
        var configProvider = new ConfigurationProvider(() => config);
        var activityTracker = new RelayActivityTracker();
        var tokenOptions = new RelayTokenOptions(configProvider);

        var act = () => new RelayController(
            _relayServiceMock.Object,
            _metricsMock.Object,
            activityTracker,
            null!,
            _urlBuilderMock.Object,
            _tokenValidatorMock.Object,
            tokenOptions,
            _healthServiceMock.Object,
            configProvider);

        act.Should().Throw<ArgumentNullException>().WithParameterName("sessionTracker");
    }

    [Fact]
    public void Constructor_NullUrlBuilder_ThrowsArgumentNullException()
    {
        var config = new PluginConfiguration();
        var configProvider = new ConfigurationProvider(() => config);
        var activityTracker = new RelayActivityTracker();
        var tokenOptions = new RelayTokenOptions(configProvider);
        var activeSessionStore = new ActiveSessionStore();
        var sessionTracker = new SessionTracker(activeSessionStore, NullLogger<SessionTracker>.Instance);

        var act = () => new RelayController(
            _relayServiceMock.Object,
            _metricsMock.Object,
            activityTracker,
            sessionTracker,
            null!,
            _tokenValidatorMock.Object,
            tokenOptions,
            _healthServiceMock.Object,
            configProvider);

        act.Should().Throw<ArgumentNullException>().WithParameterName("relayUrlBuilder");
    }

    [Fact]
    public void Constructor_NullTokenValidator_ThrowsArgumentNullException()
    {
        var config = new PluginConfiguration();
        var configProvider = new ConfigurationProvider(() => config);
        var activityTracker = new RelayActivityTracker();
        var tokenOptions = new RelayTokenOptions(configProvider);
        var activeSessionStore = new ActiveSessionStore();
        var sessionTracker = new SessionTracker(activeSessionStore, NullLogger<SessionTracker>.Instance);

        var act = () => new RelayController(
            _relayServiceMock.Object,
            _metricsMock.Object,
            activityTracker,
            sessionTracker,
            _urlBuilderMock.Object,
            null!,
            tokenOptions,
            _healthServiceMock.Object,
            configProvider);

        act.Should().Throw<ArgumentNullException>().WithParameterName("tokenValidator");
    }

    [Fact]
    public void Constructor_NullTokenOptions_ThrowsArgumentNullException()
    {
        var config = new PluginConfiguration();
        var configProvider = new ConfigurationProvider(() => config);
        var activityTracker = new RelayActivityTracker();
        var activeSessionStore = new ActiveSessionStore();
        var sessionTracker = new SessionTracker(activeSessionStore, NullLogger<SessionTracker>.Instance);

        var act = () => new RelayController(
            _relayServiceMock.Object,
            _metricsMock.Object,
            activityTracker,
            sessionTracker,
            _urlBuilderMock.Object,
            _tokenValidatorMock.Object,
            null!,
            _healthServiceMock.Object,
            configProvider);

        act.Should().Throw<ArgumentNullException>().WithParameterName("tokenOptions");
    }

    [Fact]
    public void Constructor_NullHealthService_ThrowsArgumentNullException()
    {
        var config = new PluginConfiguration();
        var configProvider = new ConfigurationProvider(() => config);
        var activityTracker = new RelayActivityTracker();
        var tokenOptions = new RelayTokenOptions(configProvider);
        var activeSessionStore = new ActiveSessionStore();
        var sessionTracker = new SessionTracker(activeSessionStore, NullLogger<SessionTracker>.Instance);

        var act = () => new RelayController(
            _relayServiceMock.Object,
            _metricsMock.Object,
            activityTracker,
            sessionTracker,
            _urlBuilderMock.Object,
            _tokenValidatorMock.Object,
            tokenOptions,
            null!,
            configProvider);

        act.Should().Throw<ArgumentNullException>().WithParameterName("healthService");
    }

    [Fact]
    public void Constructor_NullConfigProvider_ThrowsArgumentNullException()
    {
        var config = new PluginConfiguration();
        var configProvider = new ConfigurationProvider(() => config);
        var activityTracker = new RelayActivityTracker();
        var tokenOptions = new RelayTokenOptions(configProvider);
        var activeSessionStore = new ActiveSessionStore();
        var sessionTracker = new SessionTracker(activeSessionStore, NullLogger<SessionTracker>.Instance);

        var act = () => new RelayController(
            _relayServiceMock.Object,
            _metricsMock.Object,
            activityTracker,
            sessionTracker,
            _urlBuilderMock.Object,
            _tokenValidatorMock.Object,
            tokenOptions,
            _healthServiceMock.Object,
            null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("configProvider");
    }

    // ── HEAD Request Tests ──────────────────────────────────────────────────

    [Fact]
    public async Task GetStream_HeadRequest_SetsHeadersAndReturnsWithoutBody()
    {
        // Arrange — simulate HEAD request.
        _controller.HttpContext.Request.Method = HttpMethods.Head;

        // Act — HEAD should return immediately without calling upstream.
        await _controller.GetStream("ch-head", null, CancellationToken.None);

        // Assert — fixed headers set, no body written, no upstream call.
        _controller.Response.StatusCode.Should().Be(200);
        _controller.Response.ContentType.Should().Be("video/mp2t");
        _controller.Response.Headers["Accept-Ranges"].ToString().Should().Be("none");

        // Body stream should be empty (HEAD = no body).
        _controller.Response.Body.Position.Should().Be(0, "HEAD response must have empty body");

        // Verify no upstream stream was opened.
        _relayServiceMock.Verify(
            x => x.RelayStreamAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "HEAD must never open an upstream stream");
    }

    [Fact]
    public async Task GetStream_HeadRequest_WithNullContentType_UsesDefault()
    {
        _controller.HttpContext.Request.Method = HttpMethods.Head;

        // HEAD returns immediately without calling upstream — always uses default content type.
        await _controller.GetStream("ch-head-default", null, CancellationToken.None);

        _controller.Response.ContentType.Should().Be("video/mp2t",
            "default content type must be lowercase 'video/mp2t'");
    }

    // ── Security Gate Tests (regression for anonymous-stream bypass) ─────────

    [Fact]
    public async Task GetStream_OpenEndpoint_WithTokenSecurityOn_InvalidToken_IsRejected()
    {
        // With relay token security enabled, the open /stream endpoint must NOT serve anonymously —
        // it must require a valid token so the token gate cannot be bypassed.
        _config.EnableRelayTokenSecurity = true;
        _controller.HttpContext.Request.Method = HttpMethods.Get;
        _tokenValidatorMock
            .Setup(x => x.ValidateAsync(It.IsAny<string?>(), RelayType.Stream, "ch-secured", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RelayTokenValidationResult { IsValid = false, FailureReason = RelayTokenFailureReason.MissingToken });

        await _controller.GetStream("ch-secured", null, CancellationToken.None);

        _controller.Response.StatusCode.Should().BeOneOf(401, 403, 410);
        _relayServiceMock.Verify(
            x => x.RelayStreamAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the open endpoint must not stream without a valid token when security is enabled");
    }

    [Fact]
    public async Task GetStream_WithRelayDisabled_ReturnsServiceUnavailable()
    {
        _config.RelayEnabled = false;
        _controller.HttpContext.Request.Method = HttpMethods.Get;

        await _controller.GetStream("ch-disabled", null, CancellationToken.None);

        _controller.Response.StatusCode.Should().Be(503);
        _relayServiceMock.Verify(
            x => x.RelayStreamAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a disabled relay must not proxy streams");
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

    // ── Timing Mark Ordering Tests ──────────────────────────────────────────

    [Fact]
    public async Task GetStream_MarksFirstByteFromUpstream_BeforeClientWriteCompletes()
    {
        _controller.HttpContext.Request.Method = HttpMethods.Get;

        // Response body whose writes take a measurable amount of time, so the gap between
        // the upstream-read mark and the client-write mark becomes observable.
        _controller.HttpContext.Response.Body = new SlowWriteStream(TimeSpan.FromMilliseconds(50));

        var timing = new RelayTimingContext { RelayType = RelayType.Stream, ChannelId = "ch-timing" };
        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = "video/mp2t",
            Body = new MemoryStream(new byte[] { 0x47, 0x11, 0x22, 0x33 }),
            TimingContext = timing,
        };

        _relayServiceMock
            .Setup(x => x.RelayStreamAsync("ch-timing", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        await _controller.GetStream("ch-timing", null, CancellationToken.None);

        timing.FirstByteFromUpstreamTicks.Should().NotBeNull();
        timing.FirstByteToClientTicks.Should().NotBeNull();

        var deltaMs = (timing.FirstByteToClientTicks!.Value - timing.FirstByteFromUpstreamTicks!.Value)
            / (double)System.Diagnostics.Stopwatch.Frequency * 1000.0;
        deltaMs.Should().BeGreaterThanOrEqualTo(30,
            "first-byte-to-client must be marked only after the client write completed, " +
            "so the delta to first-byte-from-upstream reflects real downstream write latency");
    }

    [Fact]
    public async Task GetImage_MarksFirstByteToClient_OnlyWhenResponseBodyIsRead()
    {
        var timing = new RelayTimingContext { RelayType = RelayType.Image };
        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = "image/png",
            Body = new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47 }),
            TimingContext = timing,
        };

        _relayServiceMock
            .Setup(x => x.RelayImageAsync("imagecache/timing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        var result = await _controller.GetImage("imagecache/timing", CancellationToken.None);

        // The action returned, but nothing was streamed to the client yet — the mark must not be set.
        timing.FirstByteToClientTicks.Should().BeNull(
            "first-byte-to-client must not be marked before the response pipeline streams any byte");

        // ASP.NET's FileStreamResult writer reads from the wrapper to copy bytes to the client.
        var fileResult = result.Should().BeOfType<FileStreamResult>().Subject;
        var buffer = new byte[8];
        _ = await fileResult.FileStream.ReadAsync(buffer);

        timing.FirstByteToClientTicks.Should().NotBeNull(
            "the first successful read towards the client must set the to-client mark");
        timing.FirstByteFromUpstreamTicks.Should().BeNull(
            "the controller must not fabricate an upstream mark — the relay service records it when upstream bytes arrive");
    }

    // ── Image Relay Tests ───────────────────────────────────────────────────

    [Fact]
    public async Task GetImage_WithRelayDisabled_ReturnsServiceUnavailable()
    {
        _config.RelayEnabled = false;

        var result = await _controller.GetImage("imagecache/disabled", CancellationToken.None);

        result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(503);
        _relayServiceMock.Verify(
            x => x.RelayImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a disabled relay must not proxy images");
    }

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
    public async Task GetTokenSecuredImage_WithRelayDisabled_ReturnsServiceUnavailable()
    {
        _config.RelayEnabled = false;

        var result = await _controller.GetTokenSecuredImage("imagecache/disabled", CancellationToken.None);

        result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(503);
        _tokenValidatorMock.Verify(
            x => x.ValidateAsync(It.IsAny<string?>(), It.IsAny<RelayType>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the disabled-relay gate must run before token validation, mirroring the stream endpoint");
        _relayServiceMock.Verify(
            x => x.RelayImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a disabled relay must not proxy token-secured images");
    }

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

    // ── GetStream Exception Path Tests ──────────────────────────────────────

    [Fact]
    public async Task GetStream_OperationCancelled_DuringBodyRead_HandlesGracefully()
    {
        _controller.HttpContext.Request.Method = HttpMethods.Get;

        var throwingStream = new ThrowingStream(new OperationCanceledException());
        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = "video/mp2t",
            Body = throwingStream,
        };

        _relayServiceMock
            .Setup(x => x.RelayStreamAsync("ch-cancel", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        // Should not throw — OperationCanceledException is caught internally.
        await _controller.GetStream("ch-cancel", null, CancellationToken.None);

        // Status code was set to 200 before streaming started.
        _controller.Response.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task GetStream_IOException_DuringBodyRead_HandlesGracefully()
    {
        _controller.HttpContext.Request.Method = HttpMethods.Get;

        var throwingStream = new ThrowingStream(new IOException("Connection reset"));
        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = "video/mp2t",
            Body = throwingStream,
        };

        _relayServiceMock
            .Setup(x => x.RelayStreamAsync("ch-ioerr", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        // Should not throw — IOException is caught internally.
        await _controller.GetStream("ch-ioerr", null, CancellationToken.None);

        _controller.Response.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task GetStream_UnexpectedException_DuringBodyRead_HandlesGracefully()
    {
        _controller.HttpContext.Request.Method = HttpMethods.Get;

        var throwingStream = new ThrowingStream(new InvalidOperationException("Unexpected"));
        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = "video/mp2t",
            Body = throwingStream,
        };

        _relayServiceMock
            .Setup(x => x.RelayStreamAsync("ch-unexpected", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        // Should not throw — generic Exception is caught internally.
        await _controller.GetStream("ch-unexpected", null, CancellationToken.None);

        _controller.Response.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task GetStream_WithProfile_PassesProfileToRelayService()
    {
        _controller.HttpContext.Request.Method = HttpMethods.Get;

        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = "video/mp2t",
            Body = new MemoryStream(Array.Empty<byte>()),
        };

        _relayServiceMock
            .Setup(x => x.RelayStreamAsync("ch-profile", "pass", It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        await _controller.GetStream("ch-profile", "pass", CancellationToken.None);

        _relayServiceMock.Verify(
            x => x.RelayStreamAsync("ch-profile", "pass", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetStream_UpstreamContentType_PassedThrough()
    {
        _controller.HttpContext.Request.Method = HttpMethods.Get;

        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = "video/h264",
            Body = new MemoryStream(Array.Empty<byte>()),
        };

        _relayServiceMock
            .Setup(x => x.RelayStreamAsync("ch-ct", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        await _controller.GetStream("ch-ct", null, CancellationToken.None);

        _controller.Response.ContentType.Should().Be("video/h264",
            "upstream Content-Type must be passed through to client");
    }

    [Fact]
    public async Task GetStream_NullChannelId_Returns400()
    {
        await _controller.GetStream(null!, null, CancellationToken.None);

        _controller.Response.StatusCode.Should().Be(400);
    }

    // ── Relay Status — Branch Coverage ──────────────────────────────────────

    [Fact]
    public void GetRelayStatus_RelayDisabled_ReturnsDisabledStatus()
    {
        _config.RelayEnabled = false;

        var result = _controller.GetRelayStatus();

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var value = ok.Value!;
        value.GetType().GetProperty("Status")!.GetValue(value).Should().Be("disabled");
        value.GetType().GetProperty("Message")!.GetValue(value).Should().NotBeNull();
    }

    [Fact]
    public void GetRelayStatus_NotConfigured_ReturnsErrorStatus()
    {
        _config.RelayEnabled = true;
        _config.Host = "";
        _config.Port = 0;

        var result = _controller.GetRelayStatus();

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var value = ok.Value!;
        value.GetType().GetProperty("Status")!.GetValue(value).Should().Be("error");
    }

    [Fact]
    public void GetRelayStatus_Healthy_ReturnsOkStatus()
    {
        _config.RelayEnabled = true;
        _config.Host = "tvh.local";
        _config.Port = 9981;

        _healthServiceMock.Setup(x => x.GetSnapshot())
            .Returns(new HealthSnapshot { Status = HealthStatus.Healthy });

        var result = _controller.GetRelayStatus();

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var value = ok.Value!;
        value.GetType().GetProperty("Status")!.GetValue(value).Should().Be("ok");
    }

    [Fact]
    public void GetRelayStatus_Degraded_ReturnsDegradedStatus()
    {
        _config.RelayEnabled = true;
        _config.Host = "tvh.local";
        _config.Port = 9981;

        _healthServiceMock.Setup(x => x.GetSnapshot())
            .Returns(new HealthSnapshot { Status = HealthStatus.Degraded });

        var result = _controller.GetRelayStatus();

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var value = ok.Value!;
        value.GetType().GetProperty("Status")!.GetValue(value).Should().Be("degraded");
    }

    [Fact]
    public void GetRelayStatus_Recovering_ReturnsDegradedStatus()
    {
        _config.RelayEnabled = true;
        _config.Host = "tvh.local";
        _config.Port = 9981;

        _healthServiceMock.Setup(x => x.GetSnapshot())
            .Returns(new HealthSnapshot { Status = HealthStatus.Recovering });

        var result = _controller.GetRelayStatus();

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var value = ok.Value!;
        value.GetType().GetProperty("Status")!.GetValue(value).Should().Be("degraded");
    }

    [Fact]
    public void GetRelayStatus_Unreachable_ReturnsErrorStatus()
    {
        _config.RelayEnabled = true;
        _config.Host = "tvh.local";
        _config.Port = 9981;

        _healthServiceMock.Setup(x => x.GetSnapshot())
            .Returns(new HealthSnapshot { Status = HealthStatus.Unreachable });

        var result = _controller.GetRelayStatus();

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var value = ok.Value!;
        value.GetType().GetProperty("Status")!.GetValue(value).Should().Be("error");
    }

    [Fact]
    public void GetRelayStatus_CircuitOpen_ReturnsErrorStatus()
    {
        _config.RelayEnabled = true;
        _config.Host = "tvh.local";
        _config.Port = 9981;

        _healthServiceMock.Setup(x => x.GetSnapshot())
            .Returns(new HealthSnapshot { Status = HealthStatus.CircuitOpen });

        var result = _controller.GetRelayStatus();

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var value = ok.Value!;
        value.GetType().GetProperty("Status")!.GetValue(value).Should().Be("error");
    }

    [Fact]
    public void GetRelayStatus_Timeout_ReturnsErrorStatus()
    {
        _config.RelayEnabled = true;
        _config.Host = "tvh.local";
        _config.Port = 9981;

        _healthServiceMock.Setup(x => x.GetSnapshot())
            .Returns(new HealthSnapshot { Status = HealthStatus.Timeout });

        var result = _controller.GetRelayStatus();

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var value = ok.Value!;
        value.GetType().GetProperty("Status")!.GetValue(value).Should().Be("error");
    }

    [Fact]
    public void GetRelayStatus_AuthFailed_ReturnsErrorWithFailureReason()
    {
        _config.RelayEnabled = true;
        _config.Host = "tvh.local";
        _config.Port = 9981;

        _healthServiceMock.Setup(x => x.GetSnapshot())
            .Returns(new HealthSnapshot
            {
                Status = HealthStatus.AuthFailed,
                LastFailureReason = FailureReason.AuthFailed,
            });

        var result = _controller.GetRelayStatus();

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var value = ok.Value!;
        value.GetType().GetProperty("Status")!.GetValue(value).Should().Be("error");
        var message = (string?)value.GetType().GetProperty("Message")!.GetValue(value);
        message.Should().Contain("AuthFailed");
    }

    [Fact]
    public void GetRelayStatus_Unknown_ReturnsUnknownStatus()
    {
        _config.RelayEnabled = true;
        _config.Host = "tvh.local";
        _config.Port = 9981;

        _healthServiceMock.Setup(x => x.GetSnapshot())
            .Returns(new HealthSnapshot { Status = HealthStatus.Unknown });

        var result = _controller.GetRelayStatus();

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var value = ok.Value!;
        value.GetType().GetProperty("Status")!.GetValue(value).Should().Be("unknown");
    }

    // ── GetImage Exception Path Tests ───────────────────────────────────────

    [Fact]
    public async Task GetImage_OperationCancelled_PropagatesException()
    {
        _relayServiceMock
            .Setup(x => x.RelayImageAsync("imagecache/cancel", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => _controller.GetImage("imagecache/cancel", CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetImage_UnexpectedException_PropagatesException()
    {
        _relayServiceMock
            .Setup(x => x.RelayImageAsync("imagecache/boom", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Boom"));

        var act = () => _controller.GetImage("imagecache/boom", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Token-Secured Edge Cases ────────────────────────────────────────────

    [Fact]
    public async Task GetTokenSecuredImage_ExpiredToken_ReturnsGone()
    {
        _tokenValidatorMock
            .Setup(x => x.ValidateAsync(It.IsAny<string?>(), RelayType.Image, "imagecache/expired", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RelayTokenValidationResult { IsValid = false, FailureReason = RelayTokenFailureReason.Expired });

        var result = await _controller.GetTokenSecuredImage("imagecache/expired", CancellationToken.None);

        result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(410);
    }

    [Fact]
    public async Task GetTokenSecuredImage_RevokedToken_ReturnsForbidden()
    {
        _tokenValidatorMock
            .Setup(x => x.ValidateAsync(It.IsAny<string?>(), RelayType.Image, "imagecache/revoked", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RelayTokenValidationResult { IsValid = false, FailureReason = RelayTokenFailureReason.Revoked });

        var result = await _controller.GetTokenSecuredImage("imagecache/revoked", CancellationToken.None);

        result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task GetTokenSecuredImage_WhitespacePath_ReturnsBadRequest()
    {
        var result = await _controller.GetTokenSecuredImage("   ", CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetTokenSecuredStream_HeadRequest_InvalidToken_ReturnsUnauthorized()
    {
        _controller.HttpContext.Request.Method = HttpMethods.Head;

        _tokenValidatorMock
            .Setup(x => x.ValidateAsync(It.IsAny<string?>(), RelayType.Stream, "ch-head-invalid", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RelayTokenValidationResult { IsValid = false, FailureReason = RelayTokenFailureReason.MissingToken });

        await _controller.GetTokenSecuredStream("ch-head-invalid", null, CancellationToken.None);

        _controller.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task GetTokenSecuredStream_WhitespaceChannelId_Returns400()
    {
        await _controller.GetTokenSecuredStream("   ", null, CancellationToken.None);

        _controller.Response.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task GetTokenSecuredStream_RevokedToken_ReturnsForbidden()
    {
        _tokenValidatorMock
            .Setup(x => x.ValidateAsync(It.IsAny<string?>(), RelayType.Stream, "ch-revoked", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RelayTokenValidationResult { IsValid = false, FailureReason = RelayTokenFailureReason.Revoked });

        await _controller.GetTokenSecuredStream("ch-revoked", null, CancellationToken.None);

        _controller.Response.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task GetTokenSecuredStream_ValidToken_WithProfile_PassesProfile()
    {
        _controller.HttpContext.Request.Method = HttpMethods.Get;

        _tokenValidatorMock
            .Setup(x => x.ValidateAsync(It.IsAny<string?>(), RelayType.Stream, "ch-prof", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RelayTokenValidationResult { IsValid = true });

        var relayResult = new RelayResult
        {
            StatusCode = 200,
            ContentType = "video/mp2t",
            Body = new MemoryStream(Array.Empty<byte>()),
        };

        _relayServiceMock
            .Setup(x => x.RelayStreamAsync("ch-prof", "matroska", It.IsAny<CancellationToken>()))
            .ReturnsAsync(relayResult);

        await _controller.GetTokenSecuredStream("ch-prof", "matroska", CancellationToken.None);

        _relayServiceMock.Verify(
            x => x.RelayStreamAsync("ch-prof", "matroska", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Helper Types ────────────────────────────────────────────────────────

    /// <summary>Response stream whose writes take a fixed delay — makes downstream write latency observable.</summary>
    private sealed class SlowWriteStream : MemoryStream
    {
        private readonly TimeSpan _delay;

        public SlowWriteStream(TimeSpan delay)
        {
            _delay = delay;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delay, cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    /// <summary>Stream that throws a configurable exception on read — for testing error paths.</summary>
    private sealed class ThrowingStream : MemoryStream
    {
        private readonly Exception _exception;

        public ThrowingStream(Exception exception)
        {
            _exception = exception;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw _exception;

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw _exception;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw _exception;
    }
}
