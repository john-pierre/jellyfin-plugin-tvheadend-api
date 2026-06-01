// Central database cleanup service — manages retention for all plugin tables.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Database;

/// <summary>
/// Central cleanup service that manages data retention for all plugin database tables.
/// Replaces per-service cleanup timers with a single coordinated approach.
/// All cleanup operations use the <see cref="DatabaseWriteCoordinator"/> for thread safety.
/// </summary>
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "All SQL uses internal constants, never user input.")]
internal sealed class DatabaseCleanupService
{
    private readonly DatabaseHealthService _healthService;
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly DatabaseWriteCoordinator _writeCoordinator;
    private readonly ILogger<DatabaseCleanupService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseCleanupService"/> class.
    /// </summary>
    /// <param name="healthService">Central database health service.</param>
    /// <param name="connectionFactory">Factory for creating SQLite connections.</param>
    /// <param name="writeCoordinator">Central write coordinator.</param>
    /// <param name="logger">Logger for cleanup events.</param>
    public DatabaseCleanupService(
        DatabaseHealthService healthService,
        DatabaseConnectionFactory connectionFactory,
        DatabaseWriteCoordinator writeCoordinator,
        ILogger<DatabaseCleanupService> logger)
    {
        _healthService = healthService ?? throw new ArgumentNullException(nameof(healthService));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _writeCoordinator = writeCoordinator ?? throw new ArgumentNullException(nameof(writeCoordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets the UTC timestamp of the last successful cleanup run, or null if none has run.
    /// </summary>
    public DateTime? LastCleanupUtc { get; private set; }

    /// <summary>
    /// Gets the UTC timestamp of the last cleanup failure, or null if no failure has occurred.
    /// </summary>
    public DateTime? LastCleanupFailureUtc { get; private set; }

    /// <summary>
    /// Gets the last cleanup failure message, or null if no failure has occurred.
    /// </summary>
    public string? LastCleanupFailureMessage { get; private set; }

    /// <summary>
    /// Runs retention cleanup on all tables using the specified retention days.
    /// </summary>
    /// <param name="statisticsRetentionDays">Days to retain viewing sessions, health transitions, and TVHeadend logs.</param>
    /// <param name="logRetentionDays">Days to retain plugin log entries.</param>
    /// <param name="metricsRetentionDays">Days to retain relay request metrics.</param>
    /// <param name="tokenRetentionDays">Days to retain expired/revoked relay tokens.</param>
    /// <param name="healthEventRetentionDays">Days to retain database health events.</param>
    public void RunCleanup(
        int statisticsRetentionDays = 30,
        int logRetentionDays = 7,
        int metricsRetentionDays = 30,
        int tokenRetentionDays = 7,
        int healthEventRetentionDays = 30)
    {
        if (!_healthService.IsAvailable)
        {
            _logger.LogDebug("Database cleanup skipped — database unavailable.");
            return;
        }

        try
        {
            using var writeLock = _writeCoordinator.AcquireWrite();
            using var connection = _connectionFactory.CreateConnection();

            var totalDeleted = 0;

            totalDeleted += CleanupTable(
                connection, "viewing_session", "start_time_utc", statisticsRetentionDays);

            totalDeleted += CleanupTable(
                connection, "health_transition", "timestamp_utc", statisticsRetentionDays);

            totalDeleted += CleanupTable(
                connection, "tvheadend_log_entry", "timestamp_utc", statisticsRetentionDays);

            totalDeleted += CleanupTable(
                connection, "plugin_log_entry", "created_at_utc", logRetentionDays);

            totalDeleted += CleanupTable(
                connection, "relay_request_metric", "created_at_utc", metricsRetentionDays);

            // Streaming telemetry tables — these grow per stream and previously had no retention.
            totalDeleted += CleanupTable(
                connection, "completed_stream_sessions", "ended_at_utc", metricsRetentionDays);

            totalDeleted += CleanupTable(
                connection, "relay_events", "timestamp_utc", metricsRetentionDays);

            // Active sessions are normally removed on finalization; prune any stale rows left by
            // an abnormal shutdown so the table cannot grow unbounded.
            totalDeleted += CleanupTable(
                connection, "active_stream_sessions", "started_at_utc", metricsRetentionDays);

            totalDeleted += CleanupExpiredTokens(connection, tokenRetentionDays);

            totalDeleted += CleanupTable(
                connection, "database_health_event", "timestamp_utc", healthEventRetentionDays);

            LastCleanupUtc = DateTime.UtcNow;

            if (totalDeleted > 0)
            {
                _logger.LogInformation("Database cleanup completed: {TotalDeleted} rows removed across all tables.", totalDeleted);
            }
            else
            {
                _logger.LogDebug("Database cleanup completed: no rows to remove.");
            }
        }
        catch (Exception ex)
        {
            LastCleanupFailureUtc = DateTime.UtcNow;
            LastCleanupFailureMessage = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            _logger.LogWarning(ex, "Database cleanup failed.");
            _healthService.RecordError(ex);
        }
    }

    private int CleanupTable(SqliteConnection connection, string tableName, string timestampColumn, int retentionDays)
    {
        if (retentionDays <= 0)
        {
            return 0;
        }

        if (!DatabaseMigrationService.TableExists(connection, tableName))
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays).ToString("o", CultureInfo.InvariantCulture);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM \"{tableName}\" WHERE \"{timestampColumn}\" < @cutoff;";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        var deleted = cmd.ExecuteNonQuery();

        if (deleted > 0)
        {
            _logger.LogInformation(
                "Cleaned up {Count} rows from {Table} older than {Days} days.",
                deleted,
                tableName,
                retentionDays);
        }

        return deleted;
    }

    private int CleanupExpiredTokens(SqliteConnection connection, int retentionDays)
    {
        if (retentionDays <= 0)
        {
            return 0;
        }

        if (!DatabaseMigrationService.TableExists(connection, "relay_token"))
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays).ToString("o", CultureInfo.InvariantCulture);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM "relay_token"
            WHERE ("expires_at_utc" < @cutoff)
               OR ("revoked" = 1 AND "revoked_at_utc" < @cutoff);
            """;
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        var deleted = cmd.ExecuteNonQuery();

        if (deleted > 0)
        {
            _logger.LogInformation(
                "Cleaned up {Count} expired/revoked relay tokens older than {Days} days.",
                deleted,
                retentionDays);
        }

        return deleted;
    }
}
