using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Auth;

/// <summary>
/// A <see cref="DelegatingHandler"/> that implements HTTP Digest and Basic Authentication.
/// <para>
/// .NET's <see cref="SocketsHttpHandler"/> (default since .NET Core) does not support Digest auth.
/// This handler intercepts 401 responses, inspects the <c>WWW-Authenticate</c> header, and:
/// <list type="bullet">
///   <item>For <c>Digest</c> challenges: computes the RFC 7616 / RFC 2617 response hash and retries.</item>
///   <item>For <c>Basic</c> challenges: sends a proactive <c>Authorization: Basic</c> header and retries.</item>
/// </list>
/// Supports <c>qop=auth</c> with MD5 (TVHeadend default) and SHA-256 algorithms for Digest.
/// The negotiated challenge is cached (with a per-nonce <c>nc</c> counter) so subsequent requests
/// authenticate preemptively instead of paying a 401 round-trip each time; a stale nonce simply
/// triggers a fresh negotiation.
/// </para>
/// </summary>
internal sealed class DigestAuthHandler : DelegatingHandler
{
    private readonly string _username;
    private readonly string _password;
    private volatile DigestSession? _session;
    private volatile bool _useBasicFallback;

    /// <summary>
    /// Initializes a new instance of the <see cref="DigestAuthHandler"/> class.
    /// </summary>
    /// <param name="username">The username for authentication.</param>
    /// <param name="password">The password for authentication.</param>
    public DigestAuthHandler(string username, string password)
    {
        _username = username ?? throw new ArgumentNullException(nameof(username));
        _password = password ?? string.Empty;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Preemptive authentication from the cached session avoids a 401 round-trip per request.
        var session = _session;
        if (session != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Digest", BuildDigestHeader(session, request));
        }
        else if (_useBasicFallback)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", BuildBasicToken());
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        var digestChallenge = response.Headers.WwwAuthenticate
            .FirstOrDefault(h => string.Equals(h.Scheme, "Digest", StringComparison.OrdinalIgnoreCase));

        if (digestChallenge == null)
        {
            // No Digest challenge — check for Basic challenge as fallback.
            var basicChallenge = response.Headers.WwwAuthenticate
                .FirstOrDefault(h => string.Equals(h.Scheme, "Basic", StringComparison.OrdinalIgnoreCase));

            if (basicChallenge == null)
            {
                return response;
            }

            response.Dispose();
            _session = null;
            _useBasicFallback = true;
            return await RetryWithBasicAuthAsync(request, cancellationToken).ConfigureAwait(false);
        }

        response.Dispose();

        var parameters = ParseDigestChallenge(digestChallenge.Parameter ?? string.Empty);
        if (!parameters.TryGetValue("nonce", out var nonce) ||
            !parameters.TryGetValue("realm", out var realm))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        parameters.TryGetValue("opaque", out var opaque);
        parameters.TryGetValue("qop", out var qop);
        parameters.TryGetValue("algorithm", out var algorithm);

        var newSession = new DigestSession(realm, nonce, opaque, qop, algorithm);
        _session = newSession;
        _useBasicFallback = false;

        using var retryRequest = await HttpRequestCloner.CloneAsync(request, cancellationToken).ConfigureAwait(false);
        retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Digest", BuildDigestHeader(newSession, retryRequest));

        return await base.SendAsync(retryRequest, cancellationToken).ConfigureAwait(false);
    }

    private string BuildDigestHeader(DigestSession session, HttpRequestMessage request)
    {
        var ncHex = session.NextNonceCount().ToString("x8", CultureInfo.InvariantCulture);
        var cnonce = GenerateCnonce();
        var method = request.Method.Method;
        var uri = request.RequestUri?.PathAndQuery ?? "/";

        var ha1 = ComputeHash(session.Algorithm, $"{_username}:{session.Realm}:{_password}");
        var ha2 = ComputeHash(session.Algorithm, $"{method}:{uri}");

        string digestResponse;
        if (!string.IsNullOrEmpty(session.Qop) && session.Qop.Contains("auth", StringComparison.OrdinalIgnoreCase))
        {
            digestResponse = ComputeHash(session.Algorithm, $"{ha1}:{session.Nonce}:{ncHex}:{cnonce}:{session.Qop}:{ha2}");
        }
        else
        {
            digestResponse = ComputeHash(session.Algorithm, $"{ha1}:{session.Nonce}:{ha2}");
        }

        return BuildAuthorizationHeader(session.Realm, session.Nonce, uri, ncHex, cnonce, session.Qop, session.Opaque, session.Algorithm, digestResponse);
    }

    private string BuildBasicToken()
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_username}:{_password}"));
    }

    private async Task<HttpResponseMessage> RetryWithBasicAuthAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var retryRequest = await HttpRequestCloner.CloneAsync(request, cancellationToken).ConfigureAwait(false);
        retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", BuildBasicToken());
        return await base.SendAsync(retryRequest, cancellationToken).ConfigureAwait(false);
    }

    private static string ComputeHash(string? algorithm, string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        byte[] hash;

        if (string.Equals(algorithm, "SHA-256", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(algorithm, "SHA-512-256", StringComparison.OrdinalIgnoreCase))
        {
            hash = SHA256.HashData(bytes);
        }
        else
        {
#pragma warning disable CA5351 // Digest auth requires MD5 per RFC 2617
            hash = MD5.HashData(bytes);
#pragma warning restore CA5351
        }

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GenerateCnonce()
    {
        Span<byte> buffer = stackalloc byte[16];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToHexString(buffer).ToLowerInvariant();
    }

    private string BuildAuthorizationHeader(
        string realm,
        string nonce,
        string uri,
        string nc,
        string cnonce,
        string? qop,
        string? opaque,
        string? algorithm,
        string response)
    {
        var sb = new StringBuilder(256);
        sb.Append(CultureInfo.InvariantCulture, $"username=\"{_username}\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{uri}\"");

        if (!string.IsNullOrEmpty(algorithm))
        {
            sb.Append(CultureInfo.InvariantCulture, $", algorithm={algorithm}");
        }

        if (!string.IsNullOrEmpty(qop))
        {
            sb.Append(CultureInfo.InvariantCulture, $", qop=auth, nc={nc}, cnonce=\"{cnonce}\"");
        }

        sb.Append(CultureInfo.InvariantCulture, $", response=\"{response}\"");

        if (!string.IsNullOrEmpty(opaque))
        {
            sb.Append(CultureInfo.InvariantCulture, $", opaque=\"{opaque}\"");
        }

        return sb.ToString();
    }

    private static System.Collections.Generic.Dictionary<string, string> ParseDigestChallenge(string challenge)
    {
        var result = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var span = challenge.AsSpan();

        while (span.Length > 0)
        {
            span = span.TrimStart();
            var eqIndex = span.IndexOf('=');
            if (eqIndex < 0)
            {
                break;
            }

            var key = span[..eqIndex].Trim().ToString();
            span = span[(eqIndex + 1)..].TrimStart();

            string value;
            if (span.Length > 0 && span[0] == '"')
            {
                span = span[1..];
                var closeQuote = span.IndexOf('"');
                if (closeQuote < 0)
                {
                    value = span.ToString();
                    span = ReadOnlySpan<char>.Empty;
                }
                else
                {
                    value = span[..closeQuote].ToString();
                    span = span[(closeQuote + 1)..];
                }
            }
            else
            {
                var commaIndex = span.IndexOf(',');
                if (commaIndex < 0)
                {
                    value = span.Trim().ToString();
                    span = ReadOnlySpan<char>.Empty;
                }
                else
                {
                    value = span[..commaIndex].Trim().ToString();
                    span = span[(commaIndex + 1)..];
                }
            }

            // Skip comma separator
            span = span.TrimStart();
            if (span.Length > 0 && span[0] == ',')
            {
                span = span[1..];
            }

            result[key] = value;
        }

        return result;
    }

    /// <summary>
    /// An immutable negotiated Digest challenge with its per-nonce <c>nc</c> counter.
    /// </summary>
    private sealed class DigestSession
    {
        private int _nonceCount;

        public DigestSession(string realm, string nonce, string? opaque, string? qop, string? algorithm)
        {
            Realm = realm;
            Nonce = nonce;
            Opaque = opaque;
            Qop = qop;
            Algorithm = algorithm;
        }

        public string Realm { get; }

        public string Nonce { get; }

        public string? Opaque { get; }

        public string? Qop { get; }

        public string? Algorithm { get; }

        public int NextNonceCount() => Interlocked.Increment(ref _nonceCount);
    }
}
