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
    private readonly DatabaseProvider _databaseProvider;
    private DbContextOptions<RelayTokenDbContext>? _lazyDbContextOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayTokenRepository"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="dbHealthService">Central database health service.</param>
    /// <param name="writeCoordinator">Central write coordinator.</param>
    /// <param name="databaseProvider">Central database provider.</param>
    public RelayTokenRepository(
        ILogger<RelayTokenRepository> logger,
        DatabaseHealthService dbHealthService,
        DatabaseWriteCoordinator writeCoordinator,
        DatabaseProvider databaseProvider)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dbHealthService = dbHealthService ?? throw new ArgumentNullException(nameof(dbHealthService));
        _writeCoordinator = writeCoordinator ?? throw new ArgumentNullException(nameof(writeCoordinator));
        _databaseProvider = databaseProvider ?? throw new ArgumentNullException(nameof(databaseProvider));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayTokenRepository"/> class
    /// with pre-built context options for unit testing.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="dbHealthService">Central database health service.</param>
    /// <param name="writeCoordinator">Central write coordinator.</param>
    /// <param name="dbContextOptions">Pre-built EF Core context options.</param>
    internal RelayTokenRepository(
        ILogger<RelayTokenRepository> logger,
        DatabaseHealthService dbHealthService,
        DatabaseWriteCoordinator writeCoordinator,
        DbContextOptions<RelayTokenDbContext> dbContextOptions)
        : this(logger, dbHealthService, writeCoordinator, CreateNullProvider())
    {
        _lazyDbContextOptions = dbContextOptions;
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
    public async Task<bool> TryConsumeUseAsync(long id, CancellationToken cancellationToken)
    {
        if (!_dbHealthService.IsAvailable)
        {
            // Fail-open: max-uses is a defense-in-depth soft limit; when the database is down
            // the validator has already checked the (unavailable) snapshot and streaming must
            // not break because usage accounting is offline.
            return true;
        }

        var now = DateTime.UtcNow;

        using var writeLock = await _writeCoordinator.AcquireWriteAsync(cancellationToken).ConfigureAwait(false);
        using var db = CreateContext();

        // Single conditional UPDATE: the max-uses check and the increment are atomic,
        // so concurrent validations of the same token cannot exceed the limit.
        var affected = await db.RelayTokens
            .Where(t => t.Id == id && (t.MaxUses == null || t.MaxUses <= 0 || t.UseCount < t.MaxUses))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(t => t.UseCount, t => t.UseCount + 1)
                    .SetProperty(t => t.LastUsedAtUtc, now)
                    .SetProperty(t => t.FirstUsedAtUtc, t => t.FirstUsedAtUtc ?? now),
                cancellationToken)
            .ConfigureAwait(false);

        return affected > 0;
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
    public async Task UpdateStreamTelemetryAsync(string tokenHash, string? resolutionSource, string? mediaInfoCacheStatus, double? streamSetupMs, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        if (!_dbHealthService.IsAvailable)
        {
            return;
        }

        using var writeLock = await _writeCoordinator.AcquireWriteAsync(cancellationToken).ConfigureAwait(false);
        using var db = CreateContext();
        await db.RelayTokens
            .Where(t => t.TokenHash == tokenHash)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(t => t.ResolutionSource, resolutionSource)
                    .SetProperty(t => t.MediaInfoCacheStatus, mediaInfoCacheStatus)
                    .SetProperty(t => t.StreamSetupMs, streamSetupMs),
                cancellationToken)
            .ConfigureAwait(false);
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

    /// <inheritdoc />
    public async Task<RelayTokenStatistics> GetTokenStatisticsAsync(CancellationToken cancellationToken)
    {
        if (!_dbHealthService.IsAvailable)
        {
            return new RelayTokenStatistics();
        }

        using var db = CreateContext();
        var now = DateTime.UtcNow;
        var all = await db.RelayTokens.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

        return new RelayTokenStatistics
        {
            TotalCount = all.Count,
            ActiveStreamTokens = all.Count(t => t.RelayType == "stream" && !t.Revoked && t.ExpiresAtUtc > now),
            ActiveImageTokens = all.Count(t => t.RelayType == "image" && !t.Revoked && t.ExpiresAtUtc > now),
            ExpiredCount = all.Count(t => t.ExpiresAtUtc <= now && !t.Revoked),
            RevokedCount = all.Count(t => t.Revoked),
        };
    }

    /// <inheritdoc />
    public async Task<int> RevokeAllAsync(string reason, CancellationToken cancellationToken)
    {
        if (!_dbHealthService.IsAvailable)
        {
            return 0;
        }

        using var writeLock = await _writeCoordinator.AcquireWriteAsync(cancellationToken).ConfigureAwait(false);
        using var db = CreateContext();
        var now = DateTime.UtcNow;
        var active = await db.RelayTokens
            .Where(t => !t.Revoked)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var token in active)
        {
            token.Revoked = true;
            token.RevokedAtUtc = now;
            token.RevokedReason = reason;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return active.Count;
    }

    private static DatabaseProvider CreateNullProvider()
    {
        return new DatabaseProvider(new Storage.DataFolderPathProvider(() => null));
    }

    private DbContextOptions<RelayTokenDbContext> GetDbContextOptions()
    {
        return _lazyDbContextOptions ??= _databaseProvider.CreateContextOptions<RelayTokenDbContext>();
    }

    private RelayTokenDbContext CreateContext() => new(GetDbContextOptions());
}
