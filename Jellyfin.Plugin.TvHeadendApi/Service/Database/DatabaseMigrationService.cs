// Central database migration service — owns all schema creation and versioning.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Database;

/// <summary>
/// Central service that owns all database schema creation and migration.
/// Individual services must NOT create tables themselves — this service handles all DDL.
/// Migrations are idempotent and recorded in the <c>schema_version</c> table.
/// </summary>
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "All SQL uses internal constants, never user input.")]
internal sealed class DatabaseMigrationService
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly ILogger<DatabaseMigrationService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseMigrationService"/> class.
    /// </summary>
    /// <param name="connectionFactory">Factory for creating SQLite connections.</param>
    /// <param name="logger">Logger for migration events.</param>
    public DatabaseMigrationService(
        DatabaseConnectionFactory connectionFactory,
        ILogger<DatabaseMigrationService> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets the total number of migrations that are defined.
    /// </summary>
    public int TotalMigrationCount => Migrations.Count;

    /// <summary>
    /// Gets the current schema version from the database.
    /// Returns 0 if the schema_version table does not exist.
    /// </summary>
    /// <param name="connection">An open SQLite connection.</param>
    /// <returns>The latest applied schema version number.</returns>
    public int GetCurrentVersion(SqliteConnection connection)
    {
        if (!TableExists(connection, "schema_version"))
        {
            return 0;
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Runs all pending migrations in order.
    /// </summary>
    /// <param name="connection">An open SQLite connection.</param>
    /// <returns>The number of migrations applied.</returns>
    public int RunMigrations(SqliteConnection connection)
    {
        var currentVersion = GetCurrentVersion(connection);
        var applied = 0;

        foreach (var migration in Migrations)
        {
            if (migration.Version <= currentVersion)
            {
                continue;
            }

            _logger.LogInformation(
                "Applying database migration {Version}: {Name}",
                migration.Version,
                migration.Name);

            try
            {
                using var transaction = connection.BeginTransaction();

                foreach (var sql in migration.Statements)
                {
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = sql;
                    cmd.ExecuteNonQuery();
                }

                RecordMigration(connection, transaction, migration);
                transaction.Commit();
                applied++;

                _logger.LogInformation(
                    "Migration {Version} ({Name}) applied successfully.",
                    migration.Version,
                    migration.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Migration {Version} ({Name}) failed.",
                    migration.Version,
                    migration.Name);
                throw;
            }
        }

        return applied;
    }

    /// <summary>
    /// Gets the number of pending migrations.
    /// </summary>
    /// <param name="connection">An open SQLite connection.</param>
    /// <returns>The number of unapplied migrations.</returns>
    public int GetPendingMigrationCount(SqliteConnection connection)
    {
        var currentVersion = GetCurrentVersion(connection);
        var pending = 0;
        foreach (var m in Migrations)
        {
            if (m.Version > currentVersion)
            {
                pending++;
            }
        }

        return pending;
    }

    // ── Schema Helper Methods ────────────────────────────────────────

    /// <summary>
    /// Checks whether a table exists in the database.
    /// </summary>
    /// <param name="connection">An open SQLite connection.</param>
    /// <param name="tableName">The table name to check.</param>
    /// <returns><c>true</c> if the table exists; otherwise <c>false</c>.</returns>
    public static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name;";
        cmd.Parameters.AddWithValue("@name", tableName);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>
    /// Checks whether a column exists in a given table.
    /// </summary>
    /// <param name="connection">An open SQLite connection.</param>
    /// <param name="tableName">The table name.</param>
    /// <param name="columnName">The column name.</param>
    /// <returns><c>true</c> if the column exists; otherwise <c>false</c>.</returns>
    public static bool ColumnExists(SqliteConnection connection, string tableName, string columnName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{tableName}\");";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether an index exists in the database.
    /// </summary>
    /// <param name="connection">An open SQLite connection.</param>
    /// <param name="indexName">The index name.</param>
    /// <returns><c>true</c> if the index exists; otherwise <c>false</c>.</returns>
    public static bool IndexExists(SqliteConnection connection, string indexName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=@name;";
        cmd.Parameters.AddWithValue("@name", indexName);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>
    /// Adds a column to an existing table if it does not already exist.
    /// </summary>
    /// <param name="connection">An open SQLite connection.</param>
    /// <param name="tableName">The table name.</param>
    /// <param name="columnName">The column name.</param>
    /// <param name="columnDef">The column definition (e.g. "TEXT NULL").</param>
    public static void AddColumnIfMissing(SqliteConnection connection, string tableName, string columnName, string columnDef)
    {
        if (!ColumnExists(connection, tableName, columnName))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"ALTER TABLE \"{tableName}\" ADD COLUMN \"{columnName}\" {columnDef};";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Creates an index if it does not already exist.
    /// </summary>
    /// <param name="connection">An open SQLite connection.</param>
    /// <param name="sql">The full CREATE INDEX IF NOT EXISTS statement.</param>
    public static void CreateIndexIfMissing(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ── Private ──────────────────────────────────────────────────────

    private static void RecordMigration(SqliteConnection connection, SqliteTransaction transaction, MigrationDefinition migration)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO schema_version (version, applied_at_utc, name) VALUES (@v, @t, @n);";
        cmd.Parameters.AddWithValue("@v", migration.Version);
        cmd.Parameters.AddWithValue("@t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@n", migration.Name);
        cmd.ExecuteNonQuery();
    }

    // ── Migration Definitions ────────────────────────────────────────

    /// <summary>
    /// Gets all migration definitions in order. Each migration is idempotent via IF NOT EXISTS.
    /// </summary>
#pragma warning disable SA1201 // Elements should appear in the correct order — data definition section
    internal static IReadOnlyList<MigrationDefinition> Migrations { get; } = new List<MigrationDefinition>
    {
        // 001 — Bootstrap: create the schema_version table itself
        new(1, "create_schema_version", new[]
        {
            """
            CREATE TABLE IF NOT EXISTS "schema_version" (
                "id"             INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "version"        INTEGER NOT NULL,
                "applied_at_utc" TEXT    NOT NULL,
                "name"           TEXT    NOT NULL
            )
            """,
        }),

        // 002 — Viewing sessions (new lowercase_with_underscore table)
        new(2, "create_viewing_session", new[]
        {
            """
            CREATE TABLE IF NOT EXISTS "viewing_session" (
                "id"              INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "user_name"       TEXT    NOT NULL,
                "device_name"     TEXT    NOT NULL,
                "client_name"     TEXT    NOT NULL,
                "channel_name"    TEXT    NOT NULL,
                "channel_id"      TEXT    NOT NULL,
                "play_method"     TEXT    NOT NULL,
                "play_session_id" TEXT    NOT NULL,
                "start_time_utc"  TEXT    NOT NULL,
                "end_time_utc"    TEXT    NULL
            )
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "ix_viewing_session_composite" ON "viewing_session" ("user_name", "device_name", "client_name", "channel_id", "play_session_id")""",
            """CREATE INDEX IF NOT EXISTS "ix_viewing_session_user_name" ON "viewing_session" ("user_name")""",
            """CREATE INDEX IF NOT EXISTS "ix_viewing_session_device_name" ON "viewing_session" ("device_name")""",
            """CREATE INDEX IF NOT EXISTS "ix_viewing_session_start_time_utc" ON "viewing_session" ("start_time_utc")""",
            """CREATE INDEX IF NOT EXISTS "ix_viewing_session_end_time_utc" ON "viewing_session" ("end_time_utc")""",
        }),

        // 003 — Health transitions (new lowercase table)
        new(3, "create_health_transition", new[]
        {
            """
            CREATE TABLE IF NOT EXISTS "health_transition" (
                "id"                    INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "timestamp_utc"         TEXT    NOT NULL,
                "from_status"           TEXT    NOT NULL,
                "to_status"             TEXT    NOT NULL,
                "failure_reason"        TEXT    NULL,
                "response_time_ms"      INTEGER NULL,
                "consecutive_failures"  INTEGER NOT NULL DEFAULT 0
            )
            """,
            """CREATE INDEX IF NOT EXISTS "ix_health_transition_timestamp_utc" ON "health_transition" ("timestamp_utc")""",
        }),

        // 004 — TVHeadend log entries (renamed from TvhLogEntries)
        new(4, "create_tvheadend_log_entry", new[]
        {
            """
            CREATE TABLE IF NOT EXISTS "tvheadend_log_entry" (
                "id"             INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "timestamp_utc"  TEXT    NOT NULL,
                "text"           TEXT    NOT NULL
            )
            """,
            """CREATE INDEX IF NOT EXISTS "ix_tvheadend_log_entry_timestamp_utc" ON "tvheadend_log_entry" ("timestamp_utc")""",
        }),

        // 005 — Plugin log entries (new lowercase table, was plugin_log_entries)
        new(5, "create_plugin_log_entry", new[]
        {
            """
            CREATE TABLE IF NOT EXISTS "plugin_log_entry" (
                "id"              INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "created_at_utc"  TEXT    NOT NULL,
                "source"          TEXT    NOT NULL,
                "log_type"        TEXT    NOT NULL,
                "level"           TEXT    NOT NULL,
                "category"        TEXT    NULL,
                "message"         TEXT    NOT NULL,
                "exception"       TEXT    NULL,
                "event_id"        TEXT    NULL,
                "correlation_id"  TEXT    NULL,
                "channel_id"      TEXT    NULL,
                "raw_source"      TEXT    NULL,
                "raw_line_hash"   TEXT    NULL,
                "imported_at_utc" TEXT    NULL
            )
            """,
            """CREATE INDEX IF NOT EXISTS "ix_plugin_log_entry_created_at_utc" ON "plugin_log_entry" ("created_at_utc")""",
            """CREATE INDEX IF NOT EXISTS "ix_plugin_log_entry_source" ON "plugin_log_entry" ("source")""",
            """CREATE INDEX IF NOT EXISTS "ix_plugin_log_entry_level" ON "plugin_log_entry" ("level")""",
            """CREATE INDEX IF NOT EXISTS "ix_plugin_log_entry_log_type" ON "plugin_log_entry" ("log_type")""",
            """CREATE INDEX IF NOT EXISTS "ix_plugin_log_entry_category" ON "plugin_log_entry" ("category")""",
            """CREATE INDEX IF NOT EXISTS "ix_plugin_log_entry_source_created" ON "plugin_log_entry" ("source", "created_at_utc")""",
            """CREATE INDEX IF NOT EXISTS "ix_plugin_log_entry_level_created" ON "plugin_log_entry" ("level", "created_at_utc")""",
            """CREATE INDEX IF NOT EXISTS "ix_plugin_log_entry_raw_line_hash" ON "plugin_log_entry" ("raw_line_hash")""",
        }),

        // 006 — Relay request metrics (lowercase singular)
        new(6, "create_relay_request_metric", new[]
        {
            """
            CREATE TABLE IF NOT EXISTS "relay_request_metric" (
                "id"                                  INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "created_at_utc"                      TEXT    NOT NULL,
                "relay_type"                          TEXT    NOT NULL,
                "media_kind"                          TEXT    NOT NULL,
                "image_source_type"                   TEXT    NULL,
                "channel_id"                          TEXT    NULL,
                "total_duration_ms"                   REAL    NOT NULL,
                "upstream_connect_duration_ms"        REAL    NULL,
                "upstream_headers_duration_ms"        REAL    NULL,
                "first_byte_from_upstream_duration_ms" REAL   NULL,
                "first_byte_to_client_duration_ms"    REAL    NULL,
                "startup_latency_ms"                  REAL    NULL,
                "session_duration_ms"                 REAL    NULL,
                "bytes_sent"                          INTEGER NOT NULL,
                "average_bytes_per_second"            REAL    NULL,
                "upstream_status_code"                INTEGER NULL,
                "client_status_code"                  INTEGER NOT NULL,
                "final_outcome"                       TEXT    NOT NULL,
                "failure_reason"                      TEXT    NOT NULL,
                "client_cancelled"                    INTEGER NOT NULL,
                "upstream_timed_out"                  INTEGER NOT NULL,
                "cache_status"                        TEXT    NOT NULL,
                "cache_lookup_duration_ms"            REAL    NULL,
                "had_etag"                            INTEGER NOT NULL,
                "had_last_modified"                   INTEGER NOT NULL,
                "was_not_modified_304"                INTEGER NOT NULL,
                "was_range_request"                   INTEGER NOT NULL,
                "has_content_length"                  INTEGER NOT NULL,
                "content_length"                      INTEGER NULL,
                "content_type"                        TEXT    NULL,
                "request_method"                      TEXT    NOT NULL,
                "ended_by"                            TEXT    NULL,
                "startup_failed_within_5_seconds"     INTEGER NOT NULL,
                "parallel_active_stream_count_at_start" INTEGER NULL
            )
            """,
            """CREATE INDEX IF NOT EXISTS "ix_relay_request_metric_created_at_utc" ON "relay_request_metric" ("created_at_utc")""",
            """CREATE INDEX IF NOT EXISTS "ix_relay_request_metric_relay_type" ON "relay_request_metric" ("relay_type")""",
            """CREATE INDEX IF NOT EXISTS "ix_relay_request_metric_media_kind" ON "relay_request_metric" ("media_kind")""",
            """CREATE INDEX IF NOT EXISTS "ix_relay_request_metric_failure_reason" ON "relay_request_metric" ("failure_reason")""",
            """CREATE INDEX IF NOT EXISTS "ix_relay_request_metric_final_outcome" ON "relay_request_metric" ("final_outcome")""",
            """CREATE INDEX IF NOT EXISTS "ix_relay_request_metric_channel_id" ON "relay_request_metric" ("channel_id")""",
            """CREATE INDEX IF NOT EXISTS "ix_relay_request_metric_created_type" ON "relay_request_metric" ("created_at_utc", "relay_type")""",
            """CREATE INDEX IF NOT EXISTS "ix_relay_request_metric_created_failure" ON "relay_request_metric" ("created_at_utc", "failure_reason")""",
        }),

        // 007 — Relay tokens (lowercase singular)
        new(7, "create_relay_token", new[]
        {
            """
            CREATE TABLE IF NOT EXISTS "relay_token" (
                "id"                              INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "token_hash"                      TEXT    NOT NULL,
                "relay_type"                      TEXT    NOT NULL,
                "channel_id"                      TEXT    NULL,
                "image_id"                        TEXT    NULL,
                "media_kind"                      TEXT    NULL,
                "user_id"                         TEXT    NULL,
                "device_id"                       TEXT    NULL,
                "playback_mode"                   TEXT    NULL,
                "selected_profile"                TEXT    NULL,
                "created_at_utc"                  TEXT    NOT NULL,
                "expires_at_utc"                  TEXT    NOT NULL,
                "first_used_at_utc"               TEXT    NULL,
                "last_used_at_utc"                TEXT    NULL,
                "use_count"                       INTEGER NOT NULL DEFAULT 0,
                "max_uses"                        INTEGER NULL,
                "revoked"                         INTEGER NOT NULL DEFAULT 0,
                "revoked_at_utc"                  TEXT    NULL,
                "revoked_reason"                  TEXT    NULL,
                "last_validation_result"          TEXT    NULL,
                "last_validation_failure_reason"  TEXT    NULL
            )
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "ix_relay_token_token_hash" ON "relay_token" ("token_hash")""",
            """CREATE INDEX IF NOT EXISTS "ix_relay_token_expires_at_utc" ON "relay_token" ("expires_at_utc")""",
            """CREATE INDEX IF NOT EXISTS "ix_relay_token_relay_type" ON "relay_token" ("relay_type")""",
            """CREATE INDEX IF NOT EXISTS "ix_relay_token_type_channel" ON "relay_token" ("relay_type", "channel_id")""",
            """CREATE INDEX IF NOT EXISTS "ix_relay_token_type_image" ON "relay_token" ("relay_type", "image_id")""",
        }),

        // 008 — Database health event log
        new(8, "create_database_health_event", new[]
        {
            """
            CREATE TABLE IF NOT EXISTS "database_health_event" (
                "id"           INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "timestamp_utc" TEXT   NOT NULL,
                "event_type"   TEXT    NOT NULL,
                "message"      TEXT    NULL,
                "severity"     TEXT    NOT NULL DEFAULT 'information'
            )
            """,
            """CREATE INDEX IF NOT EXISTS "ix_database_health_event_timestamp_utc" ON "database_health_event" ("timestamp_utc")""",
        }),

        // 009 — Migrate data from old CamelCase tables if they exist
        new(9, "migrate_legacy_camel_case_tables", new[]
        {
            // These are executed as individual statements. The MigrateLegacyData method
            // handles the conditional logic. Here we use a simpler approach: attempt
            // inserts from old tables wrapped in error-tolerant patterns.
            // SQLite doesn't support IF EXISTS in INSERT...SELECT, but we check in code.
            "SELECT 1", // placeholder — real migration is done by MigrateLegacyDataIfNeeded
        }),

        // 010 — Migrate data from old relay_request_metrics (plural) and relay_tokens (plural)
        new(10, "migrate_legacy_plural_relay_tables", new[]
        {
            "SELECT 1", // placeholder — real migration done by MigrateLegacyDataIfNeeded
        }),
    };

    /// <summary>
    /// Migrates data from legacy CamelCase and plural tables to the new lowercase_with_underscore tables.
    /// Called after RunMigrations to handle data transfer from old schema.
    /// </summary>
    /// <param name="connection">An open SQLite connection.</param>
    public void MigrateLegacyDataIfNeeded(SqliteConnection connection)
    {
        // ViewingSessions -> viewing_session
        MigrateLegacyTable(
            connection,
            "ViewingSessions",
            "viewing_session",
            """
            INSERT OR IGNORE INTO "viewing_session" ("id", "user_name", "device_name", "client_name", "channel_name", "channel_id", "play_method", "play_session_id", "start_time_utc", "end_time_utc")
            SELECT "Id", "UserName", "DeviceName", "ClientName", "ChannelName", "ChannelId", "PlayMethod", "PlaySessionId", "StartTimeUtc", "EndTimeUtc"
            FROM "ViewingSessions"
            """);

        // HealthTransitions -> health_transition
        MigrateLegacyTable(
            connection,
            "HealthTransitions",
            "health_transition",
            """
            INSERT OR IGNORE INTO "health_transition" ("id", "timestamp_utc", "from_status", "to_status", "failure_reason", "response_time_ms", "consecutive_failures")
            SELECT "Id", "TimestampUtc", "FromStatus", "ToStatus", "FailureReason", "ResponseTimeMs", "ConsecutiveFailures"
            FROM "HealthTransitions"
            """);

        // TvhLogEntries -> tvheadend_log_entry
        MigrateLegacyTable(
            connection,
            "TvhLogEntries",
            "tvheadend_log_entry",
            """
            INSERT OR IGNORE INTO "tvheadend_log_entry" ("id", "timestamp_utc", "text")
            SELECT "Id", "TimestampUtc", "Text"
            FROM "TvhLogEntries"
            """);

        // plugin_log_entries -> plugin_log_entry
        MigrateLegacyTable(
            connection,
            "plugin_log_entries",
            "plugin_log_entry",
            """
            INSERT OR IGNORE INTO "plugin_log_entry" ("id", "created_at_utc", "source", "log_type", "level", "category", "message", "exception", "event_id", "correlation_id", "channel_id", "raw_source", "raw_line_hash", "imported_at_utc")
            SELECT "Id", "CreatedAtUtc", "Source", "LogType", "Level", "Category", "Message", "Exception", "EventId", "CorrelationId", "ChannelId", "RawSource", "RawLineHash", "ImportedAtUtc"
            FROM "plugin_log_entries"
            """);

        // relay_request_metrics -> relay_request_metric (plural to singular)
        MigrateLegacyTable(
            connection,
            "relay_request_metrics",
            "relay_request_metric",
            """
            INSERT OR IGNORE INTO "relay_request_metric" ("id", "created_at_utc", "relay_type", "media_kind", "image_source_type", "channel_id",
                "total_duration_ms", "upstream_connect_duration_ms", "upstream_headers_duration_ms",
                "first_byte_from_upstream_duration_ms", "first_byte_to_client_duration_ms",
                "startup_latency_ms", "session_duration_ms", "bytes_sent", "average_bytes_per_second",
                "upstream_status_code", "client_status_code", "final_outcome", "failure_reason",
                "client_cancelled", "upstream_timed_out", "cache_status", "cache_lookup_duration_ms",
                "had_etag", "had_last_modified", "was_not_modified_304", "was_range_request",
                "has_content_length", "content_length", "content_type", "request_method",
                "ended_by", "startup_failed_within_5_seconds", "parallel_active_stream_count_at_start")
            SELECT "Id", "CreatedAtUtc", "RelayType", "MediaKind", "ImageSourceType", "ChannelId",
                "TotalDurationMs", "UpstreamConnectDurationMs", "UpstreamHeadersDurationMs",
                "FirstByteFromUpstreamDurationMs", "FirstByteToClientDurationMs",
                "StartupLatencyMs", "SessionDurationMs", "BytesSent", "AverageBytesPerSecond",
                "UpstreamStatusCode", "ClientStatusCode", "FinalOutcome", "FailureReason",
                "ClientCancelled", "UpstreamTimedOut", "CacheStatus", "CacheLookupDurationMs",
                "HadEtag", "HadLastModified", "WasNotModified304", "WasRangeRequest",
                "HasContentLength", "ContentLength", "ContentType", "RequestMethod",
                "EndedBy", "StartupFailedWithin5Seconds", "ParallelActiveStreamCountAtStart"
            FROM "relay_request_metrics"
            """);

        // relay_tokens -> relay_token (plural to singular)
        MigrateLegacyTable(
            connection,
            "relay_tokens",
            "relay_token",
            """
            INSERT OR IGNORE INTO "relay_token" ("id", "token_hash", "relay_type", "channel_id", "image_id",
                "media_kind", "user_id", "device_id", "playback_mode", "selected_profile",
                "created_at_utc", "expires_at_utc", "first_used_at_utc", "last_used_at_utc",
                "use_count", "max_uses", "revoked", "revoked_at_utc", "revoked_reason",
                "last_validation_result", "last_validation_failure_reason")
            SELECT "Id", "TokenHash", "RelayType", "ChannelId", "ImageId",
                "MediaKind", "UserId", "DeviceId", "PlaybackMode", "SelectedProfile",
                "CreatedAtUtc", "ExpiresAtUtc", "FirstUsedAtUtc", "LastUsedAtUtc",
                "UseCount", "MaxUses", "Revoked", "RevokedAtUtc", "RevokedReason",
                "LastValidationResult", "LastValidationFailureReason"
            FROM "relay_tokens"
            """);
    }

    private void MigrateLegacyTable(SqliteConnection connection, string oldTable, string newTable, string migrationSql)
    {
        if (!TableExists(connection, oldTable))
        {
            return;
        }

        if (!TableExists(connection, newTable))
        {
            _logger.LogWarning(
                "Legacy table {OldTable} exists but target {NewTable} not created yet — skipping data migration.",
                oldTable,
                newTable);
            return;
        }

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = migrationSql;
            var rowsMigrated = cmd.ExecuteNonQuery();
            _logger.LogInformation(
                "Migrated {Rows} rows from legacy table {OldTable} to {NewTable}.",
                rowsMigrated,
                oldTable,
                newTable);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to migrate data from {OldTable} to {NewTable}. Old data preserved in legacy table.",
                oldTable,
                newTable);
        }
    }
}

/// <summary>
/// Defines a single database migration with version, name, and SQL statements.
/// </summary>
internal sealed class MigrationDefinition
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MigrationDefinition"/> class.
    /// </summary>
    /// <param name="version">Sequential version number.</param>
    /// <param name="name">Human-readable migration name.</param>
    /// <param name="statements">SQL statements to execute.</param>
    public MigrationDefinition(int version, string name, string[] statements)
    {
        Version = version;
        Name = name;
        Statements = statements;
    }

    /// <summary>Gets the sequential version number.</summary>
    public int Version { get; }

    /// <summary>Gets the human-readable migration name.</summary>
    public string Name { get; }

    /// <summary>Gets the SQL statements for this migration.</summary>
    public string[] Statements { get; }
}
