using System;
using Jellyfin.Plugin.TvHeadendApi.Service.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Database;

/// <summary>
/// Centralizes SQLite database path, file naming, and connection string construction.
/// The database file is <c>tvheadend_plugin.db</c> and stores all plugin operational data.
/// </summary>
/// <remarks>
/// Pooling is disabled because write operations are serialized via <see cref="DatabaseWriteCoordinator"/>
/// and the plugin creates very few concurrent connections. This avoids pooling overhead
/// and ensures WAL checkpoint behavior is predictable.
/// </remarks>
internal sealed class DatabaseProvider
{
    /// <summary>
    /// The canonical database file name used by the plugin.
    /// </summary>
    internal const string DatabaseFileName = "tvheadend_plugin.db";

    private readonly DataFolderPathProvider _pathProvider;
    private string? _cachedDatabasePath;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseProvider"/> class.
    /// </summary>
    /// <param name="pathProvider">Provider for the plugin data-folder path.</param>
    public DatabaseProvider(DataFolderPathProvider pathProvider)
    {
        _pathProvider = pathProvider ?? throw new ArgumentNullException(nameof(pathProvider));
    }

    /// <summary>
    /// Gets the full path to the plugin's SQLite database file.
    /// The path is cached on first successful resolution to prevent issues
    /// when <see cref="Plugin.Instance"/> is temporarily unavailable (e.g. during config reset).
    /// Returns <c>null</c> only if the plugin data-folder has never been available.
    /// </summary>
    public string? DatabasePath
    {
        get
        {
            if (_cachedDatabasePath != null)
            {
                return _cachedDatabasePath;
            }

            var folder = _pathProvider.Path;
            if (string.IsNullOrWhiteSpace(folder))
            {
                return null;
            }

            _cachedDatabasePath = System.IO.Path.Combine(folder, DatabaseFileName);
            return _cachedDatabasePath;
        }
    }

    /// <summary>
    /// Gets the SQLite connection string for the plugin database.
    /// Returns <c>null</c> when the database path is not available.
    /// </summary>
    public string? ConnectionString
    {
        get
        {
            var path = DatabasePath;
            if (path == null)
            {
                return null;
            }

            return new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = false,
            }.ToString();
        }
    }

    /// <summary>
    /// Creates <see cref="DbContextOptions{TContext}"/> for the specified EF Core context type.
    /// </summary>
    /// <typeparam name="TContext">The DbContext type.</typeparam>
    /// <returns>Configured context options using the plugin's shared SQLite database.</returns>
    public DbContextOptions<TContext> CreateContextOptions<TContext>()
        where TContext : DbContext
    {
        return new DbContextOptionsBuilder<TContext>()
            .UseSqlite(ConnectionString ?? $"Data Source={DatabaseFileName}")
            .Options;
    }
}
