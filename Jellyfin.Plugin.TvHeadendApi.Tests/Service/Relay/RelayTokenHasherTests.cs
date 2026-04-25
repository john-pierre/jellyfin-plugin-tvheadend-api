// Tests for RelayTokenHasher — token generation and HMAC hashing.

using System;
using Jellyfin.Plugin.TvHeadendApi.Service.Relay;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Relay;

/// <summary>
/// Tests for <see cref="RelayTokenHasher"/>.
/// </summary>
public class RelayTokenHasherTests
{
    private static byte[] CreateTestSecret() => new byte[32];

    [Fact]
    public void GenerateRawToken_ReturnsNonEmptyString()
    {
        var token = RelayTokenHasher.GenerateRawToken();

        Assert.False(string.IsNullOrWhiteSpace(token));
    }

    [Fact]
    public void GenerateRawToken_ProducesUniqueTokens()
    {
        var token1 = RelayTokenHasher.GenerateRawToken();
        var token2 = RelayTokenHasher.GenerateRawToken();

        Assert.NotEqual(token1, token2);
    }

    [Fact]
    public void GenerateRawToken_IsBase64UrlSafe()
    {
        var token = RelayTokenHasher.GenerateRawToken();

        Assert.DoesNotContain("+", token);
        Assert.DoesNotContain("/", token);
        Assert.DoesNotContain("=", token);
    }

    [Fact]
    public void HashToken_IsDeterministic()
    {
        using var hasher = new RelayTokenHasher(CreateTestSecret());
        var rawToken = RelayTokenHasher.GenerateRawToken();

        var hash1 = hasher.HashToken(rawToken);
        var hash2 = hasher.HashToken(rawToken);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void HashToken_DifferentTokensProduceDifferentHashes()
    {
        using var hasher = new RelayTokenHasher(CreateTestSecret());
        var token1 = RelayTokenHasher.GenerateRawToken();
        var token2 = RelayTokenHasher.GenerateRawToken();

        var hash1 = hasher.HashToken(token1);
        var hash2 = hasher.HashToken(token2);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void HashToken_ReturnsLowercaseHexString()
    {
        using var hasher = new RelayTokenHasher(CreateTestSecret());
        var rawToken = RelayTokenHasher.GenerateRawToken();

        var hash = hasher.HashToken(rawToken);

        Assert.Matches("^[0-9a-f]+$", hash);
        Assert.Equal(64, hash.Length); // SHA256 = 32 bytes = 64 hex chars
    }

    [Fact]
    public void HashToken_DifferentSecretsProduceDifferentHashes()
    {
        var secret1 = new byte[32];
        secret1[0] = 1;
        var secret2 = new byte[32];
        secret2[0] = 2;

        using var hasher1 = new RelayTokenHasher(secret1);
        using var hasher2 = new RelayTokenHasher(secret2);

        var rawToken = RelayTokenHasher.GenerateRawToken();
        var hash1 = hasher1.HashToken(rawToken);
        var hash2 = hasher2.HashToken(rawToken);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void HashToken_ThrowsOnNullOrWhitespace()
    {
        using var hasher = new RelayTokenHasher(CreateTestSecret());

        Assert.Throws<ArgumentNullException>(() => hasher.HashToken(null!));
        Assert.Throws<ArgumentException>(() => hasher.HashToken(string.Empty));
        Assert.Throws<ArgumentException>(() => hasher.HashToken("   "));
    }

    [Fact]
    public void HashToken_ThrowsOnTooLongInput()
    {
        using var hasher = new RelayTokenHasher(CreateTestSecret());
        var tooLong = new string('a', RelayTokenHasher.MaxRawTokenLength + 1);

        Assert.Throws<ArgumentException>(() => hasher.HashToken(tooLong));
    }

    [Fact]
    public void Constructor_ThrowsOnShortSecret()
    {
        Assert.Throws<ArgumentException>(() => new RelayTokenHasher(new byte[8]));
    }

    [Fact]
    public void Constructor_ThrowsOnNullSecret()
    {
        Assert.Throws<ArgumentNullException>(() => new RelayTokenHasher(null!));
    }

    [Fact]
    public void IsWellFormed_ReturnsTrueForValidToken()
    {
        var token = RelayTokenHasher.GenerateRawToken();
        Assert.True(RelayTokenHasher.IsWellFormed(token));
    }

    [Fact]
    public void IsWellFormed_ReturnsFalseForNull()
    {
        Assert.False(RelayTokenHasher.IsWellFormed(null));
    }

    [Fact]
    public void IsWellFormed_ReturnsFalseForEmpty()
    {
        Assert.False(RelayTokenHasher.IsWellFormed(string.Empty));
    }

    [Fact]
    public void IsWellFormed_ReturnsFalseForTooLong()
    {
        var tooLong = new string('x', RelayTokenHasher.MaxRawTokenLength + 1);
        Assert.False(RelayTokenHasher.IsWellFormed(tooLong));
    }
}
