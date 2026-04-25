using System;
using Jellyfin.Plugin.TvHeadendApi.Service.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Database;

/// <summary>
/// Centralizes SQLite database path, file naming, and connection string construction.
/// The database file has been renamed from <c>viewing-statistics.db</c> to
/// <c>tvheadend_plugin.db</c> because the DB now stores much more than viewing statistics.
/// Migration from the old file name is handled by <see cref="DatabaseHealthService"/>.
/// </summary>
/// <remarks>
/// Pooling is disabled because each service uses its own <see cref="System.Threading.SemaphoreSlim"/>
/// for write serialization and the plugin only creates a few concurrent connections.
/// This avoids pooling overhead and ensures WAL checkpoint behavior is predictable.
/// </remarks>
internal sealed class DatabaseProvider
{
    /// <summary>
    /// The canonical database file name used by the plugin.
    /// </summary>
    internal const string DatabaseFileName = "tvheadend_plugin.db";

    /// <summary>
    /// The legacy database file name from versions that only tracked viewing statistics.
    /// </summary>
    internal const string LegacyDatabaseFileName = "viewing-statistics.db";

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
