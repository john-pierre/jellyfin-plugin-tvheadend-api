// Relay token validator — validates tokens at public relay endpoints with scope and policy checks.

using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// Validates relay tokens presented at public endpoints. Checks existence, expiry, revocation,
/// scope, relay type, and max uses. Increments use count atomically on success.
/// </summary>
internal sealed class RelayTokenValidatorService : IRelayTokenValidator
{
    private readonly ILogger<RelayTokenValidatorService> _logger;
    private readonly RelayTokenHasher _hasher;
    private readonly IRelayTokenRepository _repository;
    private readonly RelayTokenOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayTokenValidatorService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="hasher">Token hasher.</param>
    /// <param name="repository">Token repository.</param>
    /// <param name="options">Token policy options.</param>
    public RelayTokenValidatorService(
        ILogger<RelayTokenValidatorService> logger,
        RelayTokenHasher hasher,
        IRelayTokenRepository repository,
        RelayTokenOptions options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task<RelayTokenValidationResult> ValidateAsync(
        string? rawToken,
        RelayType expectedType,
        string? expectedResourceId,
        CancellationToken cancellationToken)
    {
        // Fast path: security disabled
        if (!_options.Enabled)
        {
            return RelayTokenValidationResult.SecurityDisabledResult();
        }

        // Step 1: Check token presence
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            _logger.LogDebug("Relay token validation failed: missing token");
            return RelayTokenValidationResult.Failure(RelayTokenFailureReason.MissingToken);
        }

        // Step 2: Check well-formedness
        if (!RelayTokenHasher.IsWellFormed(rawToken))
        {
            _logger.LogDebug("Relay token validation failed: malformed token");
            return RelayTokenValidationResult.Failure(RelayTokenFailureReason.MalformedToken);
        }

        try
        {
            // Step 3: Hash and lookup
            var tokenHash = _hasher.HashToken(rawToken);
            var record = await _repository.FindByHashAsync(tokenHash, cancellationToken).ConfigureAwait(false);
            if (record == null)
            {
                _logger.LogDebug("Relay token validation failed: token not found (hash prefix: {HashPrefix})", tokenHash[..8]);
                return RelayTokenValidationResult.Failure(RelayTokenFailureReason.TokenNotFound);
            }

            // Step 4: Check expiry (with clock skew)
            var effectiveExpiry = record.ExpiresAtUtc.Add(_options.ClockSkew);
            if (DateTime.UtcNow > effectiveExpiry)
            {
                _logger.LogDebug("Relay token {TokenId} expired at {ExpiresAt}", record.Id, record.ExpiresAtUtc);
                await UpdateMetadataAsync(record.Id, "rejected", RelayTokenFailureReason.Expired, cancellationToken).ConfigureAwait(false);
                return RelayTokenValidationResult.Failure(RelayTokenFailureReason.Expired);
            }

            // Step 5: Check revocation
            if (record.Revoked)
            {
                _logger.LogDebug("Relay token {TokenId} is revoked", record.Id);
                await UpdateMetadataAsync(record.Id, "rejected", RelayTokenFailureReason.Revoked, cancellationToken).ConfigureAwait(false);
                return RelayTokenValidationResult.Failure(RelayTokenFailureReason.Revoked);
            }

            // Step 6: Check max uses
            if (record.MaxUses.HasValue && record.MaxUses.Value > 0 && record.UseCount >= record.MaxUses.Value)
            {
                _logger.LogDebug("Relay token {TokenId} exceeded max uses ({UseCount}/{MaxUses})", record.Id, record.UseCount, record.MaxUses);
                await UpdateMetadataAsync(record.Id, "rejected", RelayTokenFailureReason.MaxUsesExceeded, cancellationToken).ConfigureAwait(false);
                return RelayTokenValidationResult.Failure(RelayTokenFailureReason.MaxUsesExceeded);
            }

            // Step 7: Check relay type
            var expectedTypeStr = expectedType.ToString().ToLowerInvariant();
            if (!string.Equals(record.RelayType, expectedTypeStr, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Relay token {TokenId} type mismatch: expected {Expected}, got {Actual}", record.Id, expectedTypeStr, record.RelayType);
                await UpdateMetadataAsync(record.Id, "rejected", RelayTokenFailureReason.RelayTypeMismatch, cancellationToken).ConfigureAwait(false);
                return RelayTokenValidationResult.Failure(RelayTokenFailureReason.RelayTypeMismatch);
            }

            // Step 8: Check scope (if strict scope validation is enabled)
            if (_options.StrictScope && !string.IsNullOrWhiteSpace(expectedResourceId))
            {
                bool scopeMatch;
                if (expectedType == RelayType.Stream)
                {
                    scopeMatch = string.Equals(record.ChannelId, expectedResourceId, StringComparison.Ordinal);
                }
                else
                {
                    // Per-user reuse tokens have null ImageId — they are valid for any image.
                    scopeMatch = record.ImageId == null
                        || string.Equals(record.ImageId, expectedResourceId, StringComparison.Ordinal);
                }

                if (!scopeMatch)
                {
                    _logger.LogDebug("Relay token {TokenId} scope mismatch for {RelayType}", record.Id, expectedTypeStr);
                    await UpdateMetadataAsync(record.Id, "rejected", RelayTokenFailureReason.ScopeMismatch, cancellationToken).ConfigureAwait(false);
                    return RelayTokenValidationResult.Failure(RelayTokenFailureReason.ScopeMismatch);
                }
            }

            // Step 9: Increment use count
            await _repository.IncrementUseCountAsync(record.Id, cancellationToken).ConfigureAwait(false);

            // Step 10: Update validation metadata
            await UpdateMetadataAsync(record.Id, "valid", null, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Relay token {TokenId} validated successfully (use {UseCount})", record.Id, record.UseCount + 1);
            return RelayTokenValidationResult.Success(record);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error during relay token validation");
            return RelayTokenValidationResult.Failure(RelayTokenFailureReason.UnexpectedError);
        }
    }

    private async Task UpdateMetadataAsync(long id, string result, RelayTokenFailureReason? reason, CancellationToken cancellationToken)
    {
        try
        {
            await _repository.UpdateValidationMetadataAsync(id, result, reason?.ToString(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort — never let metadata update break validation.
            _logger.LogDebug(ex, "Failed to update validation metadata for token {TokenId}", id);
        }
    }
}
