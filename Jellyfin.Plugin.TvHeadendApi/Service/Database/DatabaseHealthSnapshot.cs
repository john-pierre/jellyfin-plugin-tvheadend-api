// Immutable snapshot of database health for dashboard display and service decision-making.

using System;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Database;

/// <summary>
/// Immutable snapshot of the plugin database health state for dashboard display.
/// </summary>
public sealed class DatabaseHealthSnapshot
{
    /// <summary>Gets the overall database health status.</summary>
    public DatabaseHealthStatus Status { get; init; } = DatabaseHealthStatus.Unknown;

    /// <summary>Gets the database file name (without path for safe display).</summary>
    public string DatabaseName { get; init; } = string.Empty;

    /// <summary>Gets a safe representation of the database path (folder name only).</summary>
    public string DatabasePathSafe { get; init; } = string.Empty;

    /// <summary>Gets the database file size in bytes, or null if unavailable.</summary>
    public long? DatabaseSizeBytes { get; init; }

    /// <summary>Gets the WAL file size in bytes, or null if not present.</summary>
    public long? WalSizeBytes { get; init; }

    /// <summary>Gets the SHM file size in bytes, or null if not present.</summary>
    public long? ShmSizeBytes { get; init; }

    /// <summary>Gets the current schema version number.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Gets the number of pending migrations that have not been applied.</summary>
    public int PendingMigrationCount { get; init; }

    /// <summary>Gets the UTC timestamp of the last integrity check.</summary>
    public DateTime? LastIntegrityCheckUtc { get; init; }

    /// <summary>Gets the UTC timestamp of the last successful database connection.</summary>
    public DateTime? LastSuccessfulConnectionUtc { get; init; }

    /// <summary>Gets the UTC timestamp of the last migration run.</summary>
    public DateTime? LastMigrationUtc { get; init; }

    /// <summary>Gets the UTC timestamp of the last database error.</summary>
    public DateTime? LastErrorUtc { get; init; }

    /// <summary>Gets the type/category of the last error.</summary>
    public string? LastErrorType { get; init; }

    /// <summary>Gets a summary of the last error message.</summary>
    public string? LastErrorMessage { get; init; }

    /// <summary>Gets the UTC timestamp of the last database recovery.</summary>
    public DateTime? LastRecoveryUtc { get; init; }

    /// <summary>Gets the total number of database recoveries since this service started.</summary>
    public int RecoveryCount { get; init; }

    /// <summary>Gets a value indicating whether the database is currently available for reads and writes.</summary>
    public bool IsAvailable { get; init; }

    /// <summary>Gets a value indicating whether the database is in a degraded state.</summary>
    public bool IsDegraded { get; init; }

    /// <summary>Gets the total number of tables in the database, or null if unavailable.</summary>
    public int? TotalTables { get; init; }

    /// <summary>Gets the viewing session row count, or null if unavailable.</summary>
    public long? ViewingSessionCount { get; init; }

    /// <summary>Gets the plugin log entry row count, or null if unavailable.</summary>
    public long? PluginLogEntryCount { get; init; }

    /// <summary>Gets the TVHeadend log entry row count, or null if unavailable.</summary>
    public long? TvheadendLogEntryCount { get; init; }

    /// <summary>Gets the relay request metric row count, or null if unavailable.</summary>
    public long? RelayRequestMetricCount { get; init; }

    /// <summary>Gets the total relay token count, or null if unavailable.</summary>
    public long? RelayTokenCount { get; init; }

    /// <summary>Gets the expired relay token count, or null if unavailable.</summary>
    public long? ExpiredRelayTokenCount { get; init; }

    /// <summary>Gets the health transition row count, or null if unavailable.</summary>
    public long? HealthTransitionCount { get; init; }

    /// <summary>Gets the UTC timestamp of the oldest log entry, or null if unavailable.</summary>
    public DateTime? OldestLogEntryUtc { get; init; }

    /// <summary>Gets the UTC timestamp of the newest log entry, or null if unavailable.</summary>
    public DateTime? NewestLogEntryUtc { get; init; }

    /// <summary>Gets the UTC timestamp when this snapshot was created.</summary>
    public DateTime SnapshotUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Gets a human-readable warning message if the database is unhealthy.</summary>
    public string? WarningMessage { get; init; }
}
