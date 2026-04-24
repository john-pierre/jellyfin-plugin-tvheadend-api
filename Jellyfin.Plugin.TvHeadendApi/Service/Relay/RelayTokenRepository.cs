// SQLite-backed repository for relay token CRUD operations with thread-safe access.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// SQLite-backed relay token repository. All database access is serialized via a
/// <see cref="SemaphoreSlim"/> to prevent concurrent write conflicts on SQLite.
/// </summary>
internal sealed class RelayTokenRepository : IRelayTokenRepository, IDisposable
{
    private readonly ILogger<RelayTokenRepository> _logger;
    private readonly DbContextOptions<RelayTokenDbContext> _dbContextOptions;
    private readonly string _dbPath;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private bool _schemaInitialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayTokenRepository"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="dbContextOptions">EF Core context options.</param>
    /// <param name="dbPath">Path to the SQLite database file.</param>
    public RelayTokenRepository(
        ILogger<RelayTokenRepository> logger,
        DbContextOptions<RelayTokenDbContext> dbContextOptions,
        string dbPath)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dbContextOptions = dbContextOptions ?? throw new ArgumentNullException(nameof(dbContextOptions));
        _dbPath = dbPath;
    }

    /// <inheritdoc />
    public async Task InsertAsync(RelayTokenRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        await _dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var db = CreateContext();
            EnsureSchema(db);
            db.RelayTokens.Add(record);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<RelayTokenRecord?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        await _dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var db = CreateContext();
            EnsureSchema(db);
            return await db.RelayTokens
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task IncrementUseCountAsync(long id, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        await _dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var db = CreateContext();
            EnsureSchema(db);
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
        finally
        {
            _dbLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task RevokeAsync(long id, string reason, CancellationToken cancellationToken)
    {
        await _dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var db = CreateContext();
            EnsureSchema(db);
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
        finally
        {
            _dbLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<int> CleanupExpiredAsync(DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        await _dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var db = CreateContext();
            EnsureSchema(db);
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
        finally
        {
            _dbLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task UpdateValidationMetadataAsync(long id, string result, string? failureReason, CancellationToken cancellationToken)
    {
        await _dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var db = CreateContext();
            EnsureSchema(db);
            var record = await db.RelayTokens.FindAsync(new object[] { id }, cancellationToken).ConfigureAwait(false);
            if (record == null)
            {
                return;
            }

            record.LastValidationResult = result;
            record.LastValidationFailureReason = failureReason;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _dbLock.Dispose();
    }

    /// <summary>
    /// Initializes the database schema on first access.
    /// Caller must hold <see cref="_dbLock"/>.
    /// </summary>
    /// <param name="db">The context to initialize.</param>
    internal void EnsureSchema(RelayTokenDbContext db)
    {
        if (_schemaInitialized && !string.IsNullOrWhiteSpace(_dbPath) && !File.Exists(_dbPath))
        {
            _logger.LogWarning("Relay token DB file deleted at runtime. Recreating at {Path}.", _dbPath);
            _schemaInitialized = false;
        }

        if (_schemaInitialized)
        {
            return;
        }

        EnsureDirectoryExists();
        db.Database.EnsureCreated();
        if (db.Database.IsRelational())
        {
            RelayTokenDbContext.ApplySchemaIfMissing(db);
        }

        _schemaInitialized = true;
    }

    private RelayTokenDbContext CreateContext() => new(_dbContextOptions);

    private void EnsureDirectoryExists()
    {
        if (string.IsNullOrWhiteSpace(_dbPath))
        {
            return;
        }

        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }
}
