// Tests for RelayTokenService — mock-based token issuance, revocation, and cleanup.

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

public class RelayTokenServiceTests
{
    private static RelayTokenOptions CreateOptions(PluginConfiguration? config = null)
    {
        var cfg = config ?? new PluginConfiguration();
        return new RelayTokenOptions(new ConfigurationProvider(() => cfg));
    }

    private static RelayTokenHasher CreateHasher()
    {
        return new RelayTokenHasher(new byte[32]);
    }

    [Fact]
    public async Task IssueStreamTokenAsync_ReturnsNonEmptyToken()
    {
        var repo = new Mock<IRelayTokenRepository>();
        using var hasher = CreateHasher();
        var sut = new RelayTokenService(
            NullLogger<RelayTokenService>.Instance, hasher, repo.Object, CreateOptions());

        var token = await sut.IssueStreamTokenAsync("ch-1", null, null, null, null, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(token));
        repo.Verify(r => r.InsertAsync(It.Is<RelayTokenRecord>(t =>
            t.RelayType == "stream" && t.ChannelId == "ch-1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IssueStreamTokenAsync_ThrowsOnEmptyChannelId()
    {
        var repo = new Mock<IRelayTokenRepository>();
        using var hasher = CreateHasher();
        var sut = new RelayTokenService(
            NullLogger<RelayTokenService>.Instance, hasher, repo.Object, CreateOptions());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.IssueStreamTokenAsync("", null, null, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task IssueStreamTokenAsync_SetsMaxUsesFromOptions()
    {
        var config = new PluginConfiguration { StreamTokenMaxUses = 10 };
        var repo = new Mock<IRelayTokenRepository>();
        using var hasher = CreateHasher();
        var sut = new RelayTokenService(
            NullLogger<RelayTokenService>.Instance, hasher, repo.Object, CreateOptions(config));

        await sut.IssueStreamTokenAsync("ch-1", null, null, null, null, CancellationToken.None);

        repo.Verify(r => r.InsertAsync(It.Is<RelayTokenRecord>(t =>
            t.MaxUses == 10), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task IssueStreamTokenAsync_UnlimitedMaxUses_SetsNull()
    {
        var config = new PluginConfiguration { StreamTokenMaxUses = 0 };
        var repo = new Mock<IRelayTokenRepository>();
        using var hasher = CreateHasher();
        var sut = new RelayTokenService(
            NullLogger<RelayTokenService>.Instance, hasher, repo.Object, CreateOptions(config));

        await sut.IssueStreamTokenAsync("ch-1", null, null, null, null, CancellationToken.None);

        repo.Verify(r => r.InsertAsync(It.Is<RelayTokenRecord>(t =>
            t.MaxUses == null), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task IssueImageTokenAsync_NeverExpire_IssuesOneReusableTokenPerUser()
    {
        // Default config: ImageTokenTtlMinutes = 0 -> never expire -> one stable token per user.
        var repo = new Mock<IRelayTokenRepository>();
        repo.Setup(r => r.FindByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RelayTokenRecord?)null);
        using var hasher = CreateHasher();
        var sut = new RelayTokenService(
            NullLogger<RelayTokenService>.Instance, hasher, repo.Object, CreateOptions());

        var token = await sut.IssueImageTokenAsync("imagecache/42", MediaKind.Logo, "user-1", CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(token));
        // One non-expiring, unlimited row scoped to the user and valid for any image.
        repo.Verify(r => r.InsertAsync(It.Is<RelayTokenRecord>(t =>
            t.RelayType == "image" && t.ImageId == null && t.UserId == "user-1" &&
            t.MaxUses == null && t.ExpiresAtUtc == DateTime.MaxValue), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IssueImageTokenAsync_ReusableToken_StablePerUser_DistinctAcrossUsers()
    {
        // The token must be byte-identical across calls for the same user (Jellyfin persists the URL),
        // differ between users, and be inserted exactly once per user.
        var stored = new System.Collections.Generic.List<RelayTokenRecord>();
        var repo = new Mock<IRelayTokenRepository>();
        repo.Setup(r => r.FindByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string h, CancellationToken _) => stored.Find(x => x.TokenHash == h));
        repo.Setup(r => r.InsertAsync(It.IsAny<RelayTokenRecord>(), It.IsAny<CancellationToken>()))
            .Callback<RelayTokenRecord, CancellationToken>((t, _) => stored.Add(t))
            .Returns(Task.CompletedTask);
        using var hasher = CreateHasher();
        var sut = new RelayTokenService(
            NullLogger<RelayTokenService>.Instance, hasher, repo.Object, CreateOptions());

        var a1 = await sut.IssueImageTokenAsync("imagecache/1", MediaKind.Logo, "user-A", CancellationToken.None);
        var a2 = await sut.IssueImageTokenAsync("imagecache/2", null, "user-A", CancellationToken.None);
        var b1 = await sut.IssueImageTokenAsync("imagecache/3", null, "user-B", CancellationToken.None);

        Assert.Equal(a1, a2);     // same user -> same stable token across images
        Assert.NotEqual(a1, b1);  // different users -> different tokens
        repo.Verify(r => r.InsertAsync(It.IsAny<RelayTokenRecord>(), It.IsAny<CancellationToken>()), Times.Exactly(2)); // one row per user
    }

    [Fact]
    public async Task IssueImageTokenAsync_WithTtl_IssuesPerImageToken()
    {
        var config = new PluginConfiguration { ImageTokenTtlMinutes = 30 };
        var repo = new Mock<IRelayTokenRepository>();
        using var hasher = CreateHasher();
        var sut = new RelayTokenService(
            NullLogger<RelayTokenService>.Instance, hasher, repo.Object, CreateOptions(config));

        var token = await sut.IssueImageTokenAsync("imagecache/42", MediaKind.Logo, "user-1", CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(token));
        repo.Verify(r => r.InsertAsync(It.Is<RelayTokenRecord>(t =>
            t.RelayType == "image" && t.ImageId == "imagecache/42" && t.MediaKind == "Logo" &&
            t.ExpiresAtUtc != DateTime.MaxValue), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IssueImageTokenAsync_ThrowsOnEmptyImageId()
    {
        var repo = new Mock<IRelayTokenRepository>();
        using var hasher = CreateHasher();
        var sut = new RelayTokenService(
            NullLogger<RelayTokenService>.Instance, hasher, repo.Object, CreateOptions());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.IssueImageTokenAsync("  ", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task RevokeTokenAsync_ExistingToken_CallsRevokeOnRepository()
    {
        var repo = new Mock<IRelayTokenRepository>();
        using var hasher = CreateHasher();
        var rawToken = RelayTokenHasher.GenerateRawToken();
        var hash = hasher.HashToken(rawToken);

        repo.Setup(r => r.FindByHashAsync(hash, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RelayTokenRecord { Id = 42, TokenHash = hash });

        var sut = new RelayTokenService(
            NullLogger<RelayTokenService>.Instance, hasher, repo.Object, CreateOptions());

        await sut.RevokeTokenAsync(rawToken, "manual-revoke", CancellationToken.None);

        repo.Verify(r => r.RevokeAsync(42, "manual-revoke", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RevokeTokenAsync_UnknownToken_DoesNotThrow()
    {
        var repo = new Mock<IRelayTokenRepository>();
        using var hasher = CreateHasher();
        repo.Setup(r => r.FindByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RelayTokenRecord?)null);

        var sut = new RelayTokenService(
            NullLogger<RelayTokenService>.Instance, hasher, repo.Object, CreateOptions());

        await sut.RevokeTokenAsync(RelayTokenHasher.GenerateRawToken(), "test", CancellationToken.None);

        repo.Verify(r => r.RevokeAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CleanupExpiredTokensAsync_DelegatesToRepository()
    {
        var repo = new Mock<IRelayTokenRepository>();
        using var hasher = CreateHasher();
        repo.Setup(r => r.CleanupExpiredAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(7);

        var sut = new RelayTokenService(
            NullLogger<RelayTokenService>.Instance, hasher, repo.Object, CreateOptions());

        var count = await sut.CleanupExpiredTokensAsync(CancellationToken.None);

        Assert.Equal(7, count);
    }

    [Fact]
    public async Task IssueStreamTokenAsync_SetsOptionalFields()
    {
        var repo = new Mock<IRelayTokenRepository>();
        using var hasher = CreateHasher();
        var sut = new RelayTokenService(
            NullLogger<RelayTokenService>.Instance, hasher, repo.Object, CreateOptions());

        await sut.IssueStreamTokenAsync("ch-1", "user-1", "device-1", "pass", "Auto", CancellationToken.None);

        repo.Verify(r => r.InsertAsync(It.Is<RelayTokenRecord>(t =>
            t.UserId == "user-1" &&
            t.DeviceId == "device-1" &&
            t.SelectedProfile == "pass" &&
            t.PlaybackMode == "Auto"), It.IsAny<CancellationToken>()));
    }
}
