// Central database corruption detection and recovery service.

using System;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Database;

/// <summary>
/// Handles database corruption detection and recovery. Moves corrupt database files
/// to timestamped backups and recreates the schema from scratch.
/// </summary>
internal sealed class DatabaseRecoveryService
{
    private readonly DatabaseProvider _provider;
    private readonly DatabaseMigrationService _migrationService;
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly ILogger<DatabaseRecoveryService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseRecoveryService"/> class.
    /// </summary>
    /// <param name="provider">The database path provider.</param>
    /// <param name="migrationService">The migration service for recreating schema.</param>
    /// <param name="connectionFactory">The connection factory.</param>
    /// <param name="logger">Logger for recovery events.</param>
    public DatabaseRecoveryService(
        DatabaseProvider provider,
        DatabaseMigrationService migrationService,
        DatabaseConnectionFactory connectionFactory,
        ILogger<DatabaseRecoveryService> logger)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _migrationService = migrationService ?? throw new ArgumentNullException(nameof(migrationService));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets the total number of recovery attempts since startup.
    /// </summary>
    public int RecoveryCount { get; private set; }

    /// <summary>
    /// Gets the UTC timestamp of the last recovery, or null if no recovery has occurred.
    /// </summary>
    public DateTime? LastRecoveryUtc { get; private set; }

    /// <summary>
    /// Runs a PRAGMA integrity_check on the database and returns whether it passed.
    /// </summary>
    /// <param name="connection">An open SQLite connection.</param>
    /// <returns><c>true</c> if the database passes integrity check; otherwise <c>false</c>.</returns>
    public bool RunIntegrityCheck(SqliteConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            var result = cmd.ExecuteScalar()?.ToString();
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database integrity check failed with exception.");
            return false;
        }
    }

    /// <summary>
    /// Attempts to recover from a corrupt database by moving files aside and recreating.
    /// </summary>
    /// <returns><c>true</c> if recovery was successful; otherwise <c>false</c>.</returns>
    public bool TryRecover()
    {
        var dbPath = _provider.DatabasePath;
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            return false;
        }

        try
        {
            _logger.LogWarning("Starting database recovery for {Path}.", dbPath);

            // Close existing connections by forcing GC of SQLite pooled connections
            SqliteConnection.ClearAllPools();

            MoveCorruptFiles(dbPath);

            // Recreate fresh database with all migrations
            using var connection = _connectionFactory.CreateConnection();
            _migrationService.RunMigrations(connection);

            RecoveryCount++;
            LastRecoveryUtc = DateTime.UtcNow;

            _logger.LogWarning(
                "Database recovery completed. Corrupt file moved to backup. New database created at {Path}.",
                dbPath);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database recovery failed at {Path}.", dbPath);
            return false;
        }
    }

    private void MoveCorruptFiles(string dbPath)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

        MoveFileIfExists(dbPath, $"{dbPath}.corrupt.{timestamp}.db");
        MoveFileIfExists(dbPath + "-wal", $"{dbPath}-wal.corrupt.{timestamp}");
        MoveFileIfExists(dbPath + "-shm", $"{dbPath}-shm.corrupt.{timestamp}");
    }

    private void MoveFileIfExists(string source, string destination)
    {
        if (!File.Exists(source))
        {
            return;
        }

        try
        {
            File.Move(source, destination, overwrite: true);
            _logger.LogInformation("Moved corrupt database file {Source} to {Destination}.", source, destination);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to move corrupt database file {Source}.", source);
            // Try delete as fallback
            try
            {
                File.Delete(source);
                _logger.LogInformation("Deleted corrupt database file {Source} after move failed.", source);
            }
            catch (Exception deleteEx)
            {
                _logger.LogError(deleteEx, "Failed to delete corrupt database file {Source}.", source);
            }
        }
    }
}
