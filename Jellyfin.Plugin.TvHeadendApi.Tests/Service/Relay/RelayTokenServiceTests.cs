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
    public async Task IssueImageTokenAsync_ReturnsNonEmptyToken()
    {
        var repo = new Mock<IRelayTokenRepository>();
        using var hasher = CreateHasher();
        var sut = new RelayTokenService(
            NullLogger<RelayTokenService>.Instance, hasher, repo.Object, CreateOptions());

        var token = await sut.IssueImageTokenAsync("imagecache/42", MediaKind.Logo, null, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(token));
        repo.Verify(r => r.InsertAsync(It.Is<RelayTokenRecord>(t =>
            t.RelayType == "image" && t.ImageId == "imagecache/42" && t.MediaKind == "Logo"), It.IsAny<CancellationToken>()));
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
