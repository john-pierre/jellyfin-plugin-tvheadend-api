// Tests for central database migration, health, recovery, and error classification services.

using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Jellyfin.Plugin.TvHeadendApi.Service.Database;
using Jellyfin.Plugin.TvHeadendApi.Service.Storage;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Database;

public class DatabaseMigrationServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly DatabaseProvider _provider;
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly DatabaseMigrationService _migrationService;

    public DatabaseMigrationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tvh_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, DatabaseProvider.DatabaseFileName);

        var pathProvider = new DataFolderPathProvider(() => _tempDir);
        _provider = new DatabaseProvider(pathProvider);
        _connectionFactory = new DatabaseConnectionFactory(_provider);
        _migrationService = new DatabaseMigrationService(
            _connectionFactory,
            NullLogger<DatabaseMigrationService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, true); } catch { /* cleanup */ }
    }

    [Fact]
    public void RunMigrations_CreatesAllTables_OnNewDatabase()
    {
        using var conn = _connectionFactory.CreateConnection();
        var applied = _migrationService.RunMigrations(conn);

        Assert.True(applied > 0);
        Assert.True(DatabaseMigrationService.TableExists(conn, "schema_version"));
        Assert.True(DatabaseMigrationService.TableExists(conn, "viewing_session"));
        Assert.True(DatabaseMigrationService.TableExists(conn, "health_transition"));
        Assert.True(DatabaseMigrationService.TableExists(conn, "tvheadend_log_entry"));
        Assert.True(DatabaseMigrationService.TableExists(conn, "plugin_log_entry"));
        Assert.True(DatabaseMigrationService.TableExists(conn, "relay_request_metric"));
        Assert.True(DatabaseMigrationService.TableExists(conn, "relay_token"));
        Assert.True(DatabaseMigrationService.TableExists(conn, "database_health_event"));
    }

    [Fact]
    public void RunMigrations_IsIdempotent()
    {
        using var conn = _connectionFactory.CreateConnection();
        var first = _migrationService.RunMigrations(conn);
        var second = _migrationService.RunMigrations(conn);

        Assert.True(first > 0);
        Assert.Equal(0, second);
    }

    [Fact]
    public void GetCurrentVersion_ReturnsCorrectVersion()
    {
        using var conn = _connectionFactory.CreateConnection();
        Assert.Equal(0, _migrationService.GetCurrentVersion(conn));

        _migrationService.RunMigrations(conn);
        var version = _migrationService.GetCurrentVersion(conn);

        Assert.True(version > 0);
        Assert.Equal(DatabaseMigrationService.Migrations.Count, version);
    }

    [Fact]
    public void GetPendingMigrationCount_ReturnsZeroAfterMigrations()
    {
        using var conn = _connectionFactory.CreateConnection();
        _migrationService.RunMigrations(conn);

        Assert.Equal(0, _migrationService.GetPendingMigrationCount(conn));
    }

    [Fact]
    public void SchemaVersion_RecordsAllMigrations()
    {
        using var conn = _connectionFactory.CreateConnection();
        _migrationService.RunMigrations(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM schema_version;";
        var count = Convert.ToInt64(cmd.ExecuteScalar());

        Assert.Equal(DatabaseMigrationService.Migrations.Count, count);
    }

    [Fact]
    public void TableExists_ReturnsFalseForNonExistent()
    {
        using var conn = _connectionFactory.CreateConnection();
        Assert.False(DatabaseMigrationService.TableExists(conn, "nonexistent_table"));
    }

    [Fact]
    public void ColumnExists_ReturnsTrueForExistingColumn()
    {
        using var conn = _connectionFactory.CreateConnection();
        _migrationService.RunMigrations(conn);

        Assert.True(DatabaseMigrationService.ColumnExists(conn, "viewing_session", "user_name"));
        Assert.False(DatabaseMigrationService.ColumnExists(conn, "viewing_session", "nonexistent_col"));
    }

    [Fact]
    public void AddColumnIfMissing_AddsOnlyWhenMissing()
    {
        using var conn = _connectionFactory.CreateConnection();
        _migrationService.RunMigrations(conn);

        Assert.False(DatabaseMigrationService.ColumnExists(conn, "viewing_session", "test_column"));
        DatabaseMigrationService.AddColumnIfMissing(conn, "viewing_session", "test_column", "TEXT NULL");
        Assert.True(DatabaseMigrationService.ColumnExists(conn, "viewing_session", "test_column"));

        // Call again — should not throw
        DatabaseMigrationService.AddColumnIfMissing(conn, "viewing_session", "test_column", "TEXT NULL");
    }

    [Fact]
    public void AllTableNames_AreLowerCaseWithUnderscore()
    {
        foreach (var migration in DatabaseMigrationService.Migrations)
        {
            foreach (var sql in migration.Statements)
            {
                // Skip placeholder statements
                if (sql.Trim().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Check that CREATE TABLE uses lowercase_with_underscore names
                if (sql.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
                {
                    // Table name should not contain uppercase letters
                    var afterTable = sql.Split("EXISTS", StringSplitOptions.None);
                    if (afterTable.Length > 1)
                    {
                        var tablePart = afterTable[1].Trim().TrimStart('"').Split('"')[0];
                        Assert.Equal(tablePart.ToLowerInvariant(), tablePart);
                        Assert.DoesNotContain("Tvh", tablePart, StringComparison.Ordinal);
                    }
                }
            }
        }
    }
}

public class DatabaseErrorClassifierTests
{
    [Fact]
    public void Classify_CorruptionException()
    {
        var ex = new SqliteException("database disk image is malformed", 11);
        Assert.True(DatabaseErrorClassifier.IsCorruption(ex));
        Assert.Equal("Corruption", DatabaseErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_LockedDatabase()
    {
        var ex = new SqliteException("database is locked", 5);
        Assert.True(DatabaseErrorClassifier.IsLocked(ex));
        Assert.Equal("Locked", DatabaseErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_IoError()
    {
        var ex = new SqliteException("disk I/O error", 10);
        Assert.True(DatabaseErrorClassifier.IsIoError(ex));
        Assert.Equal("IoError", DatabaseErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_GenericException_ReturnsUnknown()
    {
        var ex = new InvalidOperationException("test error");
        Assert.Equal("Unknown", DatabaseErrorClassifier.Classify(ex));
    }
}

public class DatabaseHealthSnapshotTests
{
    [Fact]
    public void DefaultSnapshot_HasUnknownStatus()
    {
        var snapshot = new DatabaseHealthSnapshot();
        Assert.Equal(DatabaseHealthStatus.Unknown, snapshot.Status);
        Assert.False(snapshot.IsAvailable);
        Assert.False(snapshot.IsDegraded);
    }

    [Fact]
    public void Snapshot_WarningMessage_SetForDegraded()
    {
        var snapshot = new DatabaseHealthSnapshot
        {
            Status = DatabaseHealthStatus.Degraded,
            IsDegraded = true,
            WarningMessage = "test warning",
        };

        Assert.NotNull(snapshot.WarningMessage);
        Assert.True(snapshot.IsDegraded);
    }
}

public class DatabaseHealthServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DatabaseProvider _provider;
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly DatabaseMigrationService _migrationService;
    private readonly DatabaseRecoveryService _recoveryService;
    private readonly DatabaseHealthService _healthService;

    public DatabaseHealthServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tvh_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var pathProvider = new DataFolderPathProvider(() => _tempDir);
        _provider = new DatabaseProvider(pathProvider);
        _connectionFactory = new DatabaseConnectionFactory(_provider);
        _migrationService = new DatabaseMigrationService(
            _connectionFactory,
            NullLogger<DatabaseMigrationService>.Instance);
        _recoveryService = new DatabaseRecoveryService(
            _provider,
            _migrationService,
            _connectionFactory,
            NullLogger<DatabaseRecoveryService>.Instance);
        _healthService = new DatabaseHealthService(
            _provider,
            _connectionFactory,
            _migrationService,
            _recoveryService,
            NullLogger<DatabaseHealthService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, true); } catch { /* cleanup */ }
    }

    [Fact]
    public void Initialize_SetsHealthyStatus()
    {
        _healthService.Initialize();

        Assert.True(_healthService.IsAvailable);
        Assert.Equal(DatabaseHealthStatus.Healthy, _healthService.Status);
    }


    [Fact]
    public void GetSnapshot_ReturnsPopulatedSnapshot()
    {
        _healthService.Initialize();
        var snapshot = _healthService.GetSnapshot();

        Assert.Equal(DatabaseHealthStatus.Healthy, snapshot.Status);
        Assert.True(snapshot.IsAvailable);
        Assert.False(snapshot.IsDegraded);
        Assert.True(snapshot.SchemaVersion > 0);
        Assert.Equal(0, snapshot.PendingMigrationCount);
        Assert.NotNull(snapshot.DatabaseName);
        Assert.Contains("tvheadend_plugin.db", snapshot.DatabaseName);
        Assert.Null(snapshot.WarningMessage);
    }

    [Fact]
    public void GetSnapshot_IncludesTableCounts()
    {
        _healthService.Initialize();
        var snapshot = _healthService.GetSnapshot();

        Assert.NotNull(snapshot.ViewingSessionCount);
        Assert.Equal(0, snapshot.ViewingSessionCount);
        Assert.NotNull(snapshot.TotalTables);
        Assert.True(snapshot.TotalTables >= 8);
    }

    [Fact]
    public void RecordError_SetsDegradedStatus()
    {
        _healthService.Initialize();
        _healthService.RecordError(new InvalidOperationException("test"));

        Assert.Equal(DatabaseHealthStatus.Degraded, _healthService.Status);
        Assert.True(_healthService.IsAvailable); // Degraded is still available
    }

    [Fact]
    public void RecordSuccess_ReturnsToHealthyAfterDegraded()
    {
        _healthService.Initialize();
        _healthService.RecordError(new InvalidOperationException("test"));
        Assert.Equal(DatabaseHealthStatus.Degraded, _healthService.Status);

        _healthService.RecordSuccess();
        Assert.Equal(DatabaseHealthStatus.Healthy, _healthService.Status);
    }

    [Fact]
    public void GetSnapshot_ShowsWarning_WhenDegraded()
    {
        _healthService.Initialize();
        _healthService.RecordError(new InvalidOperationException("test error"));

        var snapshot = _healthService.GetSnapshot();
        Assert.NotNull(snapshot.WarningMessage);
        Assert.Contains("degraded", snapshot.WarningMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async System.Threading.Tasks.Task GetSnapshot_RunsConcurrentlyWithHealthRecording()
    {
        _healthService.Initialize();

        // Regression guard: GetSnapshot performs file/SQL I/O outside the health lock, so
        // concurrent O(1) health updates must complete promptly alongside snapshot polling.
        var tasks = new[]
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                for (var i = 0; i < 25; i++)
                {
                    _ = _healthService.GetSnapshot();
                }
            }),
            System.Threading.Tasks.Task.Run(() =>
            {
                for (var i = 0; i < 200; i++)
                {
                    _healthService.RecordError(new InvalidOperationException("test"));
                    _healthService.RecordSuccess();
                    _ = _healthService.IsAvailable;
                }
            }),
        };

        await System.Threading.Tasks.Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(DatabaseHealthStatus.Healthy, _healthService.Status);
    }
}

public class DatabaseCleanupServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly DatabaseHealthService _healthService;
    private readonly DatabaseWriteCoordinator _writeCoordinator = new();

    public DatabaseCleanupServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tvh_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var pathProvider = new DataFolderPathProvider(() => _tempDir);
        var provider = new DatabaseProvider(pathProvider);
        _connectionFactory = new DatabaseConnectionFactory(provider);
        var migrationService = new DatabaseMigrationService(
            _connectionFactory,
            NullLogger<DatabaseMigrationService>.Instance);
        var recoveryService = new DatabaseRecoveryService(
            provider,
            migrationService,
            _connectionFactory,
            NullLogger<DatabaseRecoveryService>.Instance);
        _healthService = new DatabaseHealthService(
            provider,
            _connectionFactory,
            migrationService,
            recoveryService,
            NullLogger<DatabaseHealthService>.Instance);
        _healthService.Initialize();
    }

    public void Dispose()
    {
        _writeCoordinator.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, true); } catch { /* cleanup */ }
    }

    [Fact]
    public void RunCleanup_KeepsTokensThatExpireLaterOnTheSameDay()
    {
        // Regression: the cutoff used to be rendered with the round-trip specifier "o"
        // ("2026-08-08T06:34:12.1234567Z") and compared as TEXT against EF-written values
        // ("2026-08-08 23:59:00.0000000"). Because ' ' sorts before 'T', every token sharing
        // the cutoff's calendar date compared as older and was deleted — including tokens
        // still valid for hours, which killed running streams mid-playback.
        InsertToken("hash-valid-today", DateTime.UtcNow.AddHours(6));

        var cleanup = new DatabaseCleanupService(
            _healthService,
            _connectionFactory,
            _writeCoordinator,
            NullLogger<DatabaseCleanupService>.Instance);

        cleanup.RunCleanup();

        using var conn = _connectionFactory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT \"token_hash\" FROM \"relay_token\";";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("hash-valid-today", reader.GetString(0));
        Assert.False(reader.Read());
    }

    [Fact]
    public void RunCleanup_RetainsExpiredTokensWithinGracePeriod()
    {
        // A token expired 5 minutes ago is inside the short retention grace; one expired
        // well past the grace window is removed. Expired tokens are worthless, so the grace
        // is only minutes (clock skew / diagnostics), not days.
        InsertToken("hash-recent", DateTime.UtcNow.AddMinutes(-5));
        InsertToken("hash-old", DateTime.UtcNow - DatabaseCleanupService.RelayTokenRetentionGrace - TimeSpan.FromMinutes(5));

        var cleanup = new DatabaseCleanupService(
            _healthService,
            _connectionFactory,
            _writeCoordinator,
            NullLogger<DatabaseCleanupService>.Instance);

        cleanup.RunCleanup();

        using var conn = _connectionFactory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT \"token_hash\" FROM \"relay_token\";";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("hash-recent", reader.GetString(0));
        Assert.False(reader.Read());
    }

    [Fact]
    public void RunCleanup_RemovesRevokedTokensPastGracePeriod()
    {
        // Not yet expired, but revoked well past the grace window — must be removed.
        InsertToken(
            "hash-revoked",
            DateTime.UtcNow.AddHours(1),
            revokedAtUtc: DateTime.UtcNow - DatabaseCleanupService.RelayTokenRetentionGrace - TimeSpan.FromMinutes(5));

        var cleanup = new DatabaseCleanupService(
            _healthService,
            _connectionFactory,
            _writeCoordinator,
            NullLogger<DatabaseCleanupService>.Instance);

        cleanup.RunCleanup();

        using var conn = _connectionFactory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM \"relay_token\";";
        Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    /// <summary>
    /// Renders a timestamp in the TEXT format Microsoft.Data.Sqlite uses for DateTime columns.
    /// </summary>
    private static string ToStoredText(DateTime value)
    {
        return value.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", System.Globalization.CultureInfo.InvariantCulture);
    }

    private void InsertToken(string tokenHash, DateTime expiresAtUtc, DateTime? revokedAtUtc = null)
    {
        using var conn = _connectionFactory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "relay_token" ("token_hash", "relay_type", "created_at_utc", "expires_at_utc", "revoked", "revoked_at_utc")
            VALUES (@hash, 'stream', @created, @expires, @revoked, @revokedAt);
            """;
        // Write timestamps the way EF Core / Microsoft.Data.Sqlite actually persist them
        // ("yyyy-MM-dd HH:mm:ss.FFFFFFF"). Writing ISO-"o" here instead would mirror the
        // cleanup service's own formatting and let a broken cutoff compare correctly against
        // equally-broken test data — the tests would pass while production deleted live tokens.
        cmd.Parameters.AddWithValue("@hash", tokenHash);
        cmd.Parameters.AddWithValue("@created", ToStoredText(DateTime.UtcNow.AddDays(-30)));
        cmd.Parameters.AddWithValue("@expires", ToStoredText(expiresAtUtc));
        cmd.Parameters.AddWithValue("@revoked", revokedAtUtc.HasValue ? 1 : 0);
        cmd.Parameters.AddWithValue(
            "@revokedAt",
            revokedAtUtc.HasValue
                ? ToStoredText(revokedAtUtc.Value)
                : (object)DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}

public class DatabaseProviderTests
{
    [Fact]
    public void DatabaseFileName_IsTvheadendPlugin()
    {
        Assert.Equal("tvheadend_plugin.db", DatabaseProvider.DatabaseFileName);
    }

    [Fact]
    public void DatabaseFileName_DoesNotContainTvh()
    {
        Assert.DoesNotContain("tvh_", DatabaseProvider.DatabaseFileName);
    }

    [Fact]
    public void DatabaseFileName_UsesLowercaseWithUnderscore()
    {
        Assert.Equal(DatabaseProvider.DatabaseFileName, DatabaseProvider.DatabaseFileName.ToLowerInvariant());
    }
}

public class DatabaseWriteCoordinatorTests : IDisposable
{
    private readonly DatabaseWriteCoordinator _coordinator = new();

    public void Dispose() => _coordinator.Dispose();

    [Fact]
    public void AcquireWrite_CanBeReleasedAndReacquired()
    {
        using (var lock1 = _coordinator.AcquireWrite())
        {
            // Lock acquired
        }

        using (var lock2 = _coordinator.AcquireWrite())
        {
            // Can re-acquire after release
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task AcquireWriteAsync_CanBeReleasedAndReacquired()
    {
        using (var lock1 = await _coordinator.AcquireWriteAsync())
        {
            // Lock acquired
        }

        using (var lock2 = await _coordinator.AcquireWriteAsync())
        {
            // Can re-acquire
        }
    }
}

