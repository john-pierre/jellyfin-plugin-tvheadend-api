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
    /// Atomically consumes one use of the token: increments the use count and updates
    /// first/last used timestamps, but only while the max-uses limit is not yet reached.
    /// The check and increment happen in a single conditional UPDATE so concurrent
    /// validations cannot exceed the limit.
    /// </summary>
    /// <param name="id">The token record ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when a use was consumed; <c>false</c> when the limit was already reached (or the record is gone).</returns>
    Task<bool> TryConsumeUseAsync(long id, CancellationToken cancellationToken);

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

    /// <summary>
    /// Attaches stream-setup telemetry (profile resolution source, mediainfo cache outcome,
    /// setup duration) to an issued stream token, identified by its HMAC hash. The relay
    /// controller copies these values into the request metric when the stream starts.
    /// </summary>
    /// <param name="tokenHash">The HMAC hash of the issued token.</param>
    /// <param name="resolutionSource">Which level of the profile hierarchy resolved the profile.</param>
    /// <param name="mediaInfoCacheStatus">The mediainfo cache outcome (hit/miss/mismatch/restored/unknown).</param>
    /// <param name="streamSetupMs">The media source build duration in ms.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task UpdateStreamTelemetryAsync(string tokenHash, string? resolutionSource, string? mediaInfoCacheStatus, double? streamSetupMs, CancellationToken cancellationToken);

    /// <summary>
    /// Returns token counts grouped by relay type and status (active / expired / revoked).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Aggregated token statistics.</returns>
    Task<RelayTokenStatistics> GetTokenStatisticsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Revokes all non-revoked tokens. Returns the number of tokens revoked.
    /// </summary>
    /// <param name="reason">The revocation reason.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of tokens revoked.</returns>
    Task<int> RevokeAllAsync(string reason, CancellationToken cancellationToken);
}
