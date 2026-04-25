// Central database health monitoring service — tracks availability, corruption, recovery.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Database;

/// <summary>
/// Central database health service. Monitors database availability, runs integrity checks,
/// and exposes health snapshots for the dashboard and config UI.
/// </summary>
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "All SQL uses internal constants, never user input.")]
internal sealed class DatabaseHealthService
{
    private readonly DatabaseProvider _provider;
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly DatabaseMigrationService _migrationService;
    private readonly DatabaseRecoveryService _recoveryService;
    private readonly ILogger<DatabaseHealthService> _logger;
    private readonly object _lock = new();

    private DatabaseHealthStatus _status = DatabaseHealthStatus.Unknown;
    private DateTime? _lastIntegrityCheckUtc;
    private DateTime? _lastSuccessfulConnectionUtc;
    private DateTime? _lastMigrationUtc;
    private DateTime? _lastErrorUtc;
    private string? _lastErrorType;
    private string? _lastErrorMessage;
    private int _currentSchemaVersion;
    private bool _initialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseHealthService"/> class.
    /// </summary>
    /// <param name="provider">The database path provider.</param>
    /// <param name="connectionFactory">Factory for creating SQLite connections.</param>
    /// <param name="migrationService">Service that runs schema migrations.</param>
    /// <param name="recoveryService">Service that handles corruption recovery.</param>
    /// <param name="logger">Logger for health events.</param>
    public DatabaseHealthService(
        DatabaseProvider provider,
        DatabaseConnectionFactory connectionFactory,
        DatabaseMigrationService migrationService,
        DatabaseRecoveryService recoveryService,
        ILogger<DatabaseHealthService> logger)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _migrationService = migrationService ?? throw new ArgumentNullException(nameof(migrationService));
        _recoveryService = recoveryService ?? throw new ArgumentNullException(nameof(recoveryService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets a value indicating whether the database is currently available for operations.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            lock (_lock)
            {
                return _status is DatabaseHealthStatus.Healthy
                    or DatabaseHealthStatus.Degraded
                    or DatabaseHealthStatus.Recovered;
            }
        }
    }

    /// <summary>
    /// Gets the current database health status.
    /// </summary>
    public DatabaseHealthStatus Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    /// <summary>
    /// Initializes the database: migrates old DB file name, runs integrity check,
    /// runs migrations, and sets initial health state.
    /// Must be called once during startup before any service accesses the database.
    /// </summary>
    public void Initialize()
    {
        lock (_lock)
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                EnsureDirectoryExists();
                MigrateOldDatabaseFile();
                InitializeAndCheck();
                _initialized = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database initialization failed.");
                RecordError(ex);

                if (DatabaseErrorClassifier.IsCorruption(ex))
                {
                    _status = DatabaseHealthStatus.Recovering;
                    if (_recoveryService.TryRecover())
                    {
                        _status = DatabaseHealthStatus.Recovered;
                        _initialized = true;
                        _logger.LogWarning("Database recovered after corruption during initialization.");
                    }
                    else
                    {
                        _status = DatabaseHealthStatus.Unavailable;
                    }
                }
                else
                {
                    _status = DatabaseHealthStatus.Unavailable;
                }
            }
        }
    }

    /// <summary>
    /// Records a database error for health tracking.
    /// </summary>
    /// <param name="ex">The exception that occurred.</param>
    public void RecordError(Exception ex)
    {
        lock (_lock)
        {
            _lastErrorUtc = DateTime.UtcNow;
            _lastErrorType = DatabaseErrorClassifier.Classify(ex);
            _lastErrorMessage = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;

            if (DatabaseErrorClassifier.IsCorruption(ex))
            {
                _status = DatabaseHealthStatus.Corrupt;
            }
            else if (_status == DatabaseHealthStatus.Healthy)
            {
                _status = DatabaseHealthStatus.Degraded;
            }
        }
    }

    /// <summary>
    /// Records a successful database operation.
    /// </summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            _lastSuccessfulConnectionUtc = DateTime.UtcNow;

            // If degraded, return to healthy after a successful operation
            if (_status == DatabaseHealthStatus.Degraded)
            {
                _status = DatabaseHealthStatus.Healthy;
            }
        }
    }

    /// <summary>
    /// Builds a comprehensive health snapshot for dashboard display.
    /// </summary>
    /// <returns>A snapshot of the current database health.</returns>
    public DatabaseHealthSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            var dbPath = _provider.DatabasePath;
            var dbName = Path.GetFileName(dbPath);
            var parentFolder = Path.GetFileName(Path.GetDirectoryName(dbPath) ?? string.Empty);

            long? dbSize = null;
            long? walSize = null;
            long? shmSize = null;

            try
            {
                if (File.Exists(dbPath))
                {
                    dbSize = new FileInfo(dbPath).Length;
                }

                var walPath = dbPath + "-wal";
                if (File.Exists(walPath))
                {
                    walSize = new FileInfo(walPath).Length;
                }

                var shmPath = dbPath + "-shm";
                if (File.Exists(shmPath))
                {
                    shmSize = new FileInfo(shmPath).Length;
                }
            }
            catch
            {
                // File access failed — leave sizes as null
            }

            int? totalTables = null;
            long? viewingSessionCount = null;
            long? pluginLogEntryCount = null;
            long? tvheadendLogEntryCount = null;
            long? relayRequestMetricCount = null;
            long? relayTokenCount = null;
            long? expiredRelayTokenCount = null;
            long? healthTransitionCount = null;
            DateTime? oldestLogEntry = null;
            DateTime? newestLogEntry = null;
            var pending = 0;

            if (IsAvailable)
            {
                try
                {
                    using var conn = _connectionFactory.CreateReadOnlyConnection();
                    totalTables = CountTables(conn);
                    viewingSessionCount = SafeCount(conn, "viewing_session");
                    pluginLogEntryCount = SafeCount(conn, "plugin_log_entry");
                    tvheadendLogEntryCount = SafeCount(conn, "tvheadend_log_entry");
                    relayRequestMetricCount = SafeCount(conn, "relay_request_metric");
                    relayTokenCount = SafeCount(conn, "relay_token");
                    expiredRelayTokenCount = SafeCountWhere(conn, "relay_token", "\"expires_at_utc\" < @cutoff", ("@cutoff", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));
                    healthTransitionCount = SafeCount(conn, "health_transition");
                    oldestLogEntry = SafeMinDate(conn, "plugin_log_entry", "created_at_utc");
                    newestLogEntry = SafeMaxDate(conn, "plugin_log_entry", "created_at_utc");
                    pending = _migrationService.GetPendingMigrationCount(conn);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to gather database statistics for health snapshot.");
                }
            }

            string? warningMessage = null;
            if (_status != DatabaseHealthStatus.Healthy && _status != DatabaseHealthStatus.Unknown)
            {
                warningMessage = _status switch
                {
                    DatabaseHealthStatus.Degraded =>
                        "Database health is degraded. Some statistics, logs, relay metrics or tokens may be temporarily unavailable. Check Jellyfin logs for details.",
                    DatabaseHealthStatus.Unavailable =>
                        "Database is unavailable. Statistics, logs, relay metrics, and tokens cannot be read or written. Check Jellyfin logs for details.",
                    DatabaseHealthStatus.Corrupt =>
                        "Database corruption detected. Automatic recovery will be attempted. Recent data may be lost.",
                    DatabaseHealthStatus.Recovering =>
                        "Database recovery is in progress. Please wait.",
                    DatabaseHealthStatus.Recovered =>
                        "Database was recovered from corruption. Recent operational data before recovery may be missing.",
                    _ => null,
                };
            }

            return new DatabaseHealthSnapshot
            {
                Status = _status,
                DatabaseName = dbName,
                DatabasePathSafe = parentFolder,
                DatabaseSizeBytes = dbSize,
                WalSizeBytes = walSize,
                ShmSizeBytes = shmSize,
                SchemaVersion = _currentSchemaVersion,
                PendingMigrationCount = pending,
                LastIntegrityCheckUtc = _lastIntegrityCheckUtc,
                LastSuccessfulConnectionUtc = _lastSuccessfulConnectionUtc,
                LastMigrationUtc = _lastMigrationUtc,
                LastErrorUtc = _lastErrorUtc,
                LastErrorType = _lastErrorType,
                LastErrorMessage = _lastErrorMessage,
                LastRecoveryUtc = _recoveryService.LastRecoveryUtc,
                RecoveryCount = _recoveryService.RecoveryCount,
                IsAvailable = IsAvailable,
                IsDegraded = _status is DatabaseHealthStatus.Degraded or DatabaseHealthStatus.Recovered,
                TotalTables = totalTables,
                ViewingSessionCount = viewingSessionCount,
                PluginLogEntryCount = pluginLogEntryCount,
                TvheadendLogEntryCount = tvheadendLogEntryCount,
                RelayRequestMetricCount = relayRequestMetricCount,
                RelayTokenCount = relayTokenCount,
                ExpiredRelayTokenCount = expiredRelayTokenCount,
                HealthTransitionCount = healthTransitionCount,
                OldestLogEntryUtc = oldestLogEntry,
                NewestLogEntryUtc = newestLogEntry,
                WarningMessage = warningMessage,
            };
        }
    }

    // ── Private helpers ──────────────────────────────────────────────

    private void InitializeAndCheck()
    {
        using var connection = _connectionFactory.CreateConnection();
        _lastSuccessfulConnectionUtc = DateTime.UtcNow;

        // Integrity check
        if (!_recoveryService.RunIntegrityCheck(connection))
        {
            _logger.LogWarning("Database integrity check failed. Attempting recovery.");
            connection.Close();
            _status = DatabaseHealthStatus.Recovering;

            if (_recoveryService.TryRecover())
            {
                _status = DatabaseHealthStatus.Recovered;
                // Re-open and run migrations on fresh DB
                using var fresh = _connectionFactory.CreateConnection();
                RunMigrationsInternal(fresh);
            }
            else
            {
                _status = DatabaseHealthStatus.Unavailable;
            }

            return;
        }

        _lastIntegrityCheckUtc = DateTime.UtcNow;

        // Run migrations
        RunMigrationsInternal(connection);

        // Migrate legacy data
        _migrationService.MigrateLegacyDataIfNeeded(connection);

        _status = DatabaseHealthStatus.Healthy;
    }

    private void RunMigrationsInternal(SqliteConnection connection)
    {
        var applied = _migrationService.RunMigrations(connection);
        _currentSchemaVersion = _migrationService.GetCurrentVersion(connection);

        if (applied > 0)
        {
            _lastMigrationUtc = DateTime.UtcNow;
            _logger.LogInformation(
                "Applied {Count} database migrations. Current schema version: {Version}.",
                applied,
                _currentSchemaVersion);
        }
    }

    private void MigrateOldDatabaseFile()
    {
        var newPath = _provider.DatabasePath;
        var dir = Path.GetDirectoryName(newPath);
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        var oldPath = Path.Combine(dir, "viewing-statistics.db");
        if (File.Exists(oldPath) && !File.Exists(newPath))
        {
            try
            {
                File.Move(oldPath, newPath);
                _logger.LogInformation(
                    "Migrated database file from {OldPath} to {NewPath}.",
                    "viewing-statistics.db",
                    Path.GetFileName(newPath));

                // Also move WAL and SHM if present
                MoveCompanionFile(oldPath + "-wal", newPath + "-wal");
                MoveCompanionFile(oldPath + "-shm", newPath + "-shm");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to migrate old database file. Will create new database.");
            }
        }
    }

    private static void MoveCompanionFile(string oldFile, string newFile)
    {
        if (File.Exists(oldFile) && !File.Exists(newFile))
        {
            try
            {
                File.Move(oldFile, newFile);
            }
            catch
            {
                // Non-critical — WAL/SHM will be recreated
            }
        }
    }

    private void EnsureDirectoryExists()
    {
        var dir = Path.GetDirectoryName(_provider.DatabasePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static int CountTables(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long? SafeCount(SqliteConnection conn, string tableName)
    {
        if (!DatabaseMigrationService.TableExists(conn, tableName))
        {
            return null;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{tableName}\";";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long? SafeCountWhere(SqliteConnection conn, string tableName, string whereClause, params (string Name, string Value)[] parameters)
    {
        if (!DatabaseMigrationService.TableExists(conn, tableName))
        {
            return null;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{tableName}\" WHERE {whereClause};";
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static DateTime? SafeMinDate(SqliteConnection conn, string tableName, string columnName)
    {
        if (!DatabaseMigrationService.TableExists(conn, tableName))
        {
            return null;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT MIN(\"{columnName}\") FROM \"{tableName}\";";
        var result = cmd.ExecuteScalar();
        if (result == null || result == DBNull.Value)
        {
            return null;
        }

        return DateTime.TryParse(result.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ? dt : null;
    }

    private static DateTime? SafeMaxDate(SqliteConnection conn, string tableName, string columnName)
    {
        if (!DatabaseMigrationService.TableExists(conn, tableName))
        {
            return null;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT MAX(\"{columnName}\") FROM \"{tableName}\";";
        var result = cmd.ExecuteScalar();
        if (result == null || result == DBNull.Value)
        {
            return null;
        }

        return DateTime.TryParse(result.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ? dt : null;
    }
}
