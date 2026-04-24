// Cryptographic token generation and HMAC-SHA256 hashing for relay tokens.

using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// Generates cryptographically random relay tokens and computes HMAC-SHA256 hashes.
/// The server-side secret (pepper) ensures that stolen hashes cannot be reversed
/// to raw tokens without knowledge of the secret.
/// <para>
/// Raw tokens are never stored — only HMAC hashes are persisted in SQLite.
/// </para>
/// </summary>
internal sealed class RelayTokenHasher : IDisposable
{
    /// <summary>Length of the generated raw token in bytes (before base64url encoding).</summary>
    internal const int RawTokenByteLength = 32;

    /// <summary>Maximum accepted raw token length in characters (prevents abuse with very long inputs).</summary>
    internal const int MaxRawTokenLength = 512;

    private readonly HMAC _hmac;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayTokenHasher"/> class.
    /// </summary>
    /// <param name="serverSecret">
    /// A stable server-side secret used as the HMAC key. Should be derived from the plugin's data folder
    /// or a persisted secret. Must not change across restarts or token lookups will fail.
    /// </param>
    public RelayTokenHasher(byte[] serverSecret)
    {
        ArgumentNullException.ThrowIfNull(serverSecret);
        if (serverSecret.Length < 16)
        {
            throw new ArgumentException("Server secret must be at least 16 bytes.", nameof(serverSecret));
        }

        _hmac = new HMACSHA256(serverSecret);
    }

    /// <summary>
    /// Generates a new cryptographically random raw token encoded as a base64url string.
    /// </summary>
    /// <returns>A URL-safe base64-encoded token string (no padding).</returns>
    public static string GenerateRawToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(RawTokenByteLength);
        return Base64UrlEncode(bytes);
    }

    /// <summary>
    /// Computes the HMAC-SHA256 hash of the given raw token and returns it as a lowercase hex string.
    /// </summary>
    /// <param name="rawToken">The raw token to hash.</param>
    /// <returns>Lowercase hex-encoded HMAC digest.</returns>
    public string HashToken(string rawToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);
        if (rawToken.Length > MaxRawTokenLength)
        {
            throw new ArgumentException("Token exceeds maximum allowed length.", nameof(rawToken));
        }

        var inputBytes = Encoding.UTF8.GetBytes(rawToken);
        var hashBytes = _hmac.ComputeHash(inputBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Validates that the provided raw token input is well-formed (non-empty, within length bounds).
    /// </summary>
    /// <param name="rawToken">Raw token to check.</param>
    /// <returns><c>true</c> if the token is well-formed; otherwise <c>false</c>.</returns>
    public static bool IsWellFormed(string? rawToken)
    {
        return !string.IsNullOrWhiteSpace(rawToken) && rawToken.Length <= MaxRawTokenLength;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _hmac.Dispose();
    }

    /// <summary>
    /// Encodes bytes as a URL-safe base64 string without padding.
    /// </summary>
    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
