// Serializes write operations across all database consumers to prevent SQLite contention.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Database;

/// <summary>
/// Serializes database write operations across all services to prevent SQLite write contention.
/// Services should use this for writes that go through the central connection factory.
/// </summary>
internal sealed class DatabaseWriteCoordinator : IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Acquires the write lock synchronously. Caller must release via the returned <see cref="IDisposable"/>.
    /// </summary>
    /// <returns>A disposable that releases the lock.</returns>
    public IDisposable AcquireWrite()
    {
        _writeLock.Wait();
        return new WriteLockRelease(_writeLock);
    }

    /// <summary>
    /// Acquires the write lock asynchronously. Caller must release via the returned <see cref="IDisposable"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A disposable that releases the lock.</returns>
    public async Task<IDisposable> AcquireWriteAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new WriteLockRelease(_writeLock);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _writeLock.Dispose();
    }

    private sealed class WriteLockRelease : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private int _disposed;

        public WriteLockRelease(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
            {
                _semaphore.Release();
            }
        }
    }
}
