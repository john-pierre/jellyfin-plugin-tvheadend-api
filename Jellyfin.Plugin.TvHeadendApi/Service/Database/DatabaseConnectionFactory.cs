// Central SQLite connection factory — creates connections with consistent PRAGMA settings.

using System;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Database;

/// <summary>
/// Central factory for creating SQLite connections with consistent PRAGMA settings.
/// All database access must go through this factory — no ad-hoc connection strings in services.
/// </summary>
/// <remarks>
/// Pooling is disabled because write operations are serialized via <see cref="DatabaseWriteCoordinator"/>
/// and the plugin creates very few concurrent connections. This avoids pooling overhead
/// and ensures WAL checkpoint behavior is predictable.
/// </remarks>
internal sealed class DatabaseConnectionFactory
{
    private readonly DatabaseProvider _provider;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseConnectionFactory"/> class.
    /// </summary>
    /// <param name="provider">The database provider that owns path and connection string.</param>
    public DatabaseConnectionFactory(DatabaseProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>
    /// Creates a new opened SQLite connection with standard PRAGMA settings applied.
    /// The caller owns the connection and must dispose it.
    /// </summary>
    /// <returns>An opened <see cref="SqliteConnection"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the plugin data folder is not available.</exception>
    public SqliteConnection CreateConnection()
    {
        var connStr = _provider.ConnectionString
            ?? throw new InvalidOperationException("Database path is not available — plugin data folder is not initialized.");
        var connection = new SqliteConnection(connStr);
        connection.Open();
        ApplyPragmas(connection);
        return connection;
    }

    /// <summary>
    /// Creates a new opened read-only SQLite connection.
    /// </summary>
    /// <returns>An opened read-only <see cref="SqliteConnection"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the plugin data folder is not available.</exception>
    public SqliteConnection CreateReadOnlyConnection()
    {
        var dbPath = _provider.DatabasePath
            ?? throw new InvalidOperationException("Database path is not available — plugin data folder is not initialized.");
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        ApplyReadPragmas(connection);
        return connection;
    }

    private static void ApplyPragmas(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();

        // WAL mode for concurrent readers + single writer
        cmd.CommandText = "PRAGMA journal_mode = WAL;";
        cmd.ExecuteNonQuery();

        // 5 second busy timeout to handle contention
        cmd.CommandText = "PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();

        // Enable foreign keys
        cmd.CommandText = "PRAGMA foreign_keys = ON;";
        cmd.ExecuteNonQuery();

        // NORMAL synchronous — good balance of safety and speed for plugin data
        cmd.CommandText = "PRAGMA synchronous = NORMAL;";
        cmd.ExecuteNonQuery();
    }

    private static void ApplyReadPragmas(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();
    }
}
