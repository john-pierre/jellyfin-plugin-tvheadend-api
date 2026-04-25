using System;
using Jellyfin.Plugin.TvHeadendApi.Service.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Database;

/// <summary>
/// Centralizes SQLite database path and connection string construction for the plugin.
/// Eliminates repeated connection-string building across service registrations.
/// </summary>
internal sealed class DatabaseProvider
{
    private const string DatabaseFileName = "viewing-statistics.db";

    private readonly DataFolderPathProvider _pathProvider;

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
    /// </summary>
    public string DatabasePath
    {
        get
        {
            var folder = _pathProvider.Path ?? string.Empty;
            return System.IO.Path.Combine(folder, DatabaseFileName);
        }
    }

    /// <summary>
    /// Gets the SQLite connection string for the plugin database.
    /// </summary>
    public string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = false,
    }.ToString();

    /// <summary>
    /// Creates <see cref="DbContextOptions{TContext}"/> for the specified EF Core context type.
    /// </summary>
    /// <typeparam name="TContext">The DbContext type.</typeparam>
    /// <returns>Configured context options using the plugin's shared SQLite database.</returns>
    public DbContextOptions<TContext> CreateContextOptions<TContext>()
        where TContext : DbContext
    {
        return new DbContextOptionsBuilder<TContext>()
            .UseSqlite(ConnectionString)
            .Options;
    }
}
