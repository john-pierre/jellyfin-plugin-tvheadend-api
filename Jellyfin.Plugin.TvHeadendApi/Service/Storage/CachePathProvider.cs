using System;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Storage;

/// <summary>
/// Provides access to the plugin cache path without direct <see cref="Plugin.Instance"/> coupling.
/// </summary>
internal sealed class CachePathProvider
{
    private readonly Func<string?> _resolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="CachePathProvider"/> class.
    /// </summary>
    /// <param name="resolver">Delegate that resolves the current cache path.</param>
    public CachePathProvider(Func<string?> resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>
    /// Gets the current plugin cache path, or <c>null</c> when the plugin instance is unavailable.
    /// </summary>
    public string? Path => _resolver();
}
