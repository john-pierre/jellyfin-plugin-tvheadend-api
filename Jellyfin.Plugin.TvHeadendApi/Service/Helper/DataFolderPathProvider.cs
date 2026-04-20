using System;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Helper;

/// <summary>
/// Provides access to the plugin data-folder path without direct <see cref="Plugin.Instance"/> coupling.
/// </summary>
internal sealed class DataFolderPathProvider
{
    private readonly Func<string?> _resolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="DataFolderPathProvider"/> class.
    /// </summary>
    /// <param name="resolver">Delegate that resolves the current data-folder path.</param>
    public DataFolderPathProvider(Func<string?> resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>
    /// Gets the current plugin data-folder path, or <c>null</c> when the plugin instance is unavailable.
    /// </summary>
    public string? Path => _resolver();
}
