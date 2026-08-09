using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// A snapshot of relay image-cache size and health for the dashboard.
/// </summary>
/// <param name="Enabled">Whether image caching is currently enabled.</param>
/// <param name="FileCount">Number of cached image files.</param>
/// <param name="TotalBytes">Total size of the cache on disk, in bytes.</param>
/// <param name="RetentionDays">Configured retention period in days.</param>
/// <param name="WriteErrors">Count of cache-write errors since startup.</param>
/// <param name="ReadErrors">Count of cache-read errors since startup.</param>
internal sealed record RelayImageCacheStats(bool Enabled, long FileCount, long TotalBytes, int RetentionDays, long WriteErrors, long ReadErrors);
