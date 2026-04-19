using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Helper;

/// <summary>
/// Tests for <see cref="DigestAuthHandler"/> covering Digest auth, Basic auth fallback,
/// no-challenge passthrough, request cloning, and SHA-256 algorithm selection.
/// </summary>
public class DigestAuthHandlerTests
{
    [Fact]
    public void Constructor_NullUsername_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new DigestAuthHandler(null!, "pass"));
    }

    [Fact]
    public async Task SendAsync_NonUnauthorized_ReturnsResponseDirectly()
    {
        var inner = new FakeInnerHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new DigestAuthHandler("user", "pass") { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        var result = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/test"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task SendAsync_Unauthorized_NoWwwAuthenticate_ReturnsOriginal401()
    {
        var inner = new FakeInnerHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var handler = new DigestAuthHandler("user", "pass") { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        var result = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/test"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task SendAsync_BasicChallenge_RetriesWithBasicAuth()
    {
        var callCount = 0;
        var inner = new FakeInnerHandler(request =>
        {
            callCount++;
            if (callCount == 1)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                resp.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Basic", "realm=\"test\""));
                return resp;
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var handler = new DigestAuthHandler("user", "pass") { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        var result = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/test"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task SendAsync_DigestChallenge_RetriesWithDigestAuth()
    {
        var callCount = 0;
        var inner = new FakeInnerHandler(request =>
        {
            callCount++;
            if (callCount == 1)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                resp.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                    "Digest",
                    "realm=\"TVHeadend\", nonce=\"abc123\", qop=\"auth\", opaque=\"opq456\""));
                return resp;
            }

            // Verify Authorization header was set
            Assert.NotNull(request.Headers.Authorization);
            Assert.Equal("Digest", request.Headers.Authorization.Scheme);
            var param = request.Headers.Authorization.Parameter!;
            Assert.Contains("username=\"user\"", param);
            Assert.Contains("realm=\"TVHeadend\"", param);
            Assert.Contains("nonce=\"abc123\"", param);
            Assert.Contains("qop=auth", param);
            Assert.Contains("opaque=\"opq456\"", param);
            Assert.Contains("response=\"", param);
            Assert.Contains("nc=00000001", param);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var handler = new DigestAuthHandler("user", "pass") { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        var result = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/test"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task SendAsync_DigestChallengeWithSha256_UsesCorrectAlgorithm()
    {
        var callCount = 0;
        var inner = new FakeInnerHandler(request =>
        {
            callCount++;
            if (callCount == 1)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                resp.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                    "Digest",
                    "realm=\"TVHeadend\", nonce=\"sha256nonce\", algorithm=SHA-256"));
                return resp;
            }
            Assert.Contains("algorithm=SHA-256", request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var handler = new DigestAuthHandler("user", "pass") { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        var result = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/test"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    [Fact]
    public async Task SendAsync_DigestChallengeWithoutQop_OmitsQopFields()
    {
        var callCount = 0;
        var inner = new FakeInnerHandler(request =>
        {
            callCount++;
            if (callCount == 1)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                resp.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                    "Digest",
                    "realm=\"TVHeadend\", nonce=\"noqopnonce\""));
                return resp;
            }
            var param = request.Headers.Authorization?.Parameter ?? string.Empty;
            Assert.DoesNotContain("qop=", param);
            Assert.DoesNotContain("nc=", param);
            Assert.DoesNotContain("cnonce=", param);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var handler = new DigestAuthHandler("user", "pass") { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        var result = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/test"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    [Fact]
    public async Task SendAsync_DigestChallengeMissingNonce_FallsBackToBaseSend()
    {
        var callCount = 0;
        var inner = new FakeInnerHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                resp.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                    "Digest",
                    "realm=\"TVHeadend\""));
                return resp;
            }
            // Second call is a plain retry without digest auth
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var handler = new DigestAuthHandler("user", "pass") { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        var result = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/test"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task SendAsync_DigestChallengeWithoutOpaque_OmitsOpaqueField()
    {
        var callCount = 0;
        var inner = new FakeInnerHandler(request =>
        {
            callCount++;
            if (callCount == 1)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                resp.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                    "Digest",
                    "realm=\"TVHeadend\", nonce=\"nonce123\", qop=\"auth\""));
                return resp;
            }
            var param = request.Headers.Authorization?.Parameter ?? string.Empty;
            Assert.DoesNotContain("opaque=", param);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var handler = new DigestAuthHandler("user", "pass") { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        var result = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/test"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    [Fact]
    public async Task SendAsync_WithRequestContent_ClonesContentCorrectly()
    {
        var callCount = 0;
        var inner = new FakeInnerHandler(request =>
        {
            callCount++;
            if (callCount == 1)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                resp.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Basic", "realm=\"test\""));
                return resp;
            }
            Assert.NotNull(request.Content);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var handler = new DigestAuthHandler("user", "pass") { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        var req = new HttpRequestMessage(HttpMethod.Post, "http://localhost/api/test");
        req.Content = new StringContent("body data", Encoding.UTF8, "text/plain");

        var result = await invoker.SendAsync(req, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    [Fact]
    public async Task SendAsync_NullPassword_DefaultsToEmpty()
    {
        var inner = new FakeInnerHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new DigestAuthHandler("user", null!) { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        var result = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/test"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    [Fact]
    public async Task SendAsync_NonceCountIncrements_AcrossMultipleDigestChallenges()
    {
        var callCount = 0;
        var ncValues = new System.Collections.Generic.List<string>();
        var inner = new FakeInnerHandler(request =>
        {
            callCount++;
            if (callCount % 2 == 1) // odd calls are initial requests
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                resp.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                    "Digest",
                    "realm=\"TVH\", nonce=\"n1\", qop=\"auth\""));
                return resp;
            }
            // Extract nc value
            var param = request.Headers.Authorization?.Parameter ?? string.Empty;
            var ncStart = param.IndexOf("nc=", StringComparison.Ordinal) + 3;
            var ncEnd = param.IndexOf(',', ncStart);
            if (ncEnd < 0) ncEnd = param.Length;
            ncValues.Add(param.Substring(ncStart, ncEnd - ncStart));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var handler = new DigestAuthHandler("user", "pass") { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/test"), CancellationToken.None);
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/test"), CancellationToken.None);

        Assert.Equal(2, ncValues.Count);
        Assert.Equal("00000001", ncValues[0]);
        Assert.Equal("00000002", ncValues[1]);
    }

    private sealed class FakeInnerHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;
        public int CallCount { get; private set; }

        public FakeInnerHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) => _factory = factory;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_factory(request));
        }
    }
}

