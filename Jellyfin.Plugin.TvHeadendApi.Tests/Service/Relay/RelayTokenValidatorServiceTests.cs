// Tests for RelayTokenValidatorService — covers all validation branches.

using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Relay;

public class RelayTokenValidatorServiceTests : IDisposable
{
    private readonly RelayTokenHasher _hasher = new(new byte[32]);
    private readonly Mock<IRelayTokenRepository> _repo = new();

    public void Dispose()
    {
        _hasher.Dispose();
    }

    private RelayTokenOptions CreateOptions(PluginConfiguration? config = null)
    {
        var cfg = config ?? new PluginConfiguration();
        return new RelayTokenOptions(new ConfigurationProvider(() => cfg));
    }

    private RelayTokenValidatorService CreateSut(PluginConfiguration? config = null)
    {
        return new RelayTokenValidatorService(
            NullLogger<RelayTokenValidatorService>.Instance,
            _hasher,
            _repo.Object,
            CreateOptions(config));
    }

    [Fact]
    public async Task ValidateAsync_SecurityDisabled_ReturnsValid()
    {
        var config = new PluginConfiguration { EnableRelayTokenSecurity = false };
        var sut = CreateSut(config);

        var result = await sut.ValidateAsync(null, RelayType.Stream, "ch-1", CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(RelayTokenFailureReason.SecurityDisabled, result.FailureReason);
    }

    [Fact]
    public async Task ValidateAsync_MissingToken_ReturnsFailure()
    {
        var sut = CreateSut();
        var result = await sut.ValidateAsync(null, RelayType.Stream, "ch-1", CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(RelayTokenFailureReason.MissingToken, result.FailureReason);
    }

    [Fact]
    public async Task ValidateAsync_EmptyToken_ReturnsFailure()
    {
        var sut = CreateSut();
        var result = await sut.ValidateAsync("", RelayType.Stream, "ch-1", CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(RelayTokenFailureReason.MissingToken, result.FailureReason);
    }

    [Fact]
    public async Task ValidateAsync_MalformedToken_ReturnsFailure()
    {
        var sut = CreateSut();
        var tooLong = new string('x', RelayTokenHasher.MaxRawTokenLength + 1);
        var result = await sut.ValidateAsync(tooLong, RelayType.Stream, "ch-1", CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(RelayTokenFailureReason.MalformedToken, result.FailureReason);
    }

    [Fact]
    public async Task ValidateAsync_TokenNotFound_ReturnsFailure()
    {
        _repo.Setup(r => r.FindByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RelayTokenRecord?)null);

        var sut = CreateSut();
        var result = await sut.ValidateAsync(RelayTokenHasher.GenerateRawToken(), RelayType.Stream, "ch-1", CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(RelayTokenFailureReason.TokenNotFound, result.FailureReason);
    }

    [Fact]
    public async Task ValidateAsync_ExpiredToken_ReturnsFailure()
    {
        var rawToken = RelayTokenHasher.GenerateRawToken();
        var hash = _hasher.HashToken(rawToken);
        var record = new RelayTokenRecord
        {
            Id = 1,
            TokenHash = hash,
            RelayType = "stream",
            ChannelId = "ch-1",
            CreatedAtUtc = DateTime.UtcNow.AddHours(-2),
            ExpiresAtUtc = DateTime.UtcNow.AddHours(-1),
        };
        _repo.Setup(r => r.FindByHashAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var sut = CreateSut();
        var result = await sut.ValidateAsync(rawToken, RelayType.Stream, "ch-1", CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(RelayTokenFailureReason.Expired, result.FailureReason);
    }

    [Fact]
    public async Task ValidateAsync_RevokedToken_ReturnsFailure()
    {
        var rawToken = RelayTokenHasher.GenerateRawToken();
        var hash = _hasher.HashToken(rawToken);
        var record = new RelayTokenRecord
        {
            Id = 1,
            TokenHash = hash,
            RelayType = "stream",
            ChannelId = "ch-1",
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            Revoked = true,
        };
        _repo.Setup(r => r.FindByHashAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var sut = CreateSut();
        var result = await sut.ValidateAsync(rawToken, RelayType.Stream, "ch-1", CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(RelayTokenFailureReason.Revoked, result.FailureReason);
    }

    [Fact]
    public async Task ValidateAsync_MaxUsesExceeded_ReturnsFailure()
    {
        var rawToken = RelayTokenHasher.GenerateRawToken();
        var hash = _hasher.HashToken(rawToken);
        var record = new RelayTokenRecord
        {
            Id = 1,
            TokenHash = hash,
            RelayType = "stream",
            ChannelId = "ch-1",
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            MaxUses = 3,
            UseCount = 3,
        };
        _repo.Setup(r => r.FindByHashAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var sut = CreateSut();
        var result = await sut.ValidateAsync(rawToken, RelayType.Stream, "ch-1", CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(RelayTokenFailureReason.MaxUsesExceeded, result.FailureReason);
    }

    [Fact]
    public async Task ValidateAsync_RelayTypeMismatch_ReturnsFailure()
    {
        var rawToken = RelayTokenHasher.GenerateRawToken();
        var hash = _hasher.HashToken(rawToken);
        var record = new RelayTokenRecord
        {
            Id = 1,
            TokenHash = hash,
            RelayType = "image",
            ImageId = "img/1",
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        };
        _repo.Setup(r => r.FindByHashAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var sut = CreateSut();
        var result = await sut.ValidateAsync(rawToken, RelayType.Stream, "ch-1", CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(RelayTokenFailureReason.RelayTypeMismatch, result.FailureReason);
    }

    [Fact]
    public async Task ValidateAsync_ScopeMismatch_ReturnsFailure()
    {
        var rawToken = RelayTokenHasher.GenerateRawToken();
        var hash = _hasher.HashToken(rawToken);
        var record = new RelayTokenRecord
        {
            Id = 1,
            TokenHash = hash,
            RelayType = "stream",
            ChannelId = "ch-1",
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        };
        _repo.Setup(r => r.FindByHashAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var config = new PluginConfiguration { StrictScopeValidation = true };
        var sut = CreateSut(config);
        var result = await sut.ValidateAsync(rawToken, RelayType.Stream, "ch-DIFFERENT", CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(RelayTokenFailureReason.ScopeMismatch, result.FailureReason);
    }

    [Fact]
    public async Task ValidateAsync_ValidToken_ReturnsSuccess()
    {
        var rawToken = RelayTokenHasher.GenerateRawToken();
        var hash = _hasher.HashToken(rawToken);
        var record = new RelayTokenRecord
        {
            Id = 1,
            TokenHash = hash,
            RelayType = "stream",
            ChannelId = "ch-1",
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            UseCount = 0,
            MaxUses = 5,
        };
        _repo.Setup(r => r.FindByHashAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _repo.Setup(r => r.TryConsumeUseAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var sut = CreateSut();
        var result = await sut.ValidateAsync(rawToken, RelayType.Stream, "ch-1", CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(RelayTokenFailureReason.None, result.FailureReason);
        Assert.NotNull(result.TokenRecord);
        _repo.Verify(r => r.TryConsumeUseAsync(1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ValidateAsync_ConcurrentMaxUsesRace_ConsumeFails_ReturnsMaxUsesExceeded()
    {
        // The snapshot check passes (UseCount < MaxUses) but another request consumes the
        // last use before this one — the atomic consume must reject the request.
        var rawToken = RelayTokenHasher.GenerateRawToken();
        var hash = _hasher.HashToken(rawToken);
        var record = new RelayTokenRecord
        {
            Id = 1,
            TokenHash = hash,
            RelayType = "stream",
            ChannelId = "ch-1",
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            UseCount = 4,
            MaxUses = 5,
        };
        _repo.Setup(r => r.FindByHashAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _repo.Setup(r => r.TryConsumeUseAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var sut = CreateSut();
        var result = await sut.ValidateAsync(rawToken, RelayType.Stream, "ch-1", CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(RelayTokenFailureReason.MaxUsesExceeded, result.FailureReason);
    }

    [Fact]
    public async Task ValidateAsync_UnlimitedMaxUses_PassesValidation()
    {
        var rawToken = RelayTokenHasher.GenerateRawToken();
        var hash = _hasher.HashToken(rawToken);
        var record = new RelayTokenRecord
        {
            Id = 1,
            TokenHash = hash,
            RelayType = "stream",
            ChannelId = "ch-1",
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            UseCount = 1000,
            MaxUses = null,
        };
        _repo.Setup(r => r.FindByHashAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var sut = CreateSut();
        var result = await sut.ValidateAsync(rawToken, RelayType.Stream, "ch-1", CancellationToken.None);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ValidateAsync_RepositoryThrows_ReturnsUnexpectedError()
    {
        _repo.Setup(r => r.FindByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB error"));

        var sut = CreateSut();
        var result = await sut.ValidateAsync(RelayTokenHasher.GenerateRawToken(), RelayType.Stream, "ch-1", CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(RelayTokenFailureReason.UnexpectedError, result.FailureReason);
    }

    [Fact]
    public async Task ValidateAsync_NeverExpiringImageToken_MaxValueExpiryWithClockSkew_ReturnsSuccess()
    {
        // Regression: the reusable image token is persisted with ExpiresAtUtc = DateTime.MaxValue
        // (default ImageTokenTtlMinutes = 0). Adding the default 5s clock skew to DateTime.MaxValue
        // overflowed and every relay image request failed with UnexpectedError (HTTP 500).
        var rawToken = RelayTokenHasher.GenerateRawToken();
        var hash = _hasher.HashToken(rawToken);
        var record = new RelayTokenRecord
        {
            Id = 1,
            TokenHash = hash,
            RelayType = "image",
            ImageId = null,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.MaxValue,
            MaxUses = null,
        };
        _repo.Setup(r => r.FindByHashAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var sut = CreateSut();
        var result = await sut.ValidateAsync(rawToken, RelayType.Image, "imagecache/42", CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(RelayTokenFailureReason.None, result.FailureReason);
    }

    [Fact]
    public async Task ValidateAsync_ImageScope_ValidatesImageId()
    {
        var rawToken = RelayTokenHasher.GenerateRawToken();
        var hash = _hasher.HashToken(rawToken);
        var record = new RelayTokenRecord
        {
            Id = 1,
            TokenHash = hash,
            RelayType = "image",
            ImageId = "imagecache/42",
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        };
        _repo.Setup(r => r.FindByHashAsync(hash, It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var sut = CreateSut();
        var result = await sut.ValidateAsync(rawToken, RelayType.Image, "imagecache/42", CancellationToken.None);

        Assert.True(result.IsValid);
    }
}
