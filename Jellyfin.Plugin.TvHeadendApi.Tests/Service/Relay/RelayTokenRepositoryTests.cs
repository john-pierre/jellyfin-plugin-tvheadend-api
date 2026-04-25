// In-memory SQLite tests for RelayTokenRepository — covers all CRUD operations and edge cases.

using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Relay;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Relay;

public class RelayTokenRepositoryTests : IDisposable
{
    private readonly DbContextOptions<RelayTokenDbContext> _options;
    private readonly RelayTokenRepository _repo;

    public RelayTokenRepositoryTests()
    {
        _options = new DbContextOptionsBuilder<RelayTokenDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        // EnsureCreated needs a persistent connection for in-memory SQLite,
        // so we use a shared file-based temp DB instead.
        var dbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"relay-test-{Guid.NewGuid():N}.db");
        var fileOptions = new DbContextOptionsBuilder<RelayTokenDbContext>()
            .UseSqlite($"DataSource={dbPath}")
            .Options;

        _repo = new RelayTokenRepository(
            NullLogger<RelayTokenRepository>.Instance,
            fileOptions,
            dbPath);
    }

    public void Dispose()
    {
        _repo.Dispose();
    }

    private static RelayTokenRecord CreateStreamRecord(string hash = "abc123", string channelId = "ch-1", int ttlMinutes = 30)
    {
        var now = DateTime.UtcNow;
        return new RelayTokenRecord
        {
            TokenHash = hash,
            RelayType = "stream",
            ChannelId = channelId,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(ttlMinutes),
            MaxUses = 5,
        };
    }

    private static RelayTokenRecord CreateImageRecord(string hash = "img456", string imageId = "imagecache/1")
    {
        var now = DateTime.UtcNow;
        return new RelayTokenRecord
        {
            TokenHash = hash,
            RelayType = "image",
            ImageId = imageId,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(60),
        };
    }

    [Fact]
    public async Task InsertAsync_InsertsRecord()
    {
        var record = CreateStreamRecord();
        await _repo.InsertAsync(record, CancellationToken.None);

        var found = await _repo.FindByHashAsync("abc123", CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal("abc123", found.TokenHash);
        Assert.Equal("stream", found.RelayType);
        Assert.Equal("ch-1", found.ChannelId);
    }

    [Fact]
    public async Task InsertAsync_ThrowsOnNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _repo.InsertAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task FindByHashAsync_ReturnsNullForMissing()
    {
        var result = await _repo.FindByHashAsync("nonexistent", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task FindByHashAsync_ThrowsOnNullOrWhitespace()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _repo.FindByHashAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _repo.FindByHashAsync("", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _repo.FindByHashAsync("   ", CancellationToken.None));
    }

    [Fact]
    public async Task IncrementUseCountAsync_IncrementsAndSetsTimestamps()
    {
        var record = CreateStreamRecord("inc-hash");
        await _repo.InsertAsync(record, CancellationToken.None);
        var inserted = await _repo.FindByHashAsync("inc-hash", CancellationToken.None);

        await _repo.IncrementUseCountAsync(inserted!.Id, CancellationToken.None);
        var updated = await _repo.FindByHashAsync("inc-hash", CancellationToken.None);

        Assert.Equal(1, updated!.UseCount);
        Assert.NotNull(updated.FirstUsedAtUtc);
        Assert.NotNull(updated.LastUsedAtUtc);
    }

    [Fact]
    public async Task IncrementUseCountAsync_MultipleIncrements()
    {
        var record = CreateStreamRecord("multi-inc");
        await _repo.InsertAsync(record, CancellationToken.None);
        var inserted = await _repo.FindByHashAsync("multi-inc", CancellationToken.None);

        await _repo.IncrementUseCountAsync(inserted!.Id, CancellationToken.None);
        await _repo.IncrementUseCountAsync(inserted.Id, CancellationToken.None);
        await _repo.IncrementUseCountAsync(inserted.Id, CancellationToken.None);

        var updated = await _repo.FindByHashAsync("multi-inc", CancellationToken.None);
        Assert.Equal(3, updated!.UseCount);
    }

    [Fact]
    public async Task IncrementUseCountAsync_NonExistentId_DoesNotThrow()
    {
        await _repo.IncrementUseCountAsync(999999, CancellationToken.None);
    }

    [Fact]
    public async Task RevokeAsync_SetsRevokedFields()
    {
        var record = CreateStreamRecord("revoke-hash");
        await _repo.InsertAsync(record, CancellationToken.None);
        var inserted = await _repo.FindByHashAsync("revoke-hash", CancellationToken.None);

        await _repo.RevokeAsync(inserted!.Id, "test-reason", CancellationToken.None);
        var revoked = await _repo.FindByHashAsync("revoke-hash", CancellationToken.None);

        Assert.True(revoked!.Revoked);
        Assert.NotNull(revoked.RevokedAtUtc);
        Assert.Equal("test-reason", revoked.RevokedReason);
    }

    [Fact]
    public async Task RevokeAsync_NonExistentId_DoesNotThrow()
    {
        await _repo.RevokeAsync(999999, "reason", CancellationToken.None);
    }

    [Fact]
    public async Task CleanupExpiredAsync_RemovesExpiredTokens()
    {
        var expired = new RelayTokenRecord
        {
            TokenHash = "expired-hash",
            RelayType = "stream",
            ChannelId = "ch-x",
            CreatedAtUtc = DateTime.UtcNow.AddHours(-2),
            ExpiresAtUtc = DateTime.UtcNow.AddHours(-1),
        };
        var valid = CreateStreamRecord("valid-hash");

        await _repo.InsertAsync(expired, CancellationToken.None);
        await _repo.InsertAsync(valid, CancellationToken.None);

        var count = await _repo.CleanupExpiredAsync(DateTime.UtcNow, CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Null(await _repo.FindByHashAsync("expired-hash", CancellationToken.None));
        Assert.NotNull(await _repo.FindByHashAsync("valid-hash", CancellationToken.None));
    }

    [Fact]
    public async Task CleanupExpiredAsync_RemovesRevokedTokens()
    {
        var record = CreateStreamRecord("revoked-cleanup");
        await _repo.InsertAsync(record, CancellationToken.None);
        var inserted = await _repo.FindByHashAsync("revoked-cleanup", CancellationToken.None);

        await _repo.RevokeAsync(inserted!.Id, "cleanup-test", CancellationToken.None);

        var count = await _repo.CleanupExpiredAsync(DateTime.UtcNow.AddMinutes(1), CancellationToken.None);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CleanupExpiredAsync_EmptyDatabase_ReturnsZero()
    {
        var count = await _repo.CleanupExpiredAsync(DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task UpdateValidationMetadataAsync_SetsFields()
    {
        var record = CreateStreamRecord("meta-hash");
        await _repo.InsertAsync(record, CancellationToken.None);
        var inserted = await _repo.FindByHashAsync("meta-hash", CancellationToken.None);

        await _repo.UpdateValidationMetadataAsync(inserted!.Id, "rejected", "Expired", CancellationToken.None);
        var updated = await _repo.FindByHashAsync("meta-hash", CancellationToken.None);

        Assert.Equal("rejected", updated!.LastValidationResult);
        Assert.Equal("Expired", updated.LastValidationFailureReason);
    }

    [Fact]
    public async Task UpdateValidationMetadataAsync_NonExistentId_DoesNotThrow()
    {
        await _repo.UpdateValidationMetadataAsync(999999, "valid", null, CancellationToken.None);
    }

    [Fact]
    public async Task InsertAsync_ImageToken_PersistsCorrectly()
    {
        var record = CreateImageRecord();
        await _repo.InsertAsync(record, CancellationToken.None);

        var found = await _repo.FindByHashAsync("img456", CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal("image", found.RelayType);
        Assert.Equal("imagecache/1", found.ImageId);
    }
}
