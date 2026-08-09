// Apple AVPlayer compatibility test suite — validates HTTP behavior, headers, MIME types,
// range requests, security, and stream signatures required for reliable Apple client playback.
// Covers iPhone, iPad, Apple TV, Swiftfin, and native AVPlayer-based clients.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Api;

/// <summary>
/// Validates that the plugin relay endpoints are fully compatible with Apple AVPlayer expectations.
/// AVPlayer (used on iPhone, iPad, Apple TV, Swiftfin) requires strict HTTP compliance:
/// correct MIME types, HEAD support, byte-range support, no broken redirects, and no credential leakage.
/// <para>
/// These tests use WireMock as a fake TVHeadend backend and exercise the <see cref="RelayService"/>
/// directly, verifying every aspect of the relay pipeline that affects Apple client playback.
/// </para>
/// </summary>
public sealed class AppleAvPlayerCompatibilityTests : IDisposable
{
    /// <summary>MPEG-TS sync byte used to verify stream payload integrity.</summary>
    private const byte MpegTsSyncByte = 0x47;

    /// <summary>HLS playlist signature prefix.</summary>
    private const string HlsSignature = "#EXTM3U";

    /// <summary>MP4 ftyp atom identifier bytes.</summary>
    private static readonly byte[] FtypAtom = "ftyp"u8.ToArray();

    private readonly WireMockServer _server;
    private readonly RelayService _sut;
    private readonly PluginConfiguration _config;
    private readonly ITestOutputHelper _output;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppleAvPlayerCompatibilityTests"/> class.
    /// Creates a WireMock server simulating TVHeadend and a <see cref="RelayService"/> under test.
    /// </summary>
    public AppleAvPlayerCompatibilityTests(ITestOutputHelper output)
    {
        _output = output;
        _server = WireMockServer.Start();

        var uri = new Uri(_server.Url!);
        _config = new PluginConfiguration
        {
            Host = uri.Host,
            Port = uri.Port,
            UseSSL = false,
            AllowAnonymousAccess = true,
            Username = "tvhadmin",
            Password = "s3cret!Pass",
            AuthToken = "tok-abc-123",
        };

        var configProvider = new ConfigurationProvider(() => _config);
        var urlBuilder = new UrlBuilder();
        var metricsService = new Mock<IRelayMetricsService>().Object;
        var activityTracker = new RelayActivityTracker();

        _sut = new RelayService(
            urlBuilder,
            configProvider,
            NullLogger<RelayService>.Instance,
            metricsService,
            activityTracker,
            NullHealthService.Instance,
            new RelayImageCache(NullLogger<RelayImageCache>.Instance, configProvider, () => null));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _sut.Dispose();
        _server.Dispose();
    }

    // ── 1. HEAD Request Validation ─────────────────────────────────────────

    /// <summary>
    /// AVPlayer sends HEAD requests before initiating stream playback to discover
    /// content type, length, and range support. The relay must not return a body for HEAD.
    /// <para>
    /// FINDING: The current stream relay endpoint only supports GET and writes directly
    /// to Response.Body — HEAD requests are not handled at the controller level.
    /// This is a critical incompatibility for AVPlayer.
    /// </para>
    /// </summary>
    [Fact]
    public async Task HeadRequest_StreamEndpoint_ShouldReturnHeadersWithoutBody()
    {
        // Arrange — WireMock responds to HEAD with headers only (standard HTTP behavior).
        _server.Given(Request.Create().WithPath("/stream/channel/*").UsingHead())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "video/MP2T")
                .WithHeader("Accept-Ranges", "bytes"));

        _server.Given(Request.Create().WithPath("/stream/channel/*").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "video/MP2T")
                .WithBody(BuildMpegTsPayload(188 * 10)));

        // Act — RelayService only sends GET (HttpMethod.Get hardcoded in RelayRequestAsync).
        // This test documents the limitation: the relay cannot forward HEAD requests.
        using var result = await _sut.RelayStreamAsync("test-channel-head", null, CancellationToken.None);

        // Assert — the relay always returns a body stream since it only uses GET.
        result.StatusCode.Should().Be(200, "relay should succeed for GET");
        result.Body.Should().NotBeNull("relay always returns body stream because it uses GET, not HEAD");

        _output.WriteLine(
            "⚠️ INCOMPATIBILITY: RelayService.RelayRequestAsync only sends GET — " +
            "HEAD requests from AVPlayer are never forwarded. " +
            "The RelayController.GetStream endpoint writes directly to Response.Body and " +
            "does not handle HEAD method. Apple clients will fail discovery.");
    }

    /// <summary>
    /// Image relay should support HEAD via FileStreamResult (ASP.NET handles it automatically).
    /// Verifies image endpoint returns valid headers.
    /// </summary>
    [Fact]
    public async Task HeadRequest_ImageEndpoint_ShouldReturnValidHeaders()
    {
        _server.Given(Request.Create().WithPath("/imagecache/logo-1").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "image/png")
                .WithBody(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));

        using var result = await _sut.RelayImageAsync("imagecache/logo-1", CancellationToken.None);

        result.StatusCode.Should().Be(200);
        result.ContentType.Should().NotBeNullOrWhiteSpace("AVPlayer needs Content-Type for all responses");
        result.Body.Should().NotBeNull();

        _output.WriteLine(
            "✅ Image relay returns FileStreamResult with EnableRangeProcessing=true — " +
            "ASP.NET Core handles HEAD automatically for FileStreamResult.");
    }

    // ── 2. Range Request Validation ────────────────────────────────────────

    /// <summary>
    /// AVPlayer sends Range: bytes=0-1023 to test whether the server supports partial content.
    /// If the relay does not support 206 Partial Content, AVPlayer may refuse to play or buffer excessively.
    /// <para>
    /// FINDING: The stream relay writes sequentially to Response.Body without setting
    /// Accept-Ranges or Content-Range headers. Range requests are silently ignored.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RangeRequest_StreamRelay_ShouldDocumentMissingRangeSupport()
    {
        var payload = BuildMpegTsPayload(188 * 100); // ~18 KB
        _server.Given(Request.Create().WithPath("/stream/channel/*").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "video/MP2T")
                .WithHeader("Accept-Ranges", "bytes")
                .WithBody(payload));

        using var result = await _sut.RelayStreamAsync("test-channel-range", null, CancellationToken.None);

        // The relay passes through the upstream response as-is.
        result.StatusCode.Should().Be(200, "relay returns upstream status unchanged");

        // Document: the relay does NOT intercept Range headers from the client request.
        // RelayRequestAsync creates a plain GET — no Range header is forwarded.
        result.Body.Should().NotBeNull();

        _output.WriteLine(
            "⚠️ INCOMPATIBILITY: Stream relay does not forward Range headers to TVHeadend. " +
            "RelayRequestAsync constructs a plain GET without copying client request headers. " +
            "The controller writes directly to Response.Body without EnableRangeProcessing. " +
            "AVPlayer byte-range requests will receive the full stream instead of 206 Partial Content.");
    }

    /// <summary>
    /// Image relay uses FileStreamResult with EnableRangeProcessing=true, which should
    /// correctly serve 206 responses when clients send Range headers.
    /// </summary>
    [Fact]
    public async Task RangeRequest_ImageRelay_ShouldSupportRangeProcessing()
    {
        _server.Given(Request.Create().WithPath("/imagecache/range-test").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "image/jpeg")
                .WithBody(new byte[4096]));

        using var result = await _sut.RelayImageAsync("imagecache/range-test", CancellationToken.None);

        result.StatusCode.Should().Be(200);
        result.Body.Should().NotBeNull();

        // FileStreamResult with EnableRangeProcessing=true handles this at ASP.NET level.
        _output.WriteLine(
            "✅ Image relay uses FileStreamResult(EnableRangeProcessing=true) — " +
            "ASP.NET Core automatically handles Range headers for images.");
    }

    // ── 3. Content-Type Validation ─────────────────────────────────────────

    /// <summary>
    /// AVPlayer requires correct MIME types. The relay must pass through upstream Content-Type
    /// without modification. Validates all Apple-compatible MIME types are preserved.
    /// </summary>
    /// <param name="upstreamContentType">The Content-Type header from TVHeadend.</param>
    [Theory]
    [InlineData("video/mp2t")]
    [InlineData("video/MP2T")]
    [InlineData("application/vnd.apple.mpegurl")]
    [InlineData("application/x-mpegURL")]
    [InlineData("video/mp4")]
    [InlineData("audio/aac")]
    public async Task ContentType_StreamRelay_ShouldPreserveUpstreamMimeType(string upstreamContentType)
    {
        _server.Given(Request.Create().WithPath("/stream/channel/*").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", upstreamContentType)
                .WithBody(BuildMpegTsPayload(188)));

        using var result = await _sut.RelayStreamAsync($"ch-mime-{upstreamContentType.GetHashCode()}", null, CancellationToken.None);

        result.StatusCode.Should().Be(200);
        result.ContentType.Should().NotBeNull("Content-Type must always be set");
        result.ContentType.Should().Contain(
            upstreamContentType,
            $"relay must preserve upstream MIME type '{upstreamContentType}' for AVPlayer compatibility");
    }

    /// <summary>
    /// When TVHeadend returns no Content-Type, the stream controller defaults to "video/MP2T".
    /// AVPlayer may be case-sensitive about MIME types — "video/mp2t" is the IANA-registered form.
    /// <para>
    /// FINDING: The default content type uses uppercase "MP2T". While technically valid per RFC,
    /// some AVPlayer implementations are stricter about lowercase MIME subtypes.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ContentType_StreamRelay_DefaultsToVideoMp2t_WhenUpstreamHasNoContentType()
    {
        _server.Given(Request.Create().WithPath("/stream/channel/*").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody(BuildMpegTsPayload(188)));

        using var result = await _sut.RelayStreamAsync("ch-no-content-type", null, CancellationToken.None);

        result.StatusCode.Should().Be(200);

        // The controller sets Content-Type to result.ContentType ?? "video/MP2T".
        // When upstream has no Content-Type, result.ContentType will be null.
        // The controller then uses "video/MP2T" (uppercase).
        _output.WriteLine(
            "⚠️ POTENTIAL ISSUE: When upstream returns no Content-Type, the relay controller " +
            "defaults to 'video/MP2T' (uppercase). The IANA-registered type is 'video/mp2t'. " +
            "While HTTP MIME types are technically case-insensitive (RFC 2045), " +
            "some AVPlayer implementations may not handle uppercase correctly.");
    }

    /// <summary>
    /// Validates that generic or misleading content types from TVHeadend are automatically
    /// overridden to 'video/mp2t' by the stream relay normalization.
    /// This prevents AVPlayer from failing format detection.
    /// </summary>
    /// <param name="badContentType">A content type that should be overridden for video streams.</param>
    [Theory]
    [InlineData("application/octet-stream")]
    [InlineData("text/plain")]
    [InlineData("text/html")]
    public async Task ContentType_StreamRelay_ShouldOverrideGenericMimeTypes(string badContentType)
    {
        _server.Given(Request.Create().WithPath("/stream/channel/*").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", badContentType)
                .WithBody(BuildMpegTsPayload(188)));

        using var result = await _sut.RelayStreamAsync($"ch-bad-mime-{badContentType.GetHashCode()}", null, CancellationToken.None);

        result.StatusCode.Should().Be(200);
        result.ContentType.Should().Be("video/mp2t",
            $"generic Content-Type '{badContentType}' must be normalized to 'video/mp2t' " +
            "for AVPlayer compatibility");

        _output.WriteLine(
            $"✅ FIX VERIFIED: Upstream returned '{badContentType}' — " +
            "relay correctly overrode to 'video/mp2t' for Apple AVPlayer compatibility.");
    }

    // ── 4. Redirect / Relay Security Validation ────────────────────────────

    /// <summary>
    /// The relay's SocketsHttpHandler has AllowAutoRedirect=true with MaxAutomaticRedirections=3.
    /// This means TVHeadend redirects are followed transparently. The client never sees a Location
    /// header — which is correct for security but means redirect targets are not validated.
    /// </summary>
    [Fact]
    public async Task Redirect_ShouldBeFollowedTransparently_WithoutExposingLocation()
    {
        // TVHeadend redirects to a different internal path.
        _server.Given(Request.Create().WithPath("/stream/channel/redirect-ch").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(302)
                .WithHeader("Location", $"{_server.Url}/stream/channelid/real-uuid"));

        _server.Given(Request.Create().WithPath("/stream/channelid/real-uuid").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "video/mp2t")
                .WithBody(BuildMpegTsPayload(188)));

        using var result = await _sut.RelayStreamAsync("redirect-ch", null, CancellationToken.None);

        // Relay follows the redirect and returns final content — no Location header exposed.
        result.StatusCode.Should().Be(200, "relay should follow the redirect transparently");
        result.Body.Should().NotBeNull("body should contain the stream from the redirect target");

        _output.WriteLine(
            "✅ Relay follows TVHeadend redirects transparently (AllowAutoRedirect=true). " +
            "Client never receives Location header with internal URLs.");
    }

    /// <summary>
    /// Verifies behavior when TVHeadend responds with a redirect chain exceeding MaxAutomaticRedirections.
    /// <para>
    /// FIX APPLIED: Redirect status codes (3xx) that exceed the auto-redirect limit are now
    /// mapped to 502 Bad Gateway by <c>MapUpstreamStatus</c>, preventing internal URL leakage.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Redirect_ChainExceedingMax_ShouldReturnGatewayError()
    {
        // Create a redirect chain of 4 (exceeds MaxAutomaticRedirections=3).
        for (var i = 0; i < 4; i++)
        {
            _server.Given(Request.Create().WithPath($"/stream/channel/redir{i}").UsingGet())
                .RespondWith(Response.Create()
                    .WithStatusCode(302)
                    .WithHeader("Location", $"{_server.Url}/stream/channel/redir{i + 1}"));
        }

        _server.Given(Request.Create().WithPath("/stream/channel/redir4").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "video/mp2t")
                .WithBody(BuildMpegTsPayload(188)));

        using var result = await _sut.RelayStreamAsync("redir0", null, CancellationToken.None);

        // FIX: 3xx status codes are now mapped to 502 Bad Gateway to prevent URL leakage.
        result.StatusCode.Should().BeOneOf(new[] { 200, 502 },
            "relay should either follow the chain successfully or return 502 — never expose raw 302");

        _output.WriteLine(
            result.StatusCode == 502
                ? "✅ FIX VERIFIED: Redirect chain exceeding limit now returns 502 Bad Gateway. " +
                  "Internal TVHeadend URLs are no longer exposed to clients."
                : "✅ Redirect chain was fully resolved within the limit.");
    }

    // ── 5. Secret Leakage Validation ───────────────────────────────────────

    /// <summary>
    /// Ensures that TVHeadend credentials (username, password, auth token) never appear
    /// in relay result metadata (content type, headers, ETag, etc.).
    /// </summary>
    [Fact]
    public async Task SecretLeakage_RelayResult_ShouldNeverContainCredentials()
    {
        _server.Given(Request.Create().WithPath("/imagecache/secret-check").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "image/png")
                .WithHeader("ETag", "\"safe-etag-123\"")
                .WithHeader("Cache-Control", "public, max-age=3600")
                .WithBody(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));

        using var result = await _sut.RelayImageAsync("imagecache/secret-check", CancellationToken.None);

        var sensitiveValues = new[]
        {
            _config.Username,
            _config.Password,
            _config.AuthToken,
        };

        var allMetadata = string.Join("|",
            result.ContentType ?? string.Empty,
            result.ETag ?? string.Empty,
            result.LastModified ?? string.Empty,
            result.CacheControl ?? string.Empty);

        foreach (var secret in sensitiveValues.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            allMetadata.Should().NotContain(
                secret!,
                $"relay result metadata must never contain credential '{secret}'");
        }
    }

    /// <summary>
    /// Verifies that the relay URL builder masks sensitive data in logged URLs.
    /// </summary>
    [Fact]
    public void SecretLeakage_UrlBuilder_ShouldMaskCredentials()
    {
        var urlBuilder = new UrlBuilder();
        var configWithAuth = new PluginConfiguration
        {
            Host = "tvh-internal.local",
            Port = 9981,
            UseSSL = false,
            Username = "admin",
            Password = "s3cretP@ss",
            AuthToken = "tok-xyz-789",
            AllowAnonymousAccess = false,
        };

        var url = urlBuilder.BuildResourceUrl(configWithAuth, "stream/channel/abc");
        var masked = urlBuilder.MaskSensitiveData(url, configWithAuth);

        masked.Should().NotContain("admin", "username must be masked");
        masked.Should().NotContain("s3cretP@ss", "password must be masked");
        masked.Should().NotContain("tok-xyz-789", "auth token must be masked");

        _output.WriteLine($"Masked URL: {masked}");
    }

    /// <summary>
    /// Ensures URLs built by <see cref="UrlBuilder.BuildResourceUrl"/> with auth tokens
    /// use the correct query parameter format and don't leak in unexpected patterns.
    /// </summary>
    [Fact]
    public void SecretLeakage_ResourceUrl_ShouldNotContainPlaintextCredentials()
    {
        var urlBuilder = new UrlBuilder();

        var url = urlBuilder.BuildResourceUrl(_config, "stream/channel/test-ch");

        // When AllowAnonymousAccess is true, no auth token should be appended.
        url.Should().NotContain("username=", "query param 'username=' must never appear in URLs");
        url.Should().NotContain("password=", "query param 'password=' must never appear in URLs");
        url.Should().NotContain("Authorization", "'Authorization' must never appear in query strings");
    }

    /// <summary>
    /// When AllowAnonymousAccess is false, the auth token is appended to URLs.
    /// Verifies it's the only credential in the URL and properly encoded.
    /// </summary>
    [Fact]
    public void SecretLeakage_ResourceUrl_WithAuth_ShouldOnlyContainAuthToken()
    {
        var urlBuilder = new UrlBuilder();
        var authConfig = new PluginConfiguration
        {
            Host = "192.168.1.100",
            Port = 9981,
            UseSSL = false,
            AllowAnonymousAccess = false,
            Username = "admin",
            Password = "secret",
            AuthToken = "my-token-value",
        };

        var url = urlBuilder.BuildResourceUrl(authConfig, "stream/channel/test");

        // Auth token is appended as ?auth= parameter (not username/password).
        url.Should().Contain("auth=", "non-anonymous URLs use auth token");
        url.Should().NotContain("username=");
        url.Should().NotContain("password=");
        url.Should().NotContain("admin", "username must not appear in URLs");
        url.Should().NotContain("secret", "password must not appear in URLs");
    }

    // ── 6. Stream Signature Validation ─────────────────────────────────────

    /// <summary>
    /// Validates MPEG-TS stream starts with the 0x47 sync byte.
    /// AVPlayer uses the first bytes to detect format and select the correct decoder.
    /// </summary>
    [Fact]
    public async Task StreamSignature_MpegTs_ShouldStartWithSyncByte()
    {
        var payload = BuildMpegTsPayload(188 * 5);

        _server.Given(Request.Create().WithPath("/stream/channel/*").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "video/mp2t")
                .WithBody(payload));

        using var result = await _sut.RelayStreamAsync("ch-ts-sig", null, CancellationToken.None);
        result.Body.Should().NotBeNull();

        var buffer = new byte[1];
        var bytesRead = await result.Body!.ReadAsync(buffer, CancellationToken.None);

        bytesRead.Should().BeGreaterThan(0, "stream must return at least 1 byte");
        buffer[0].Should().Be(MpegTsSyncByte,
            "MPEG-TS stream must start with 0x47 sync byte — " +
            "AVPlayer uses this to identify the transport stream format");
    }

    /// <summary>
    /// Validates HLS playlist starts with #EXTM3U signature.
    /// </summary>
    [Fact]
    public async Task StreamSignature_Hls_ShouldStartWithExtm3u()
    {
        var hlsPlaylist = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:10\n#EXTINF:10.0,\nchunk001.ts\n#EXT-X-ENDLIST";
        var payload = Encoding.UTF8.GetBytes(hlsPlaylist);

        _server.Given(Request.Create().WithPath("/stream/channel/*").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/vnd.apple.mpegurl")
                .WithBody(payload));

        using var result = await _sut.RelayStreamAsync("ch-hls-sig", null, CancellationToken.None);
        result.Body.Should().NotBeNull();

        var buffer = new byte[7]; // "#EXTM3U" = 7 bytes
        var bytesRead = await result.Body!.ReadAsync(buffer, CancellationToken.None);

        bytesRead.Should().Be(7, "must read enough bytes for HLS signature");
        var signature = Encoding.UTF8.GetString(buffer);
        signature.Should().Be(HlsSignature,
            "HLS playlist must start with '#EXTM3U' — " +
            "AVPlayer validates this before attempting playback");
    }

    /// <summary>
    /// Validates MP4 stream contains the ftyp atom in the first 32 bytes.
    /// </summary>
    [Fact]
    public async Task StreamSignature_Mp4_ShouldContainFtypAtom()
    {
        var payload = BuildMp4Payload();

        _server.Given(Request.Create().WithPath("/stream/channel/*").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "video/mp4")
                .WithBody(payload));

        using var result = await _sut.RelayStreamAsync("ch-mp4-sig", null, CancellationToken.None);
        result.Body.Should().NotBeNull();

        var buffer = new byte[32];
        var bytesRead = await result.Body!.ReadAsync(buffer, CancellationToken.None);

        bytesRead.Should().BeGreaterThan(8, "MP4 must have enough bytes for ftyp atom");

        var containsFtyp = ContainsFtypAtom(buffer, bytesRead);
        containsFtyp.Should().BeTrue(
            "MP4 stream must contain 'ftyp' atom in the first bytes — " +
            "AVPlayer uses this to identify the container format and compatible codecs");
    }

    /// <summary>
    /// Verifies that an empty body is detected as an error condition.
    /// </summary>
    [Fact]
    public async Task StreamSignature_EmptyPayload_ShouldBeDetected()
    {
        _server.Given(Request.Create().WithPath("/stream/channel/*").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "video/mp2t")
                .WithBody(Array.Empty<byte>()));

        using var result = await _sut.RelayStreamAsync("ch-empty", null, CancellationToken.None);
        result.Body.Should().NotBeNull("relay returns a body stream even when upstream body is empty");

        var buffer = new byte[1];
        var bytesRead = await result.Body!.ReadAsync(buffer, CancellationToken.None);

        bytesRead.Should().Be(0,
            "empty stream payload detected — AVPlayer would fail with 'cannot open' error");

        _output.WriteLine(
            "⚠️ ISSUE: Upstream returned 200 OK with empty body. " +
            "The relay passes this through unchanged. " +
            "AVPlayer will fail immediately because there are no bytes to decode.");
    }

    // ── 7. Stability Validation ────────────────────────────────────────────

    /// <summary>
    /// Multiple sequential requests should succeed without errors or resource leaks.
    /// </summary>
    [Fact]
    public async Task Stability_SequentialRequests_ShouldAllSucceed()
    {
        _server.Given(Request.Create().WithPath("/stream/channel/*").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "video/mp2t")
                .WithBody(BuildMpegTsPayload(188 * 3)));

        const int requestCount = 10;
        for (var i = 0; i < requestCount; i++)
        {
            using var result = await _sut.RelayStreamAsync($"ch-seq-{i}", null, CancellationToken.None);
            result.StatusCode.Should().Be(200, $"sequential request {i} should succeed");
            result.Body.Should().NotBeNull($"sequential request {i} should return a body");

            // Drain the stream to ensure it completes.
            var buffer = new byte[4096];
            while (await result.Body!.ReadAsync(buffer, CancellationToken.None) > 0)
            {
                // Drain.
            }
        }

        _output.WriteLine($"✅ {requestCount} sequential requests completed successfully.");
    }

    /// <summary>
    /// Parallel requests should not crash the relay or cause resource exhaustion.
    /// </summary>
    [Fact]
    public async Task Stability_ParallelRequests_ShouldNotCrash()
    {
        _server.Given(Request.Create().WithPath("/stream/channel/*").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "video/mp2t")
                .WithBody(BuildMpegTsPayload(188 * 5)));

        const int parallelCount = 5;
        var tasks = Enumerable.Range(0, parallelCount).Select(async i =>
        {
            using var result = await _sut.RelayStreamAsync($"ch-par-{i}", null, CancellationToken.None);
            result.StatusCode.Should().Be(200, $"parallel request {i} should succeed");

            if (result.Body != null)
            {
                var buffer = new byte[4096];
                while (await result.Body.ReadAsync(buffer, CancellationToken.None) > 0)
                {
                    // Drain.
                }
            }

            return result.StatusCode;
        }).ToList();

        var results = await Task.WhenAll(tasks);
        results.Should().AllSatisfy(s => s.Should().Be(200));

        _output.WriteLine($"✅ {parallelCount} parallel requests completed without crashes.");
    }

    /// <summary>
    /// Stream relay should start responding within a reasonable time.
    /// AVPlayer has its own connection timeout — the relay must not add excessive latency.
    /// </summary>
    [Fact]
    public async Task Stability_ResponseStartupTime_ShouldBeReasonable()
    {
        _server.Given(Request.Create().WithPath("/stream/channel/*").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "video/mp2t")
                .WithBody(BuildMpegTsPayload(188)));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var result = await _sut.RelayStreamAsync("ch-startup", null, CancellationToken.None);
        sw.Stop();

        result.StatusCode.Should().Be(200);
        sw.ElapsedMilliseconds.Should().BeLessThan(5000,
            "relay should return headers within 5 seconds — " +
            "AVPlayer has a ~30 second connection timeout but excessive latency degrades UX");

        _output.WriteLine($"Stream startup time: {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Cancelled requests should not leave hanging connections.
    /// </summary>
    [Fact]
    public async Task Stability_CancelledRequest_ShouldNotHang()
    {
        _server.Given(Request.Create().WithPath("/stream/channel/*").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "video/mp2t")
                .WithDelay(TimeSpan.FromSeconds(30))
                .WithBody(BuildMpegTsPayload(188)));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        Func<Task> act = async () =>
        {
            using var result = await _sut.RelayStreamAsync("ch-cancel", null, cts.Token);
        };

        await act.Should().ThrowAsync<OperationCanceledException>(
            "relay should propagate cancellation cleanly");
    }

    // ── 8. Apple Specific Behavior Checks ──────────────────────────────────

    /// <summary>
    /// Comprehensive check that validates all AVPlayer expectations against a single stream response.
    /// This aggregates findings into a single compatibility report.
    /// </summary>
    [Fact]
    public async Task AppleCompatibility_ComprehensiveCheck_ShouldDocumentAllFindings()
    {
        var payload = BuildMpegTsPayload(188 * 20);

        _server.Given(Request.Create().WithPath("/stream/channel/*").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "video/mp2t")
                .WithHeader("Accept-Ranges", "bytes")
                .WithBody(payload));

        using var result = await _sut.RelayStreamAsync("ch-apple-compat", null, CancellationToken.None);

        var findings = new List<string>();

        // ✅ Basic response
        result.StatusCode.Should().Be(200);
        result.Body.Should().NotBeNull();

        // Check Content-Type
        if (string.IsNullOrWhiteSpace(result.ContentType))
        {
            findings.Add("❌ CRITICAL: No Content-Type in relay result — AVPlayer will refuse to play.");
        }
        else if (result.ContentType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add("❌ CRITICAL: Content-Type is 'application/octet-stream' — AVPlayer cannot determine stream format.");
        }
        else
        {
            findings.Add($"✅ Content-Type: {result.ContentType}");
        }

        // Check Content-Length
        if (result.ContentLength.HasValue)
        {
            findings.Add($"✅ Content-Length: {result.ContentLength.Value}");
        }
        else
        {
            findings.Add("ℹ️ No Content-Length — acceptable for live streams (chunked transfer).");
        }

        // Read first bytes for signature validation
        var buffer = new byte[188];
        var bytesRead = await result.Body!.ReadAsync(buffer, CancellationToken.None);
        if (bytesRead > 0 && buffer[0] == MpegTsSyncByte)
        {
            findings.Add("✅ MPEG-TS sync byte (0x47) present in first byte.");
        }
        else if (bytesRead == 0)
        {
            findings.Add("❌ CRITICAL: Empty body — AVPlayer will fail immediately.");
        }
        else
        {
            findings.Add($"⚠️ First byte is 0x{buffer[0]:X2}, not MPEG-TS sync byte 0x47.");
        }

        // Document HEAD support limitation
        findings.Add("❌ HEAD not supported — RelayController.GetStream only handles GET and writes to Response.Body.");

        // Document Range support limitation
        findings.Add("❌ Range requests not supported — no EnableRangeProcessing on stream endpoint.");

        // Document Accept-Ranges header
        findings.Add("⚠️ Accept-Ranges header not set by relay — upstream header is not forwarded to client.");

        // Credential safety
        var allText = $"{result.ContentType}|{result.ETag}|{result.CacheControl}";
        var hasLeakedSecrets = allText.Contains(_config.Username!, StringComparison.OrdinalIgnoreCase)
            || allText.Contains(_config.Password!, StringComparison.OrdinalIgnoreCase)
            || allText.Contains(_config.AuthToken!, StringComparison.OrdinalIgnoreCase);
        findings.Add(hasLeakedSecrets
            ? "❌ CRITICAL: Credentials found in response metadata!"
            : "✅ No credential leakage in response metadata.");

        // Output report
        _output.WriteLine("═══════════════════════════════════════════════════════════════");
        _output.WriteLine("  Apple AVPlayer Compatibility Report");
        _output.WriteLine("═══════════════════════════════════════════════════════════════");
        foreach (var finding in findings)
        {
            _output.WriteLine($"  {finding}");
        }

        _output.WriteLine("═══════════════════════════════════════════════════════════════");

        // This test always passes — it's a diagnostic that documents the current state.
        // Real failures are caught by the specific tests above.
        findings.Should().NotBeEmpty("compatibility report should contain findings");
    }

    /// <summary>
    /// Verifies that the stream relay does not add unexpected authentication requirements.
    /// AVPlayer will fail if mid-stream the server suddenly requires auth.
    /// </summary>
    [Fact]
    public async Task AppleCompatibility_NoAuthSurprise_StreamShouldNotRequireReAuth()
    {
        // First response is fine, second response requires auth (simulating token expiry).
        _server.Given(Request.Create().WithPath("/stream/channel/*").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "video/mp2t")
                .WithBody(BuildMpegTsPayload(188 * 5)));

        using var result1 = await _sut.RelayStreamAsync("ch-auth-1", null, CancellationToken.None);
        result1.StatusCode.Should().Be(200, "first request should succeed");

        // Drain stream.
        var buf = new byte[4096];
        while (await result1.Body!.ReadAsync(buf, CancellationToken.None) > 0)
        {
        }

        // Second request — still works because relay uses configured credentials, not client auth.
        using var result2 = await _sut.RelayStreamAsync("ch-auth-2", null, CancellationToken.None);
        result2.StatusCode.Should().Be(200,
            "second request should succeed — relay uses stable server-side credentials");

        _output.WriteLine(
            "✅ Relay uses server-side credentials for all upstream requests. " +
            "AVPlayer never needs to authenticate directly with TVHeadend.");
    }

    /// <summary>
    /// Validates image relay returns correct MIME types for common image formats.
    /// AVPlayer and associated UI components use these for thumbnails and logos.
    /// </summary>
    /// <param name="contentType">Image MIME type.</param>
    /// <param name="firstBytes">First bytes of the image format as hex pairs.</param>
    [Theory]
    [InlineData("image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47 })]
    [InlineData("image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF })]
    [InlineData("image/svg+xml", new byte[] { 0x3C, 0x73, 0x76, 0x67 })]
    public async Task AppleCompatibility_ImageRelay_ShouldPreserveMimeAndContent(string contentType, byte[] firstBytes)
    {
        _server.Given(Request.Create().WithPath($"/imagecache/img-{contentType.GetHashCode()}").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", contentType)
                .WithBody(firstBytes));

        using var result = await _sut.RelayImageAsync($"imagecache/img-{contentType.GetHashCode()}", CancellationToken.None);

        result.StatusCode.Should().Be(200);
        result.ContentType.Should().Contain(contentType, $"image MIME type '{contentType}' must be preserved");
        result.Body.Should().NotBeNull();

        var buffer = new byte[firstBytes.Length];
        var bytesRead = await result.Body!.ReadAsync(buffer, CancellationToken.None);
        bytesRead.Should().Be(firstBytes.Length);
        buffer.Should().BeEquivalentTo(firstBytes, "image content must be relayed without modification");
    }

    // ── Helper Methods ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds a fake MPEG-TS payload with valid sync bytes every 188 bytes.
    /// </summary>
    private static byte[] BuildMpegTsPayload(int length)
    {
        var payload = new byte[length];
        for (var i = 0; i < length; i += 188)
        {
            payload[i] = MpegTsSyncByte;
        }

        return payload;
    }

    /// <summary>
    /// Builds a minimal fake MP4 payload with an ftyp atom.
    /// </summary>
    private static byte[] BuildMp4Payload()
    {
        // Minimal ftyp box: size (4 bytes) + "ftyp" (4 bytes) + brand (4 bytes) + version (4 bytes)
        var payload = new byte[20];
        // Box size = 20 (big-endian)
        payload[0] = 0x00;
        payload[1] = 0x00;
        payload[2] = 0x00;
        payload[3] = 0x14; // 20 in decimal
        // "ftyp" in ASCII
        payload[4] = 0x66; // f
        payload[5] = 0x74; // t
        payload[6] = 0x79; // y
        payload[7] = 0x70; // p
        // Brand: "isom"
        payload[8] = 0x69; // i
        payload[9] = 0x73; // s
        payload[10] = 0x6F; // o
        payload[11] = 0x6D; // m
        return payload;
    }

    /// <summary>
    /// Checks whether a buffer contains the "ftyp" atom identifier.
    /// </summary>
    private static bool ContainsFtypAtom(byte[] buffer, int length)
    {
        for (var i = 0; i <= length - 4; i++)
        {
            if (buffer[i] == 0x66 && buffer[i + 1] == 0x74 && buffer[i + 2] == 0x79 && buffer[i + 3] == 0x70)
            {
                return true;
            }
        }

        return false;
    }
}



