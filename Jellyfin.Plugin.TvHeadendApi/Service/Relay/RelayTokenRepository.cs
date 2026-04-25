// SQLite-backed repository for relay token CRUD operations with centralized write coordination.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// SQLite-backed relay token repository. All database write access is serialized via
/// <see cref="DatabaseWriteCoordinator"/> to prevent concurrent write conflicts on SQLite.
/// Schema creation is owned by <see cref="Database.DatabaseMigrationService"/>.
/// </summary>
internal sealed class RelayTokenRepository : IRelayTokenRepository, IDisposable
{
    private readonly ILogger<RelayTokenRepository> _logger;
    private readonly DatabaseHealthService _dbHealthService;
    private readonly DatabaseWriteCoordinator _writeCoordinator;
    private readonly DbContextOptions<RelayTokenDbContext> _dbContextOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayTokenRepository"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="dbHealthService">Central database health service.</param>
    /// <param name="writeCoordinator">Central write coordinator.</param>
    /// <param name="dbContextOptions">EF Core context options.</param>
    public RelayTokenRepository(
        ILogger<RelayTokenRepository> logger,
        DatabaseHealthService dbHealthService,
        DatabaseWriteCoordinator writeCoordinator,
        DbContextOptions<RelayTokenDbContext> dbContextOptions)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dbHealthService = dbHealthService ?? throw new ArgumentNullException(nameof(dbHealthService));
        _writeCoordinator = writeCoordinator ?? throw new ArgumentNullException(nameof(writeCoordinator));
        _dbContextOptions = dbContextOptions ?? throw new ArgumentNullException(nameof(dbContextOptions));
    }

    /// <inheritdoc />
    public async Task InsertAsync(RelayTokenRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!_dbHealthService.IsAvailable)
        {
            _logger.LogWarning("Database unavailable; relay token insert skipped.");
            return;
        }

        using var writeLock = await _writeCoordinator.AcquireWriteAsync(cancellationToken).ConfigureAwait(false);
        using var db = CreateContext();
        db.RelayTokens.Add(record);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RelayTokenRecord?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        if (!_dbHealthService.IsAvailable)
        {
            return null;
        }

        using var db = CreateContext();
        return await db.RelayTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task IncrementUseCountAsync(long id, CancellationToken cancellationToken)
    {
        if (!_dbHealthService.IsAvailable)
        {
            return;
        }

        var now = DateTime.UtcNow;

        using var writeLock = await _writeCoordinator.AcquireWriteAsync(cancellationToken).ConfigureAwait(false);
        using var db = CreateContext();
        var record = await db.RelayTokens.FindAsync(new object[] { id }, cancellationToken).ConfigureAwait(false);
        if (record == null)
        {
            return;
        }

        record.UseCount++;
        record.LastUsedAtUtc = now;
        record.FirstUsedAtUtc ??= now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RevokeAsync(long id, string reason, CancellationToken cancellationToken)
    {
        if (!_dbHealthService.IsAvailable)
        {
            return;
        }

        using var writeLock = await _writeCoordinator.AcquireWriteAsync(cancellationToken).ConfigureAwait(false);
        using var db = CreateContext();
        var record = await db.RelayTokens.FindAsync(new object[] { id }, cancellationToken).ConfigureAwait(false);
        if (record == null)
        {
            return;
        }

        record.Revoked = true;
        record.RevokedAtUtc = DateTime.UtcNow;
        record.RevokedReason = reason;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CleanupExpiredAsync(DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        if (!_dbHealthService.IsAvailable)
        {
            return 0;
        }

        using var writeLock = await _writeCoordinator.AcquireWriteAsync(cancellationToken).ConfigureAwait(false);
        using var db = CreateContext();
        var expired = await db.RelayTokens
            .Where(t => t.ExpiresAtUtc < cutoffUtc || (t.Revoked && t.RevokedAtUtc < cutoffUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (expired.Count > 0)
        {
            db.RelayTokens.RemoveRange(expired);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return expired.Count;
    }

    /// <inheritdoc />
    public async Task UpdateValidationMetadataAsync(long id, string result, string? failureReason, CancellationToken cancellationToken)
    {
        if (!_dbHealthService.IsAvailable)
        {
            return;
        }

        using var writeLock = await _writeCoordinator.AcquireWriteAsync(cancellationToken).ConfigureAwait(false);
        using var db = CreateContext();
        var record = await db.RelayTokens.FindAsync(new object[] { id }, cancellationToken).ConfigureAwait(false);
        if (record == null)
        {
            return;
        }

        record.LastValidationResult = result;
        record.LastValidationFailureReason = failureReason;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // No local resources to dispose — write coordination is centralized.
    }

    private RelayTokenDbContext CreateContext() => new(_dbContextOptions);
}
