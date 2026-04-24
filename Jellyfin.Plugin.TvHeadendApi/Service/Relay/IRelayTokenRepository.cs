// Repository interface for relay token persistence operations.

using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// Provides CRUD operations for relay tokens in the SQLite database.
/// </summary>
public interface IRelayTokenRepository
{
    /// <summary>
    /// Inserts a new relay token record.
    /// </summary>
    /// <param name="record">The token record to insert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task InsertAsync(RelayTokenRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Finds a relay token record by its HMAC hash.
    /// </summary>
    /// <param name="tokenHash">The HMAC hash to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching record, or null if not found.</returns>
    Task<RelayTokenRecord?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically increments the use count and updates first/last used timestamps.
    /// </summary>
    /// <param name="id">The token record ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task IncrementUseCountAsync(long id, CancellationToken cancellationToken);

    /// <summary>
    /// Revokes a token by setting the revoked flag, timestamp, and reason.
    /// </summary>
    /// <param name="id">The token record ID.</param>
    /// <param name="reason">The revocation reason.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task RevokeAsync(long id, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes expired and revoked tokens older than the specified cutoff.
    /// </summary>
    /// <param name="cutoffUtc">UTC cutoff — tokens expired before this time are deleted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of deleted tokens.</returns>
    Task<int> CleanupExpiredAsync(DateTime cutoffUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Updates the last validation result and failure reason on a token record.
    /// </summary>
    /// <param name="id">The token record ID.</param>
    /// <param name="result">The validation result string (e.g. "valid", "rejected").</param>
    /// <param name="failureReason">The failure reason, or null on success.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task UpdateValidationMetadataAsync(long id, string result, string? failureReason, CancellationToken cancellationToken);
}
